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
    /// <summary>Suffix of the recovery copy written before a restore replaces the live vault: the most recent one.</summary>
    public const string RecoverySuffix = ".pre-restore";

    /// <summary>
    /// How many earlier recovery copies are kept besides the most recent one, as
    /// <c>.pre-restore.1</c> (the one before) to <c>.pre-restore.3</c> (the oldest). A second
    /// restore, or a download right after one, no longer overwrites the only copy of the vault
    /// that was there before both.
    /// </summary>
    public const int EarlierRecoveryCopies = 3;

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

        // Preserve the current vault as a recovery copy before touching it, written as durably as the
        // vault itself (flushed, then moved into place), after moving the earlier copies down a slot.
        if (File.Exists(_vaultFilePath))
        {
            string recovery = _vaultFilePath + RecoverySuffix;
            byte[]? current;
            try
            {
                current = await StorageFiles
                    .ReadBoundedAsync(_vaultFilePath, FileLocalVaultStorage.MaxFileSizeBytes, "vault file", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnsupportedVaultFormatException)
            {
                current = null; // larger than any vault: kept as it is, by a plain copy
            }

            ShiftEarlierRecoveryCopies(recovery);
            if (current is not null)
            {
                await StorageFiles.WriteAtomicAsync(recovery, current, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Copy(_vaultFilePath, recovery, overwrite: true);
            }
        }

        await StorageFiles.WriteAtomicAsync(_vaultFilePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>.pre-restore.2</c> becomes <c>.3</c>, <c>.1</c> becomes <c>.2</c>, the most recent becomes <c>.1</c>; the oldest beyond that goes.</summary>
    private static void ShiftEarlierRecoveryCopies(string recovery)
    {
        for (int slot = EarlierRecoveryCopies - 1; slot >= 1; slot--)
        {
            string from = $"{recovery}.{slot}";
            if (File.Exists(from))
            {
                File.Move(from, $"{recovery}.{slot + 1}", overwrite: true);
            }
        }

        if (File.Exists(recovery))
        {
            File.Move(recovery, recovery + ".1", overwrite: true);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
