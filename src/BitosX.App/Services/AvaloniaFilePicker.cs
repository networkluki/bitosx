using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BitosX.Platform.Files;

namespace BitosX.App.Services;

/// <summary>
/// Avalonia-backed <see cref="IFilePicker"/> over <see cref="TopLevel.StorageProvider"/>. Returns a
/// local path via <see cref="IStorageItem.TryGetLocalPath"/>; a non-local location (no filesystem
/// path) yields <see langword="null"/> so the caller fails safe rather than guessing.
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

        return file?.TryGetLocalPath();
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

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static FilePickerFileType BackupFileType() => new("BitosX backup")
    {
        Patterns = [$"*.{BackupExtension}"],
    };
}
