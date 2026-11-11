namespace BitosX.Platform.Files;

/// <summary>
/// Abstraction over the platform file-chooser, returning a local filesystem path, or
/// <see langword="null"/> if the user cancels. A chosen item that has no local path is reported
/// with <see cref="FileLocationNotLocalException"/>, so it is not mistaken for a cancel. Keeps the
/// UI framework out of Core. Callers must still validate the actual file contents — never trust the
/// name, extension, or type reported by the picker.
/// </summary>
public interface IFilePicker
{
    /// <summary>Shows a "save file" chooser for exporting a backup; returns the chosen path or null.</summary>
    Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default);

    /// <summary>Shows an "open file" chooser for selecting a backup to restore; returns the chosen path or null.</summary>
    Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows an "open file" chooser for a file to import logins from: an export of another password
    /// manager, as CSV, JSON or XML. Returns the chosen path or null. What the file is, is decided by
    /// reading it, never by its name.
    /// </summary>
    Task<string?> PickImportOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a "save file" chooser for an unencrypted export; returns the chosen path or null.
    /// </summary>
    /// <param name="suggestedFileName">The name the chooser starts with.</param>
    /// <param name="extension">The extension of the format, without a dot: <c>csv</c> or <c>json</c>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task<string?> PickExportSaveAsync(string suggestedFileName, string extension, CancellationToken cancellationToken = default);
}
