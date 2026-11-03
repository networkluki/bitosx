using BitosX.Core.Diagnostics;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Serialization;
using BitosX.Core.Storage;
using BitosX.Core.Validation;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>
/// Orchestrates encrypted backup export and restore (Approach A). Export copies the existing
/// encrypted vault envelope byte-for-byte; restore authenticates and validates a backup BEFORE it
/// is allowed to replace the live vault. No new cryptographic format is introduced.
/// </summary>
/// <remarks>
/// Authorization policy: export and restore both require the current session to be unlocked; restore
/// additionally requires the backup's own master password. Restore never modifies the live vault
/// until the backup has been strictly parsed, authenticated, and its decrypted model validated.
/// </remarks>
public sealed class VaultBackupService : IVaultBackupService
{
    private readonly IVaultService _vaultService;
    private readonly IVaultBackup _backup;
    private readonly VaultCryptor _cryptor;
    private readonly ILogger<VaultBackupService> _logger;

    public VaultBackupService(
        IVaultService vaultService,
        IVaultBackup backup,
        VaultCryptor cryptor,
        ILogger<VaultBackupService> logger)
    {
        _vaultService = vaultService ?? throw new ArgumentNullException(nameof(vaultService));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _cryptor = cryptor ?? throw new ArgumentNullException(nameof(cryptor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        RequireUnlocked();

        await _backup.ExportAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        VaultLog.BackupExported(_logger);
    }

    /// <summary>
    /// Reads, strictly parses, authenticates (with the backup's master password), and validates the
    /// decrypted model of a backup — without touching the live vault. Returns a safe summary plus the
    /// (still-encrypted) envelope to commit. Throws on any failure, leaving the live vault untouched.
    /// </summary>
    public async Task<BackupPreview> PrepareRestoreAsync(string backupPath, string backupPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentNullException.ThrowIfNull(backupPassword);
        RequireUnlocked();

        BackupReadResult read = await _backup.ReadBackupAsync(backupPath, cancellationToken).ConfigureAwait(false);

        // Up to four Argon2id derivations for a backup made before 0.6.0: never on the caller's (UI) thread.
        return await Task.Run(() => Prepare(read.Envelope, backupPassword, Path.GetFileName(backupPath), read.FileSizeBytes)).ConfigureAwait(false);
    }

    public Task<BackupPreview> PrepareRestoreAsync(EncryptedVault envelope, string backupPassword, string sourceName, long sizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(backupPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        RequireUnlocked();
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => Prepare(envelope, backupPassword, sourceName, sizeBytes));
    }

    /// <summary>Authenticates and validates an envelope without touching the live vault; see <see cref="PrepareRestoreAsync(string, string, CancellationToken)"/>.</summary>
    private BackupPreview Prepare(EncryptedVault envelope, string backupPassword, string sourceName, long sizeBytes)
    {
        // A backup made before 0.6.0 may be keyed with another encoding of the same password.
        PasswordCandidates candidates = MasterPasswordEncoding.EncodeCandidates(backupPassword);
        UnlockResult unlocked;
        try
        {
            // Authenticate; a wrong password fails closed.
            (unlocked, _) = candidates.FirstAccepted(encoding => _cryptor.Unlock(envelope, encoding));
        }
        catch (BitosXCryptographicException)
        {
            VaultLog.BackupRestoreValidationFailed(_logger);
            throw;
        }
        finally
        {
            candidates.Wipe();
        }

        int itemCount;
        try
        {
            Vault vault = VaultSerializer.Deserialize(unlocked.Plaintext); // schema + duplicate-id check
            foreach (VaultItem item in vault.Items)
            {
                VaultValidation.Validate(item); // defense-in-depth: validate decrypted model before accepting
            }

            itemCount = vault.Items.Count;
        }
        catch
        {
            VaultLog.BackupRestoreValidationFailed(_logger);
            throw;
        }
        finally
        {
            // We only need the ciphertext envelope to restore; wipe the decrypted material now.
            CryptoBuffers.Wipe(unlocked.VaultKey);
            CryptoBuffers.Wipe(unlocked.Plaintext);
        }

        return new BackupPreview(envelope, envelope.Version, itemCount, sourceName, sizeBytes);
    }

    /// <summary>
    /// Commits a previously prepared restore: preserves the current vault as a recovery copy, then
    /// atomically replaces it with the backup envelope, and locks so the user re-unlocks with the
    /// backup's master password.
    /// </summary>
    public async Task CommitRestoreAsync(BackupPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        RequireUnlocked();

        // Back on the caller's thread (the UI thread, when there is one) before locking: whoever listens
        // to the vault locking expects to hear it there.
        await _backup.RestoreAsync(preview.Envelope, cancellationToken).ConfigureAwait(true);
        _vaultService.Lock();
        await _vaultService.RefreshStateAsync(cancellationToken).ConfigureAwait(false);
        VaultLog.BackupRestored(_logger);
    }

    private void RequireUnlocked()
    {
        if (_vaultService.State != VaultState.Unlocked)
        {
            throw new InvalidOperationException("The vault must be unlocked for backup operations.");
        }
    }
}
