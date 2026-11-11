using BitosX.Core.ImportExport;
using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// File-based <see cref="IImportExportFiles"/>. An import file is read with the bound every other
/// read has; an export is written the way the vault is: to a new file that only its owner can read,
/// flushed, and then moved into place in one step.
/// </summary>
public sealed class FileImportExportFiles : IImportExportFiles
{
    private readonly string _vaultFilePath;

    /// <param name="vaultFilePath">The vault file, which an export is never written over.</param>
    public FileImportExportFiles(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);
        _vaultFilePath = Path.GetFullPath(vaultFilePath);
    }

    public async Task<byte[]> ReadImportFileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The file to import was not found.", sourcePath);
        }

        try
        {
            return await StorageFiles
                .ReadBoundedAsync(sourcePath, ImportLimits.MaxFileBytes, "import file", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnsupportedVaultFormatException)
        {
            // The bounded read has one way of failing that is not about the file system: the size.
            throw new ImportRefusedException(ImportRefusal.TooLarge);
        }
    }

    public async Task WriteExportFileAsync(
        string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDestination, _vaultFilePath, PathComparison))
        {
            // The vault is never replaced by an export, least of all by an unencrypted one.
            throw new ArgumentException("The export cannot be saved over the vault file itself.", nameof(destinationPath));
        }

        string directory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        // Created for its owner only on Unix, and never there in part: an interrupted export leaves
        // no file with half of the passwords in it under the name that was asked for.
        await StorageFiles.WriteAtomicAsync(fullDestination, content, cancellationToken).ConfigureAwait(false);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
