using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BitosX.Platform.Files;

namespace BitosX.App.Services;

/// <summary>
/// Avalonia-backed <see cref="IFilePicker"/> over <see cref="TopLevel.StorageProvider"/>. Returns a
/// local path via <see cref="IStorageItem.TryGetLocalPath"/>; a non-local location (no filesystem
/// path) throws <see cref="FileLocationNotLocalException"/>, so the caller fails safe and says why
/// rather than guessing — or doing nothing, as if the dialog had been cancelled.
/// </summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    private const string BackupExtension = "bitosx-backup";

    private readonly Func<TopLevel?> _topLevelProvider;

    public AvaloniaFilePicker(Func<TopLevel?> topLevelProvider)
    {
        _topLevelProvider = topLevelProvider ?? throw new ArgumentNullException(nameof(topLevelProvider));
    }

    public async Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default)
    {
        TopLevel? top = _topLevelProvider();
        if (top is null)
        {
            return null;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export encrypted backup",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = BackupExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [BackupFileType()],
        }).ConfigureAwait(true);

        return file is null ? null : LocalPath(file);
    }

    public async Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default)
    {
        TopLevel? top = _topLevelProvider();
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore encrypted backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFileType(), FilePickerFileTypes.All],
        }).ConfigureAwait(true);

        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    private static string LocalPath(IStorageItem item)
        => item.TryGetLocalPath() is { Length: > 0 } path ? path : throw new FileLocationNotLocalException();

    private static FilePickerFileType BackupFileType() => new("BitosX backup")
    {
        Patterns = [$"*.{BackupExtension}"],
    };
}
