using System.Security.Cryptography;
using System.Text;
using BitosX.Core.Diagnostics;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Serialization;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>
/// Default <see cref="IVaultService"/> implementation. Coordinates <see cref="VaultCryptor"/>
/// and <see cref="ILocalVaultStorage"/>.
/// </summary>
/// <remarks>
/// <para>
/// Memory handling: the master password is converted to a byte buffer (see
/// <see cref="MasterPasswordEncoding"/>), used transiently, and
/// wiped; it is NOT retained after create/unlock. The vault key is retained (as a byte array)
/// only while unlocked and is wiped on <see cref="Lock"/>. Decrypted item values live in
/// managed <see cref="string"/> objects that cannot be reliably wiped (see docs/SECURITY_MODEL.md).
/// </para>
/// <para>
/// Concurrency: operations that read or replace the stored vault run one at a time. <see cref="Lock"/>
/// never waits for them — it clears the session immediately, and an operation that was in flight
/// finishes against its own private copy of the key and then finds that the session it started in is
/// gone, so it discards its result instead of putting key material back into a locked service.
/// </para>
/// <para>
/// Consistency: every operation that replaces the stored vault first verifies that the stored vault
/// is still the one this session loaded or last wrote (see <see cref="EncryptedVaultFingerprint"/>),
/// and fails closed with <see cref="VaultChangedExternallyException"/> if it is not. Changes are
/// applied to a copy of the vault and published only after they have been persisted, so
/// <see cref="Items"/> never shows state that is not on disk.
/// </para>
/// <para>
/// <see cref="StateChanged"/> is raised while the session is held, so notifications arrive in the
/// order the transitions happened. Handlers must not block waiting on another thread.
/// </para>
/// </remarks>
public sealed class VaultService : IVaultService, IDisposable
{
    /// <summary>Minimum accepted master password length.</summary>
    public const int MinMasterPasswordLength = 8;

    /// <summary>Recommended minimum master password length (surfaced to the UI as guidance).</summary>
    public const int RecommendedMasterPasswordLength = 12;

    /// <summary>Upper bound to reject absurd input; passwords are never silently truncated.</summary>
    public const int MaxMasterPasswordLength = 1024;

    private const string NotUnlockedMessage = "The vault is not unlocked.";

    private readonly VaultCryptor _cryptor;
    private readonly ILocalVaultStorage _storage;
    private readonly Argon2Parameters _creationParameters;
    private readonly Argon2Parameters? _fallbackCreationParameters;
    private readonly ILogger<VaultService> _logger;

    // Serializes every operation that reads or replaces the stored vault.
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    // Guards the session fields below. Lock() takes only this, so it never waits on I/O or the KDF.
    private readonly Lock _sessionLock = new();

    private Vault? _vault;
    private byte[]? _vaultKey;
    private EncryptedVaultHeader? _header;
    // SHA-256 of the encrypted envelope this session last loaded or persisted. Lets every write verify
    // the on-disk vault still corresponds to the current session before replacing it.
    private byte[]? _persistedFingerprint;
    // Whether the stored vault is wrapped under the canonical encoding of the master password, as
    // opposed to an encoding used before 0.6.0. Only known once a password has opened it.
    private bool _usesCanonicalPasswordEncoding;
    // Identifies the current session; changes whenever a session is opened or cleared.
    private long _sessionId;
    private volatile VaultState _state = VaultState.NoVault;
    private volatile bool _disposed;

    public VaultService(
        VaultCryptor cryptor,
        ILocalVaultStorage storage,
        Argon2Parameters creationParameters,
        ILogger<VaultService> logger)
        : this(cryptor, storage, creationParameters, fallbackCreationParameters: null, logger)
    {
    }

