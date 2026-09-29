using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// File-based <see cref="IVaultBackup"/> (Approach A). Export copies the current encrypted vault
/// envelope byte-for-byte; restore preserves the current vault as a recovery copy and then
/// atomically replaces it. Reuses the same size limit, fail-closed parsing, and atomic, durable
/// write as <see cref="FileLocalVaultStorage"/>. Never decrypts.
/// </summary>
public sealed class FileVaultBackup : IVaultBackup
{
    /// <summary>Suffix of the recovery copy written before a restore replaces the live vault.</summary>
    public const string RecoverySuffix = ".pre-restore";

    private readonly string _vaultFilePath;

    public FileVaultBackup(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);
        _vaultFilePath = Path.GetFullPath(vaultFilePath);
    }

    /// <summary>Creates a backup helper for the default per-user vault location.</summary>
    public static FileVaultBackup CreateDefault() => new(FileLocalVaultStorage.DefaultVaultFilePath);

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(_vaultFilePath))
        {
            throw new FileNotFoundException("There is no vault to export.", _vaultFilePath);
        }

        string fullDest = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDest, _vaultFilePath, PathComparison))
        {
            // The live vault is never written by an export, not even with its own content.
            throw new ArgumentException("The backup cannot be saved over the vault file itself.", nameof(destinationPath));
        }

        string destDir = Path.GetDirectoryName(fullDest)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));

        byte[] bytes = await StorageFiles
            .ReadBoundedAsync(_vaultFilePath, FileLocalVaultStorage.MaxFileSizeBytes, "vault file", cancellationToken)
            .ConfigureAwait(false);

        // Validate that the current vault is a well-formed envelope before exporting it (never export garbage).
        _ = EncryptedVaultSerializer.Deserialize(bytes);

        Directory.CreateDirectory(destDir);

        // Byte-for-byte copy, written atomically so a failed/interrupted export leaves no partial backup.
        await StorageFiles.WriteAtomicAsync(fullDest, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupReadResult> ReadBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The backup file was not found.", sourcePath);
        }

        byte[] bytes = await StorageFiles
            .ReadBoundedAsync(sourcePath, FileLocalVaultStorage.MaxFileSizeBytes, "backup file", cancellationToken)
            .ConfigureAwait(false);
        EncryptedVault envelope = EncryptedVaultSerializer.Deserialize(bytes); // fails closed
        return new BackupReadResult(envelope, bytes.Length);
    }

    public async Task RestoreAsync(EncryptedVault backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        string directory = Path.GetDirectoryName(_vaultFilePath)
            ?? throw new InvalidOperationException("The vault path must include a directory.");
        StorageFiles.CreatePrivateDirectory(directory);

        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(backup);

        // Preserve the current vault as a recovery copy before touching it.
        if (File.Exists(_vaultFilePath))
        {
            File.Copy(_vaultFilePath, _vaultFilePath + RecoverySuffix, overwrite: true);
        }

        await StorageFiles.WriteAtomicAsync(_vaultFilePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
