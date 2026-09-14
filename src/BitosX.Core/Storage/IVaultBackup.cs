using BitosX.Crypto;

namespace BitosX.Core.Storage;

/// <summary>
/// File-level operations for encrypted vault backups (Approach A: the backup is the existing
/// encrypted vault envelope, byte-for-byte). All operations treat the backup file as untrusted
/// input and reuse the same size limits and fail-closed parsing as the live vault. None of these
/// operations decrypt or authenticate — that is the caller's responsibility before a restore
/// replaces the live vault.
/// </summary>
public interface IVaultBackup
{
    /// <summary>
    /// Exports the current vault to <paramref name="destinationPath"/> byte-for-byte, atomically
    /// (temp file + replace). The current vault is validated as parseable first and is never
    /// modified. Throws if no vault exists or the current vault is unreadable/oversized.
    /// </summary>
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and strictly parses a backup file from an arbitrary path (enforcing the maximum file
    /// size and fail-closed envelope validation). Does not decrypt. Throws
    /// <see cref="UnsupportedVaultFormatException"/> on anything malformed/oversized.
    /// </summary>
    Task<BackupReadResult> ReadBackupAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the current vault with <paramref name="backup"/>. Preserves the existing vault as a
    /// recovery copy first, then writes atomically, so a failure or crash leaves either the old or
    /// the restored valid vault in place — never a partial file. The caller MUST have already
    /// authenticated and validated <paramref name="backup"/>.
    /// </summary>
    Task RestoreAsync(EncryptedVault backup, CancellationToken cancellationToken = default);
}

/// <summary>Result of reading a backup file: the parsed (still-encrypted) envelope and its size.</summary>
public sealed record BackupReadResult(EncryptedVault Envelope, long FileSizeBytes);