    /// <param name="cryptor">The cryptography.</param>
    /// <param name="storage">Where the vault is kept.</param>
    /// <param name="creationParameters">The key-derivation settings a new vault is created with.</param>
    /// <param name="fallbackCreationParameters">
    /// The settings a new vault is created with on a device that cannot spare the memory of
    /// <paramref name="creationParameters"/> — so that a vault can be created, and then opened, on
    /// the device it is created on. Must not be stronger than <paramref name="creationParameters"/>.
    /// <see langword="null"/> for none: creating the vault then fails on such a device.
    /// </param>
    /// <param name="logger">The log.</param>
    public VaultService(
        VaultCryptor cryptor,
        ILocalVaultStorage storage,
        Argon2Parameters creationParameters,
        Argon2Parameters? fallbackCreationParameters,
        ILogger<VaultService> logger)
    {
        _cryptor = cryptor ?? throw new ArgumentNullException(nameof(cryptor));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _creationParameters = creationParameters ?? throw new ArgumentNullException(nameof(creationParameters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _creationParameters.Validate();

        if (fallbackCreationParameters is not null)
        {
            fallbackCreationParameters.Validate();
            if (!_creationParameters.IsStrongerThan(fallbackCreationParameters))
            {
                // Equal, stronger, or not comparable: there is nothing to fall back to.
                fallbackCreationParameters = null;
            }
        }

        _fallbackCreationParameters = fallbackCreationParameters;
    }

    public VaultState State => _state;

    public event EventHandler<VaultState>? StateChanged;

    /// <summary>Whether a vault key is currently held in memory. For tests of the locking guarantees.</summary>
    internal bool HoldsKeyMaterial
    {
        get
        {
            lock (_sessionLock)
            {
                return _vaultKey is not null;
            }
        }
    }

    public IReadOnlyList<VaultItem> Items
    {
        get
        {
            lock (_sessionLock)
            {
                return _vault?.Items ?? [];
            }
        }
    }

    public Argon2Parameters? KdfParameters
    {
        get
        {
            lock (_sessionLock)
            {
                return _header?.KdfParameters;
            }
        }
    }

    public bool UsesLegacyPasswordEncoding
    {
        get
        {
            lock (_sessionLock)
            {
                return _vaultKey is not null && !_usesCanonicalPasswordEncoding;
            }
        }
    }

    public async Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        if (_state == VaultState.Unlocked)
        {
            return _state;
        }

        bool exists = await _storage.ExistsAsync(cancellationToken).ConfigureAwait(false);
        lock (_sessionLock)
        {
            // The vault may have been unlocked while storage was being queried.
            if (_state != VaultState.Unlocked)
            {
                TransitionTo(exists ? VaultState.Locked : VaultState.NoVault);
            }

            return _state;
        }
    }

    /// <summary>
    /// Creates the encrypted vault with the settings for new vaults — or, on a device that cannot
    /// spare the memory they ask for, with the fallback settings. A vault is created on the device
    /// it will be opened on; settings that device cannot afford would produce a vault it cannot
    /// open. Settings shows what the vault was created with, and that it can be strengthened.
    /// </summary>
    private VaultCreationResult Create(byte[] passwordBytes, byte[] plaintext)
    {
        try
        {
            return _cryptor.Create(passwordBytes, plaintext, _creationParameters);
        }
        catch (KeyDerivationResourceException) when (_fallbackCreationParameters is not null)
        {
            VaultCreationResult created = _cryptor.Create(passwordBytes, plaintext, _fallbackCreationParameters);
            VaultLog.VaultCreatedWithLessMemory(
                _logger, _creationParameters.MemoryKiB / 1024, _fallbackCreationParameters.MemoryKiB / 1024);
            return created;
        }
    }

    public async Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        string canonicalPassword = ValidateNewMasterPassword(masterPassword);

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long sessionId = CurrentSessionId();

            if (await _storage.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("A vault already exists.");
            }

            var vault = new Vault();
            byte[] plaintext = VaultSerializer.SerializeToUtf8Bytes(vault);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(canonicalPassword);
            byte[]? vaultKey = null;
            try
            {
                // Argon2id with the settings for new vaults takes seconds and hundreds of MiB: never on
                // the caller's thread, which is the UI thread (every await before here completes at once).
                // A derivation cannot be cancelled half-way; cancellation is checked around it as before.
                VaultCreationResult created = await Task.Run(() => Create(passwordBytes, plaintext)).ConfigureAwait(false);
                vaultKey = created.VaultKey;
                await _storage.WriteAsync(created.Encrypted, cancellationToken).ConfigureAwait(false);
                byte[] fingerprint = EncryptedVaultFingerprint.Compute(created.Encrypted);

                byte[] adopted = vaultKey;
                vaultKey = null; // ownership passes to the session (or is wiped by OpenSession on failure)
                OpenSession(sessionId, vault, adopted, created.Encrypted.Header, fingerprint, canonicalEncoding: true);
                VaultLog.VaultCreated(_logger);
            }
            finally
            {
                // Only ever a key the session did not take: creation or the initial write failed.
                CryptoBuffers.Wipe(vaultKey);
                CryptoBuffers.Wipe(passwordBytes);
                CryptoBuffers.Wipe(plaintext);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long sessionId = CurrentSessionId();
            EncryptedVault encrypted = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);

            // A vault created before 0.6.0 may be keyed with another encoding of the same password.
            PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(masterPassword);
            UnlockResult unlocked;
            bool canonicalEncoding;
            try
            {
                // Off the caller's thread: the read above may have completed synchronously.
                (unlocked, canonicalEncoding) = await Task.Run(
                    () => candidates.FirstAccepted(encoding => _cryptor.Unlock(encrypted, encoding))).ConfigureAwait(false);
            }
            catch (BitosXCryptographicException)
            {
                VaultLog.VaultUnlockFailed(_logger);
                throw;
            }
            finally
            {
                candidates.Wipe();
            }

            byte[]? vaultKey = unlocked.VaultKey;
            try
            {
                Vault vault = VaultSerializer.Deserialize(unlocked.Plaintext); // validates every item
                byte[] fingerprint = EncryptedVaultFingerprint.Compute(encrypted);

                byte[] adopted = vaultKey;
                vaultKey = null; // ownership passes to the session (or is wiped by OpenSession on failure)
                OpenSession(sessionId, vault, adopted, unlocked.Header, fingerprint, canonicalEncoding);
                VaultLog.VaultUnlocked(_logger, vault.Items.Count);
            }
            finally
            {
                // Only ever a key the session did not take: the decrypted content was rejected.
                CryptoBuffers.Wipe(vaultKey);
                CryptoBuffers.Wipe(unlocked.Plaintext);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Lock()
    {
        lock (_sessionLock)
        {
            ClearSession();
            VaultLog.VaultLocked(_logger);
            TransitionTo(VaultState.Locked);
        }
    }

    public async Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        const string notUnlocked = "The vault must be unlocked to change the master password.";

        ArgumentNullException.ThrowIfNull(currentPassword);
        string canonicalNewPassword = ValidateNewMasterPassword(newPassword);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException(notUnlocked);
        }

        // Compared in canonical form: the same password typed on another keyboard is not a new one.
        if (IsSamePassword(currentPassword, canonicalNewPassword))
        {
            throw new ArgumentException("The new master password must be different from the current password.", nameof(newPassword));
        }

        byte[] newBytes = Encoding.UTF8.GetBytes(canonicalNewPassword);
        try
        {
            // Verifies the current password (authenticated unwrap) and produces a re-wrapped vault.
            await ReWrapAsync(
                notUnlocked,
                currentPassword,
                (current, encoding) => _cryptor.ChangeMasterPassword(current, encoding, newBytes),
                wrapsUnderCanonicalEncoding: true,
                VaultLog.MasterPasswordChangeFailed,
                VaultLog.MasterPasswordChanged,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptoBuffers.Wipe(newBytes);
        }
    }

    public async Task StrengthenKeyDerivationAsync(
        string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default)
    {
        const string notUnlocked = "The vault must be unlocked to change its key-derivation settings.";

        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newParameters);
        newParameters.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException(notUnlocked);
        }

