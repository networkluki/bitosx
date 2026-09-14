using BitosX.Crypto;

namespace BitosX.Core.Services;

/// <summary>
/// Encrypted backup export and restore. Restore is a two-phase operation: prepare (read, parse,
/// authenticate, validate — no changes to the live vault) then commit (atomic replace).
/// </summary>
public interface IVaultBackupService
{
    /// <summary>Exports the current encrypted vault, byte-for-byte, to the destination path. Requires an unlocked session.</summary>
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a backup with its master password without altering the live vault, returning a safe
    /// summary to confirm. Throws on wrong password, malformed/oversized/unsupported input, or an
    /// invalid decrypted model.
    /// </summary>
    Task<BackupPreview> PrepareRestoreAsync(string backupPath, string backupPassword, CancellationToken cancellationToken = default);

    /// <summary>Commits a prepared restore: atomically replaces the live vault (after a recovery copy), then locks.</summary>
    Task CommitRestoreAsync(BackupPreview preview, CancellationToken cancellationToken = default);
}

/// <summary>
/// A validated backup ready to restore. <see cref="Envelope"/> is ciphertext + public parameters
/// only (no secrets). The summary fields are safe to display; they are derived after successful
/// authentication/validation, never trusted from the file.
/// </summary>
public sealed record BackupPreview(
    EncryptedVault Envelope,
    int FormatVersion,
    int ItemCount,
    string FileName,
    long FileSizeBytes);
