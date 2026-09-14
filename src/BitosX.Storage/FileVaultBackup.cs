using System.Text;
using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// File-based <see cref="IVaultBackup"/> (Approach A). Export copies the current encrypted vault
/// envelope byte-for-byte; restore preserves the current vault as a recovery copy and then
/// atomically replaces it. Reuses the same size limit and fail-closed parsing as
/// <see cref="FileLocalVaultStorage"/>. Never decrypts.
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

        var info = new FileInfo(_vaultFilePath);
        if (info.Length > FileLocalVaultStorage.MaxFileSizeBytes)
        {
            throw new UnsupportedVaultFormatException(
                $"The vault file is too large to export ({info.Length} bytes; limit {FileLocalVaultStorage.MaxFileSizeBytes}).");
        }

        byte[] bytes = await File.ReadAllBytesAsync(_vaultFilePath, cancellationToken).ConfigureAwait(false);

        // Validate that the current vault is a well-formed envelope before exporting it (never export garbage).
        _ = EncryptedVaultSerializer.Deserialize(Encoding.UTF8.GetString(bytes));

        string fullDest = Path.GetFullPath(destinationPath);
        string destDir = Path.GetDirectoryName(fullDest)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(destDir);

        string tempPath = fullDest + ".tmp";
        try
        {
            // Byte-for-byte copy, written atomically so a failed/interrupted export leaves no partial backup.
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullDest))
            {
                File.Replace(tempPath, fullDest, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, fullDest);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task<BackupReadResult> ReadBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The backup file was not found.", sourcePath);
        }

        var info = new FileInfo(sourcePath);
        if (info.Length > FileLocalVaultStorage.MaxFileSizeBytes)
        {
            throw new UnsupportedVaultFormatException(
                $"The backup file is too large ({info.Length} bytes; limit {FileLocalVaultStorage.MaxFileSizeBytes}).");
        }

        byte[] bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        EncryptedVault envelope = EncryptedVaultSerializer.Deserialize(Encoding.UTF8.GetString(bytes)); // fails closed
        return new BackupReadResult(envelope, bytes.Length);
    }

    public async Task RestoreAsync(EncryptedVault backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        string directory = Path.GetDirectoryName(_vaultFilePath)
            ?? throw new InvalidOperationException("The vault path must include a directory.");
        Directory.CreateDirectory(directory);

        // Preserve the current vault as a recovery copy before touching it.
        if (File.Exists(_vaultFilePath))
        {
            File.Copy(_vaultFilePath, _vaultFilePath + RecoverySuffix, overwrite: true);
        }

        string json = EncryptedVaultSerializer.Serialize(backup);
        string tempPath = _vaultFilePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            if (File.Exists(_vaultFilePath))
            {
                File.Replace(tempPath, _vaultFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _vaultFilePath);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; it is overwritten on the next write.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored for the same reason.
        }
    }
}
