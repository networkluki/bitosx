using System.Text;
using BitosX.Core.Diagnostics;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>
/// Default <see cref="IVaultService"/> implementation. Coordinates <see cref="VaultCryptor"/>
/// and <see cref="ILocalVaultStorage"/>. Assumes single-threaded (UI-thread) access.
/// </summary>
/// <remarks>
/// Memory handling: the master password is converted to a byte buffer, used transiently, and
/// wiped; it is NOT retained after create/unlock. The vault key is retained (as a byte array)
/// only while unlocked and is wiped on <see cref="Lock"/>. Decrypted item values live in
/// managed <see cref="string"/> objects that cannot be reliably wiped (see docs/SECURITY_MODEL.md).
/// </remarks>
public sealed class VaultService : IVaultService
{
    /// <summary>Minimum accepted master password length.</summary>
    public const int MinMasterPasswordLength = 8;

    /// <summary>Recommended minimum master password length (surfaced to the UI as guidance).</summary>
    public const int RecommendedMasterPasswordLength = 12;

    /// <summary>Upper bound to reject absurd input; passwords are never silently truncated.</summary>
    public const int MaxMasterPasswordLength = 1024;

    private readonly VaultCryptor _cryptor;
    private readonly ILocalVaultStorage _storage;
    private readonly Argon2Parameters _creationParameters;
    private readonly ILogger<VaultService> _logger;

    private Vault? _vault;
    private byte[]? _vaultKey;
    private EncryptedVaultHeader? _header;
    // SHA-256 of the encrypted envelope this session last loaded or persisted. Lets destructive
    // operations verify the on-disk vault still corresponds to the current session before replacing it.
    private byte[]? _persistedFingerprint;
    private VaultState _state = VaultState.NoVault;

    public VaultService(
        VaultCryptor cryptor,
        ILocalVaultStorage storage,
        Argon2Parameters creationParameters,
        ILogger<VaultService> logger)
    {
        _cryptor = cryptor ?? throw new ArgumentNullException(nameof(cryptor));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _creationParameters = creationParameters ?? throw new ArgumentNullException(nameof(creationParameters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _creationParameters.Validate();
    }

    public VaultState State => _state;

    public event EventHandler<VaultState>? StateChanged;

    public IReadOnlyList<VaultItem> Items => _vault?.Items ?? [];

    public async Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        if (_state == VaultState.Unlocked)
        {
            return _state;
        }

        bool exists = await _storage.ExistsAsync(cancellationToken).ConfigureAwait(false);
        SetState(exists ? VaultState.Locked : VaultState.NoVault);
        return _state;
    }

    public async Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        ValidateMasterPassword(masterPassword);

        if (await _storage.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A vault already exists.");
        }

        var vault = new Vault();
        byte[] plaintext = VaultSerializer.SerializeToUtf8Bytes(vault);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        VaultCreationResult? created = null;
        try
        {
            created = _cryptor.Create(passwordBytes, plaintext, _creationParameters);
            await _storage.WriteAsync(created.Encrypted, cancellationToken).ConfigureAwait(false);

            _vault = vault;
            _vaultKey = created.VaultKey; // ownership transferred to the service
            _header = created.Encrypted.Header;
            _persistedFingerprint = EncryptedVaultFingerprint.Compute(created.Encrypted);
            SetState(VaultState.Unlocked);
            VaultLog.VaultCreated(_logger);
        }
        catch
        {
            // Creation or the initial write failed: do not leave key material dangling.
            CryptoBuffers.Wipe(created?.VaultKey);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(passwordBytes);
            CryptoBuffers.Wipe(plaintext);
        }
    }

