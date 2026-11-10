using BitosX.Core.ImportExport;

namespace BitosX.Core.Storage;

/// <summary>
/// The files of an import and of an unencrypted export. Unlike everything else the storage layer
/// handles, these hold passwords in plain text: an import file because another program wrote it so,
/// an export because the user asked for it. Nothing here decides whether that may happen
/// (<see cref="Services.IVaultExportService"/> does); this only reads and writes.
/// </summary>
public interface IImportExportFiles
{
    /// <summary>
    /// Reads a file to import, whole, refusing one larger than <see cref="ImportLimits.MaxFileBytes"/>
    /// (<see cref="ImportRefusedException"/> with <see cref="ImportRefusal.TooLarge"/>). The limit is
    /// enforced on the bytes actually read, not only on the size reported beforehand.
    /// </summary>
    Task<byte[]> ReadImportFileAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an unencrypted export to <paramref name="destinationPath"/>, atomically: the path holds
    /// either what was there before or the whole export, never part of it. On Unix the file can be
    /// read by its owner only. The vault file itself is never written over.
    /// </summary>
    Task WriteExportFileAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}
