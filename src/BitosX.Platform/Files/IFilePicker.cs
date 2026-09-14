namespace BitosX.Platform.Files;

/// <summary>
/// Abstraction over the platform file-chooser, returning a local filesystem path (or
/// <see langword="null"/> if the user cancels or the chosen item has no local path). Keeps the
/// UI framework out of Core. Callers must still validate the actual file contents — never trust the
/// name, extension, or type reported by the picker.
/// </summary>
public interface IFilePicker
{
    /// <summary>Shows a "save file" chooser for exporting a backup; returns the chosen path or null.</summary>
    Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default);

    /// <summary>Shows an "open file" chooser for selecting a backup to restore; returns the chosen path or null.</summary>
    Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default);
}