    public async Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        EncryptedVault encrypted = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);

        byte[] passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        UnlockResult unlocked;
        try
        {
            unlocked = _cryptor.Unlock(encrypted, passwordBytes);
        }
        catch (BitosXCryptographicException)
        {
            VaultLog.VaultUnlockFailed(_logger);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(passwordBytes);
        }

        try
        {
            Vault vault = VaultSerializer.Deserialize(unlocked.Plaintext);
            _vault = vault;
            _vaultKey = unlocked.VaultKey; // ownership transferred to the service
            _header = unlocked.Header;
            _persistedFingerprint = EncryptedVaultFingerprint.Compute(encrypted);
            SetState(VaultState.Unlocked);
            VaultLog.VaultUnlocked(_logger, vault.Items.Count);
        }
        catch
        {
            CryptoBuffers.Wipe(unlocked.VaultKey);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(unlocked.Plaintext);
        }
    }

    public void Lock()
    {
        CryptoBuffers.Wipe(_vaultKey);
        _vaultKey = null;
        _vault = null;
        _header = null;
        _persistedFingerprint = null;
        SetState(VaultState.Locked);
        VaultLog.VaultLocked(_logger);
    }

    public async Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ValidateMasterPassword(newPassword);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException("The vault must be unlocked to change the master password.");
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            throw new ArgumentException("The new master password must be different from the current password.", nameof(newPassword));
        }

        // Read the current on-disk vault; re-wrapping carries its content over unchanged.
        EncryptedVault current = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);

        byte[] currentBytes = Encoding.UTF8.GetBytes(currentPassword);
        byte[] newBytes = Encoding.UTF8.GetBytes(newPassword);
        EncryptedVault reWrapped;
        try
        {
            // Verifies the current password (authenticated unwrap) and produces a re-wrapped vault.
            reWrapped = _cryptor.ChangeMasterPassword(current, currentBytes, newBytes);
        }
        catch (BitosXCryptographicException)
        {
            VaultLog.MasterPasswordChangeFailed(_logger);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(currentBytes);
            CryptoBuffers.Wipe(newBytes);
        }

        // Atomic write; on failure the previous vault (old password) is left intact.
        await _storage.WriteAsync(reWrapped, cancellationToken).ConfigureAwait(false);

        // Only after successful persistence: adopt the new header so later saves use the new wrapping.
        // The retained vault key and decrypted contents are unchanged.
        _header = reWrapped.Header;
        _persistedFingerprint = EncryptedVaultFingerprint.Compute(reWrapped);
        VaultLog.MasterPasswordChanged(_logger);
    }

    public async Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);

        if (_state != VaultState.Unlocked)
        {
            throw new InvalidOperationException("The vault must be unlocked to rotate the vault key.");
        }

        // The unlocked session always has a known persisted fingerprint (set on create/unlock/save).
        byte[] expectedFingerprint = _persistedFingerprint
            ?? throw new InvalidOperationException("The vault must be unlocked to rotate the vault key.");

        cancellationToken.ThrowIfCancellationRequested();

        // Read the current on-disk vault (cancellable — nothing has been written yet).
        EncryptedVault current = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);

        // Consistency gate: the on-disk vault must be byte-for-byte the state this session last loaded or
        // persisted. This catches BOTH a different cryptographic identity (key/header) AND a same-key
        // external content change (different content nonce/ciphertext), because the fingerprint covers the
        // whole envelope. If it changed, fail closed and write nothing.
        byte[] actualFingerprint = EncryptedVaultFingerprint.Compute(current);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualFingerprint, expectedFingerprint))
        {
            VaultLog.VaultKeyRotationFailed(_logger);
            throw new VaultChangedExternallyException();
        }

        byte[] currentBytes = Encoding.UTF8.GetBytes(currentPassword);
        VaultKeyRotationResult rotated;
        try
        {
            // Verifies the current password (authenticated decrypt), re-encrypts under a fresh key, and
            // self-checks. All expensive/cancellable-in-spirit work happens here, before any write.
            rotated = _cryptor.RotateVaultKey(current, currentBytes);
        }
        catch (BitosXCryptographicException)
        {
            VaultLog.VaultKeyRotationFailed(_logger);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(currentBytes);
        }

        // Last safe cancellation point: before any destructive write. After this, cancellation is not
        // honored so we can never end with disk = rotated vault but memory = old key/header.
        if (cancellationToken.IsCancellationRequested)
        {
            CryptoBuffers.Wipe(rotated.NewVaultKey);
            throw new OperationCanceledException(cancellationToken);
        }

        // ---- Non-cancellable commit section ----
        // The atomic replacement is issued with CancellationToken.None so it cannot be interrupted, and
        // the in-memory key/header are adopted immediately afterward with no awaited step in between.
        byte[] rotatedFingerprint = EncryptedVaultFingerprint.Compute(rotated.Encrypted);
        Exception? writeException = null;
        try
        {
            await _storage.WriteAsync(rotated.Encrypted, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writeException = ex;
        }

        if (writeException is not null)
        {
            // The write threw AFTER we committed to replacing the vault. A correct atomic store leaves the
            // old file intact on failure, but the service must not depend on that: the replacement MAY have
            // taken effect before the throw. Reconcile against disk so the in-memory session can never end
            // up disagreeing with the on-disk vault (never disk = rotated while memory = old key).
            await ReconcileFailedRotationCommitAsync(rotated, rotatedFingerprint, expectedFingerprint, writeException)
                .ConfigureAwait(false);
            return;
        }

        AdoptRotatedVault(rotated, rotatedFingerprint);
    }

    /// <summary>Adopts rotated key material into the unlocked session (disk already holds the rotated vault).</summary>
    private void AdoptRotatedVault(VaultKeyRotationResult rotated, byte[] rotatedFingerprint)
    {
        byte[]? previousKey = _vaultKey;
        _vaultKey = rotated.NewVaultKey;
        _header = rotated.Encrypted.Header;
        _persistedFingerprint = rotatedFingerprint;
        CryptoBuffers.Wipe(previousKey);
        VaultLog.VaultKeyRotated(_logger);
    }

    /// <summary>
    /// Reconciles the in-memory session with disk after the rotation commit write threw. Reads the actual
    /// on-disk vault and compares its fingerprint against the pre-rotation and rotated fingerprints:
    /// <list type="bullet">
    /// <item>disk == old → replacement did not take effect: keep the old key, surface the write failure.</item>
    /// <item>disk == rotated → replacement took effect before the throw: adopt the new key (success).</item>
    /// <item>disk == neither, or unreadable → indeterminate: lock the session and fail closed.</item>
    /// </list>
    /// </summary>
    private async Task ReconcileFailedRotationCommitAsync(
        VaultKeyRotationResult rotated, byte[] rotatedFingerprint, byte[] oldFingerprint, Exception writeException)
    {
        EncryptedVault onDisk;
        try
        {
            onDisk = await _storage.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The on-disk state cannot even be read — treat as indeterminate and fail closed.
            CryptoBuffers.Wipe(rotated.NewVaultKey);
            LockAfterIndeterminateState();
            throw new VaultInconsistentStateException();
        }

        byte[] diskFingerprint = EncryptedVaultFingerprint.Compute(onDisk);

        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(diskFingerprint, oldFingerprint))
        {
            // Replacement did NOT take effect: the old vault is intact on disk. Keep the still-valid old
            // key and surface the genuine write failure. Nothing was rotated.
            CryptoBuffers.Wipe(rotated.NewVaultKey);
            VaultLog.VaultKeyRotationFailed(_logger);
            throw writeException;
        }

        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(diskFingerprint, rotatedFingerprint))
        {
            // Replacement DID take effect before the throw: disk holds the rotated vault. Adopt the new
            // key so memory matches disk — the rotation is effectively complete.
            AdoptRotatedVault(rotated, rotatedFingerprint);
            return;
        }

        // Disk matches neither the old nor the rotated vault — indeterminate/corrupt. Lock and fail closed.
        CryptoBuffers.Wipe(rotated.NewVaultKey);
        LockAfterIndeterminateState();
        throw new VaultInconsistentStateException();
    }

    /// <summary>Wipes all key material and locks the session after an indeterminate destructive-write outcome.</summary>
    private void LockAfterIndeterminateState()
    {
        CryptoBuffers.Wipe(_vaultKey);
        _vaultKey = null;
        _vault = null;
        _header = null;
        _persistedFingerprint = null;
        SetState(VaultState.Locked);
        VaultLog.VaultKeyRotationFailed(_logger);
    }

    public async Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        Vault vault = RequireUnlockedVault();

        vault.Add(item);
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            vault.Remove(item.Id); // roll back in-memory change to stay consistent with disk
            throw;
        }

        VaultLog.VaultItemAdded(_logger, item.ItemType);
        return item;
    }

    public async Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        Vault vault = RequireUnlockedVault();

        VaultItem previous = vault.Find(item.Id) ?? throw new ItemNotFoundException(item.Id);
        VaultItem stored = vault.Update(item);
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            vault.Update(previous); // restore previous version
            throw;
        }

        VaultLog.VaultItemUpdated(_logger, item.ItemType);
        return stored;
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Vault vault = RequireUnlockedVault();

        VaultItem previous = vault.Find(id) ?? throw new ItemNotFoundException(id);
        vault.Remove(id);
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            vault.Add(previous); // restore removed item
            throw;
        }

        VaultLog.VaultItemDeleted(_logger);
    }

    public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Vault vault = RequireUnlockedVault();
        VaultItem existing = vault.Find(id) ?? throw new ItemNotFoundException(id);
        VaultItem toggled = existing with { Favorite = !existing.Favorite };
        return UpdateItemAsync(toggled, cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Vault vault = RequireUnlockedVault();
        byte[] vaultKey = _vaultKey ?? throw new InvalidOperationException("The vault is not unlocked.");
        EncryptedVaultHeader header = _header ?? throw new InvalidOperationException("The vault is not unlocked.");

        byte[] plaintext = VaultSerializer.SerializeToUtf8Bytes(vault);
        try
        {
            EncryptedVault encrypted = _cryptor.ReEncryptContent(header, vaultKey, plaintext);
            await _storage.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
            _persistedFingerprint = EncryptedVaultFingerprint.Compute(encrypted);
            VaultLog.VaultSaved(_logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VaultLog.VaultSaveFailed(_logger);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(plaintext);
        }
    }

    private Vault RequireUnlockedVault()
        => _vault ?? throw new InvalidOperationException("The vault is not unlocked.");

    private static void ValidateMasterPassword(string masterPassword)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        if (masterPassword.Length < MinMasterPasswordLength)
        {
            throw new ArgumentException(
                $"The master password must be at least {MinMasterPasswordLength} characters.", nameof(masterPassword));
        }

        if (masterPassword.Length > MaxMasterPasswordLength)
        {
            throw new ArgumentException(
                $"The master password must be at most {MaxMasterPasswordLength} characters.", nameof(masterPassword));
        }
    }

    private void SetState(VaultState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