        // The key is wrapped again under the canonical encoding of the same password. Only if the
        // password cannot be canonicalized is the encoding that opens the vault kept.
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(currentPassword);
        byte[]? canonical = candidates.FirstIsCanonical ? candidates.Encodings[0] : null;
        try
        {
            await ReWrapAsync(
                notUnlocked,
                currentPassword,
                (current, encoding) => _cryptor.ChangeKeyDerivation(current, encoding, canonical ?? encoding, newParameters),
                wrapsUnderCanonicalEncoding: canonical is not null,
                VaultLog.KeyDerivationStrengthenFailed,
                VaultLog.KeyDerivationStrengthened,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            candidates.Wipe();
        }
    }

    /// <summary>
    /// The shared course of the operations that wrap the same vault key again — under a new password
    /// or under stronger key-derivation settings — and carry the content over unchanged.
    /// </summary>
    /// <param name="notUnlockedMessage">The message if the vault is not unlocked.</param>
    /// <param name="currentPassword">The current master password, as typed.</param>
    /// <param name="reWrap">
    /// Produces the re-wrapped vault from the stored one and an encoding of the current password.
    /// Throws <see cref="VaultAuthenticationException"/> if that encoding does not open the vault.
    /// </param>
    /// <param name="wrapsUnderCanonicalEncoding">Whether the result is wrapped under a canonical encoding.</param>
    /// <param name="logFailure">Logs that the operation failed.</param>
    /// <param name="logSuccess">Logs that the operation succeeded.</param>
    /// <param name="cancellationToken">Honored until the write begins.</param>
    private async Task ReWrapAsync(
        string notUnlockedMessage,
        string currentPassword,
        Func<EncryptedVault, byte[], EncryptedVault> reWrap,
        bool wrapsUnderCanonicalEncoding,
        Action<ILogger> logFailure,
        Action<ILogger> logSuccess,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSnapshot session = CaptureSession(notUnlockedMessage);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Re-wrapping carries the stored content over unchanged, so the stored vault must be
                // exactly the one this session holds the key for. Otherwise the new header would wrap a
                // key the session does not have, and the next save would produce an unreadable vault.
                EncryptedVault current = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!MatchesSession(current, session))
                {
                    logFailure(_logger);
                    throw new VaultChangedExternallyException();
                }

                PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(currentPassword);
                EncryptedVault reWrapped;
                try
                {
                    (reWrapped, _) = await Task.Run(
                        () => candidates.FirstAccepted(encoding => reWrap(current, encoding))).ConfigureAwait(false);
                }
                catch (BitosXCryptographicException)
                {
                    logFailure(_logger);
                    throw;
                }
                finally
                {
                    candidates.Wipe();
                }

                // Last safe cancellation point: nothing has been written yet.
                cancellationToken.ThrowIfCancellationRequested();

                byte[] fingerprint = EncryptedVaultFingerprint.Compute(reWrapped);
                try
                {
                    await CommitAsync(reWrapped, fingerprint, session.Fingerprint).ConfigureAwait(false);
                }
                catch
                {
                    logFailure(_logger);
                    throw;
                }

                // Only after the new vault is on disk: adopt the new header so later saves use the new
                // wrapping. The retained vault key and decrypted contents are unchanged.
                lock (_sessionLock)
                {
                    if (_sessionId == session.SessionId)
                    {
                        _header = reWrapped.Header;
                        _persistedFingerprint = fingerprint;
                        _usesCanonicalPasswordEncoding = wrapsUnderCanonicalEncoding;
                    }
                }

                logSuccess(_logger);
            }
            finally
            {
                CryptoBuffers.Wipe(session.VaultKey);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        const string notUnlocked = "The vault must be unlocked to rotate the vault key.";

        ArgumentNullException.ThrowIfNull(currentPassword);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException(notUnlocked);
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSnapshot session = CaptureSession(notUnlocked);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read the current on-disk vault (cancellable — nothing has been written yet).
                EncryptedVault current = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);

                // Consistency gate: the on-disk vault must be byte-for-byte the state this session last
                // loaded or persisted. This catches BOTH a different cryptographic identity (key/header)
                // AND a same-key external content change (different content nonce/ciphertext), because
                // the fingerprint covers the whole envelope. If it changed, fail closed and write nothing.
                if (!MatchesSession(current, session))
                {
                    VaultLog.VaultKeyRotationFailed(_logger);
                    throw new VaultChangedExternallyException();
                }

                // The new key is wrapped under the canonical encoding of the same password.
                PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(currentPassword);
                byte[]? canonical = candidates.FirstIsCanonical ? candidates.Encodings[0] : null;
                VaultKeyRotationResult rotated;
                try
                {
                    // Verifies the current password (authenticated decrypt), re-encrypts under a fresh
                    // key, and self-checks. All expensive work happens here, before any write.
                    (rotated, _) = await Task.Run(
                        () => candidates.FirstAccepted(encoding => _cryptor.RotateVaultKey(current, encoding, canonical ?? encoding))).ConfigureAwait(false);
                }
                catch (BitosXCryptographicException)
                {
                    VaultLog.VaultKeyRotationFailed(_logger);
                    throw;
                }
                finally
                {
                    candidates.Wipe();
                }

                byte[]? newKey = rotated.NewVaultKey;
                try
                {
                    // Last safe cancellation point: before any destructive write. After this,
                    // cancellation is not honored so we can never end with disk = rotated vault but
                    // memory = old key/header.
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] fingerprint = EncryptedVaultFingerprint.Compute(rotated.Encrypted);
                    try
                    {
                        await CommitAsync(rotated.Encrypted, fingerprint, session.Fingerprint).ConfigureAwait(false);
                    }
                    catch
                    {
                        VaultLog.VaultKeyRotationFailed(_logger);
                        throw;
                    }

                    // Disk holds the rotated vault: adopt the new key so memory matches it.
                    lock (_sessionLock)
                    {
                        if (_sessionId == session.SessionId)
                        {
                            CryptoBuffers.Wipe(_vaultKey);
                            _vaultKey = newKey;
                            newKey = null; // ownership passed to the session
                            _header = rotated.Encrypted.Header;
                            _persistedFingerprint = fingerprint;
                            _usesCanonicalPasswordEncoding = canonical is not null;
                        }
                    }

                    VaultLog.VaultKeyRotated(_logger);
                }
                finally
                {
                    // Not adopted: the commit failed, or the vault was locked while rotating.
                    CryptoBuffers.Wipe(newKey);
                }
            }
            finally
            {
                CryptoBuffers.Wipe(session.VaultKey);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<VaultItem>> VerifyMasterPasswordAsync(
        string currentPassword, CancellationToken cancellationToken = default)
    {
        const string notUnlocked = "The vault must be unlocked to check the master password.";

        ArgumentNullException.ThrowIfNull(currentPassword);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException(notUnlocked);
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSnapshot session = CaptureSession(notUnlocked);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The same course as a master password change: the password is checked against the
                // stored vault, which therefore has to be the one this session holds. A password that
                // opens some other file says nothing about the items this session is showing.
                EncryptedVault current = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!MatchesSession(current, session))
                {
                    VaultLog.MasterPasswordVerificationFailed(_logger);
                    throw new VaultChangedExternallyException();
                }

                PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(currentPassword);
                UnlockResult opened;
                try
                {
                    // An authenticated unwrap and decrypt, as unlocking does. Off the caller's thread.
                    (opened, _) = await Task.Run(
                        () => candidates.FirstAccepted(encoding => _cryptor.Unlock(current, encoding))).ConfigureAwait(false);
                }
                catch (BitosXCryptographicException)
                {
                    VaultLog.MasterPasswordVerificationFailed(_logger);
                    throw;
                }
                finally
                {
                    candidates.Wipe();
                }

                // Only the fact that it opened is wanted.
                CryptoBuffers.Wipe(opened.VaultKey);
                CryptoBuffers.Wipe(opened.Plaintext);

                lock (_sessionLock)
                {
                    if (_sessionId != session.SessionId)
                    {
                        // Lock() was called while the password was being checked: the lock request wins.
                        throw new OperationCanceledException("The vault was locked while the master password was being checked.");
                    }
                }

                VaultLog.MasterPasswordVerified(_logger);
                return session.Vault.Items;
            }
            finally
            {
                CryptoBuffers.Wipe(session.VaultKey);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await ApplyAsync(vault => vault.Add(item), cancellationToken).ConfigureAwait(false);

        VaultLog.VaultItemAdded(_logger, item.ItemType);
        return item;
    }

    public async Task<IReadOnlyList<LoginItem>> AddLoginsAsync(
        Func<IReadOnlyList<VaultItem>, IReadOnlyList<LoginItem>> chooseItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chooseItems);

        IReadOnlyList<LoginItem> added = [];
        await ApplyAsync(
            vault =>
            {
                // A copy: what the caller is shown must not change when the items are added below.
                IReadOnlyList<VaultItem> current = [.. vault.Items];
                added = chooseItems(current)
                    ?? throw new InvalidOperationException("The items to add must not be null.");
                if (added.Count == 0)
                {
                    return false; // nothing to store: no write
                }

                vault.AddRange(added); // validates every item and id before adding the first
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (added.Count > 0)
        {
            VaultLog.VaultItemsAdded(_logger, added.Count);
        }

        return added;
    }

    public async Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        VaultItem? stored = null;
        await ApplyAsync(vault => stored = vault.Update(item), cancellationToken).ConfigureAwait(false);

        VaultLog.VaultItemUpdated(_logger, item.ItemType);
        return stored!;
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(
            vault =>
            {
                if (!vault.Remove(id))
                {
                    throw new ItemNotFoundException(id);
                }
            },
            cancellationToken).ConfigureAwait(false);

        VaultLog.VaultItemDeleted(_logger);
    }

    public async Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        VaultItem? stored = null;
        await ApplyAsync(
            vault =>
            {
                VaultItem existing = vault.Find(id) ?? throw new ItemNotFoundException(id);
                stored = vault.Update(existing with { Favorite = !existing.Favorite });
            },
            cancellationToken).ConfigureAwait(false);

        VaultLog.VaultItemUpdated(_logger, stored!.ItemType);
        return stored;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sessionLock)
        {
            // Shutting down: wipe the key without notifying subscribers that are being torn down too.
            ClearSession();
            _state = VaultState.Locked;
        }

        _operationGate.Dispose();
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the vault, persists the result, and only then
    /// publishes it. If anything fails the session keeps showing the vault that is on disk.
    /// </summary>
    private Task ApplyAsync(Action<Vault> change, CancellationToken cancellationToken)
        => ApplyAsync(
            vault =>
            {
                change(vault);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// As <see cref="ApplyAsync(Action{Vault}, CancellationToken)"/>, for a change that may turn out
    /// to change nothing: when <paramref name="change"/> returns <see langword="false"/> the copy is
    /// dropped and nothing is written.
    /// </summary>
    private async Task ApplyAsync(Func<Vault, bool> change, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSnapshot session = CaptureSession(NotUnlockedMessage);
            byte[]? plaintext = null;
            try
            {
                Vault candidate = session.Vault.Clone();
                if (!change(candidate)) // validates; rejects before anything is encrypted or written
                {
                    return;
                }

                try
                {
                    plaintext = VaultSerializer.SerializeToUtf8Bytes(candidate);
                    EncryptedVault encrypted = _cryptor.ReEncryptContent(session.Header, session.VaultKey, plaintext);
                    byte[] fingerprint = EncryptedVaultFingerprint.Compute(encrypted);

                    byte[]? storedFingerprint =
                        await VerifyStoredVaultForSaveAsync(session, cancellationToken).ConfigureAwait(false);

                    // Last safe cancellation point: nothing has been written yet.
                    cancellationToken.ThrowIfCancellationRequested();

                    await CommitAsync(encrypted, fingerprint, storedFingerprint).ConfigureAwait(false);

                    lock (_sessionLock)
                    {
                        if (_sessionId == session.SessionId)
                        {
                            _vault = candidate;
                            _persistedFingerprint = fingerprint;
                        }
                    }

                    VaultLog.VaultSaved(_logger);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    VaultLog.VaultSaveFailed(_logger);
                    throw;
                }
            }
            finally
            {
                CryptoBuffers.Wipe(session.VaultKey);
                CryptoBuffers.Wipe(plaintext);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Verifies that saving will not overwrite a vault this session does not know about. Returns the
    /// fingerprint of the stored vault, or <see langword="null"/> when no stored vault exists: a
    /// missing file has nothing to lose, and the session holds the last copy of the data, so writing
    /// it back is the safe outcome. A stored vault that differs from the session's — or that cannot be
    /// read at all — is left untouched.
    /// </summary>
    private async Task<byte[]?> VerifyStoredVaultForSaveAsync(SessionSnapshot session, CancellationToken cancellationToken)
    {
        if (!await _storage.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EncryptedVault stored;
        try
        {
            stored = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BitosXCryptographicException)
        {
            // Present but not parseable by this build (corrupted, or written by a newer version).
            throw new VaultChangedExternallyException();
        }

        if (!MatchesSession(stored, session))
        {
            throw new VaultChangedExternallyException();
        }

        return session.Fingerprint;
    }

    /// <summary>
    /// Replaces the stored vault with <paramref name="next"/> and returns only once the stored vault
    /// is known to be <paramref name="next"/>. The write is not cancellable. If it throws, the outcome
    /// is reconciled against storage rather than assumed:
    /// <list type="bullet">
    /// <item>storage still holds the previous vault → the write failure is rethrown; nothing changed.</item>
    /// <item>storage holds <paramref name="next"/> → the replacement took effect before the throw; success.</item>
    /// <item>anything else, or storage cannot be read → the session is locked and
    /// <see cref="VaultInconsistentStateException"/> is thrown.</item>
    /// </list>
    /// </summary>
    /// <param name="next">The envelope to store.</param>
    /// <param name="nextFingerprint">Fingerprint of <paramref name="next"/>.</param>
    /// <param name="previousFingerprint">
    /// Fingerprint of the vault being replaced, or <see langword="null"/> if there was none.
    /// </param>
    private async Task CommitAsync(EncryptedVault next, byte[] nextFingerprint, byte[]? previousFingerprint)
    {
        try
        {
            await _storage.WriteAsync(next, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A correct atomic store leaves the old file intact on failure, but the service must not
            // depend on that: the replacement MAY have taken effect before the throw.
            byte[]? stored = await ReadStoredFingerprintAfterFailedWriteAsync().ConfigureAwait(false);

            if (FingerprintsEqual(stored, previousFingerprint))
            {
                throw; // the previous state is intact; surface the genuine write failure
            }

            if (FingerprintsEqual(stored, nextFingerprint))
            {
                return; // the replacement took effect before the throw
            }

            LockAfterIndeterminateState();
            throw new VaultInconsistentStateException();
        }
    }

    private async Task<byte[]?> ReadStoredFingerprintAfterFailedWriteAsync()
    {
        try
        {
            if (!await _storage.ExistsAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return null;
            }

            EncryptedVault stored = await _storage.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            return EncryptedVaultFingerprint.Compute(stored);
        }
        catch
        {
            // The on-disk state cannot even be read — treat as indeterminate and fail closed.
            LockAfterIndeterminateState();
            throw new VaultInconsistentStateException();
        }
    }

    private static bool FingerprintsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool MatchesSession(EncryptedVault stored, SessionSnapshot session)
        => CryptographicOperations.FixedTimeEquals(EncryptedVaultFingerprint.Compute(stored), session.Fingerprint);

    /// <summary>Wipes all key material and locks the session after an indeterminate write outcome.</summary>
    private void LockAfterIndeterminateState()
    {
        lock (_sessionLock)
        {
            ClearSession();
            try
            {
                TransitionTo(VaultState.Locked);
            }
            catch
            {
                // The caller is already failing with VaultInconsistentStateException, which is the
                // error the user has to see; the session is locked either way.
            }
        }
    }

    /// <summary>Waits for the operation gate. The caller must release it.</summary>
    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private long CurrentSessionId()
    {
        lock (_sessionLock)
        {
            return _sessionId;
        }
    }

    /// <summary>
    /// Takes a consistent view of the unlocked session, including a private copy of the vault key, so
    /// an operation is unaffected by <see cref="Lock"/> wiping the session's key while it runs. The
    /// caller owns and must wipe <see cref="SessionSnapshot.VaultKey"/>.
    /// </summary>
    private SessionSnapshot CaptureSession(string notUnlockedMessage)
    {
        lock (_sessionLock)
        {
            if (_state != VaultState.Unlocked
                || _vault is null
                || _vaultKey is null
                || _header is null
                || _persistedFingerprint is null)
            {
                throw new InvalidOperationException(notUnlockedMessage);
            }

            return new SessionSnapshot(_sessionId, _vault, (byte[])_vaultKey.Clone(), _header, _persistedFingerprint);
        }
    }

    /// <summary>
    /// Opens an unlocked session, taking ownership of <paramref name="vaultKey"/> in every outcome:
    /// it either becomes the session key or is wiped here.
    /// </summary>
    private void OpenSession(
        long expectedSessionId,
        Vault vault,
        byte[] vaultKey,
        EncryptedVaultHeader header,
        byte[] fingerprint,
        bool canonicalEncoding)
    {
        lock (_sessionLock)
        {
            if (_sessionId != expectedSessionId)
            {
                // Lock() was called while the vault was being opened: the lock request wins.
                CryptoBuffers.Wipe(vaultKey);
                throw new OperationCanceledException("The vault was locked while it was being opened.");
            }

            ClearSession(); // wipes the key of a session being replaced, if any
            _vault = vault;
            _vaultKey = vaultKey;
            _header = header;
            _persistedFingerprint = fingerprint;
            _usesCanonicalPasswordEncoding = canonicalEncoding;

            try
            {
                TransitionTo(VaultState.Unlocked);
            }
            catch
            {
                // A subscriber could not be told the vault is open (the UI could not show it, or
                // auto-lock could not arm). Do not stay unlocked behind its back.
                ClearSession();
                try
                {
                    TransitionTo(VaultState.Locked);
                }
                catch
                {
                    // Already failing with the original error; the session is locked either way.
                }

                throw;
            }
        }
    }

    /// <summary>Wipes the vault key and drops all session state. Must be called under the session lock.</summary>
    private void ClearSession()
    {
        CryptoBuffers.Wipe(_vaultKey);
        _vaultKey = null;
        _vault = null;
        _header = null;
        _persistedFingerprint = null;
        _usesCanonicalPasswordEncoding = false;
        _sessionId++;
    }

    /// <summary>
    /// Sets the state and notifies every subscriber, even if one of them throws; the first failure is
    /// rethrown once all have been notified. Must be called under the session lock.
    /// </summary>
    private void TransitionTo(VaultState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;

        EventHandler<VaultState>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? firstFailure = null;
        foreach (EventHandler<VaultState> handler in handlers.GetInvocationList().Cast<EventHandler<VaultState>>())
        {
            try
            {
                handler(this, state);
            }
            catch (Exception ex)
            {
                firstFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        firstFailure?.Throw();
    }

    /// <summary>
    /// Validates a password that is about to protect a vault and returns its canonical form, which is
    /// what the key is derived from. The length limits apply to the canonical form.
    /// </summary>
    private static string ValidateNewMasterPassword(string masterPassword)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        // Rejects text that is not valid Unicode. Earlier versions encoded it with a replacement
        // character, which made different passwords equal.
        string canonical = MasterPasswordEncoding.Canonicalize(masterPassword);

        if (canonical.Length < MinMasterPasswordLength)
        {
            throw new ArgumentException(
                $"The master password must be at least {MinMasterPasswordLength} characters.", nameof(masterPassword));
        }

        if (canonical.Length > MaxMasterPasswordLength)
        {
            throw new ArgumentException(
                $"The master password must be at most {MaxMasterPasswordLength} characters.", nameof(masterPassword));
        }

        return canonical;
    }

    /// <summary>
    /// Whether <paramref name="password"/>, as typed, is the password whose canonical form is
    /// <paramref name="canonicalOther"/>.
    /// </summary>
    private static bool IsSamePassword(string password, string canonicalOther)
    {
        try
        {
            return string.Equals(
                MasterPasswordEncoding.Canonicalize(password), canonicalOther, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // Not valid Unicode, so it cannot equal a canonical password.
            return false;
        }
    }

    /// <summary>A consistent view of one unlocked session. <see cref="VaultKey"/> is a private copy.</summary>
    private sealed record SessionSnapshot(
        long SessionId,
        Vault Vault,
        byte[] VaultKey,
        EncryptedVaultHeader Header,
        byte[] Fingerprint);
}
