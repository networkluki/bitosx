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
        VaultLog.MasterPasswordChanged(_logger);
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
