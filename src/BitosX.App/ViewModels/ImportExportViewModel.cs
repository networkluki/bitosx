using System.Globalization;
using BitosX.App.Services;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Platform.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>
/// The "Import and export" card of the Settings page: adding the logins of a file another password
/// manager exported, after a preview and a confirmation; and writing the vault out unencrypted,
/// after a warning, an acknowledgement and the master password. The rules both follow are in
/// docs/IMPORT_EXPORT.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Import.</b> Choosing a file reads it and shows what importing it would do; nothing is written.
/// Each of the two choices asks for the preview again. Only the button that imports writes, and it
/// can be pressed only while the preview on screen is the one the choices ask for. What is shown is
/// an <see cref="ImportPreview"/>: counts, names and reasons, never a secret of the file.
/// </para>
/// <para>
/// <b>Export.</b> The save dialog comes first, then one call that checks the master password and
/// writes the file (<see cref="IVaultExportService.ExportAsync"/>). So the key is derived once for
/// an export, not at all for one that is called off in the dialog, and what is written is what the
/// check was for. The password box is emptied after every attempt.
/// </para>
/// <para>
/// <b>Locking.</b> Between choosing a file and importing it, this object holds the logins of the
/// file as text, and while an export is open it holds what was typed into the password box. Both
/// are let go — with the preview, the file's name and every status text — when the vault locks,
/// however it comes to lock, and when the page is disposed of. Whatever a call that was still
/// running then answers is dropped. A new card is made for the next unlock.
/// </para>
/// <para>
/// No text an exception carries reaches the page, except the fixed texts of the exceptions that
/// are made to be shown: a parser's message can quote the file, and the file system's names a path.
/// </para>
/// </remarks>
public sealed partial class ImportExportViewModel : ViewModelBase, IDisposable
{
    private const string NotLocalLocation = "That location cannot be used: it is not a folder on this computer. Choose a folder on this computer.";

    private readonly IVaultImportService _import;
    private readonly IVaultExportService _export;
    private readonly IVaultService _vault;
    private readonly IFilePicker _files;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;

    // The logins of the file, as text, and the plan the preview on screen was made from. Held from
    // the moment a file is read until it is imported, the import is called off, or the vault locks.
    private ParsedImport? _file;
    private ImportPlan? _plan;

    // Which import and which export is open. A call that was started for another one has nothing
    // to say any more: what it answers is dropped.
    private int _importFlow;
    private int _exportFlow;

    // Which preview was asked for last: of two that are on their way, only the later one is shown.
    private int _previewRequest;
    private Task _previewing = Task.CompletedTask;

    private bool _resettingChoices;
    private bool _disposed;

    public ImportExportViewModel(
        IVaultImportService import,
        IVaultExportService export,
        IVaultService vault,
        IFilePicker files,
        IUiDispatcher ui,
        TimeProvider time)
    {
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _vault.StateChanged += OnVaultStateChanged;
    }

    // ---- Import ----

    /// <summary>The name of the file that was chosen, without its folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportFile))]
    public partial string? ImportFileName { get; private set; }

    public bool HasImportFile => ImportFileName is not null;

    /// <summary>The file is being read and compared with the vault.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportBusy))]
    [NotifyCanExecuteChangedFor(nameof(ChooseImportFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmImportCommand))]
    public partial bool IsReadingImport { get; private set; }

    /// <summary>The logins are being added: the vault is encrypted and written once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportBusy))]
    [NotifyPropertyChangedFor(nameof(CanChangeImportChoices))]
    [NotifyCanExecuteChangedFor(nameof(ChooseImportFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    public partial bool IsApplyingImport { get; private set; }

    public bool IsImportBusy => IsReadingImport || IsApplyingImport;

    /// <summary>The two choices can be changed until the logins are being added.</summary>
    public bool CanChangeImportChoices => !IsApplyingImport;

    /// <summary>Why a file was not read, what an import did, or why it could not be done.</summary>
    [ObservableProperty]
    public partial string? ImportStatus { get; private set; }

    /// <summary>After an import: that the file still holds every password in plain text.</summary>
    [ObservableProperty]
    public partial string? ImportReminder { get; private set; }

    /// <summary>What importing the file would do; <see langword="null"/> while no file is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportPreview))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmImportCommand))]
    public partial ImportPreview? Preview { get; private set; }

    public bool HasImportPreview => Preview is not null;

    /// <summary>
    /// Whether the preview on screen is the one the choices ask for, made against the vault as it
    /// was when the page was last shown. While it is not, nothing can be imported.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmImportCommand))]
    public partial bool IsPreviewCurrent { get; private set; }

    /// <summary>Copy what BitosX has no field for into each item's notes. Off unless chosen.</summary>
    [ObservableProperty]
    public partial bool CopyExtrasToNotes { get; set; }

    /// <summary>Import duplicates too. Off unless chosen.</summary>
    [ObservableProperty]
    public partial bool ImportDuplicates { get; set; }

    /// <summary>Whether the entries that cannot be imported are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotImportedToggleText))]
    public partial bool IsNotImportedListShown { get; private set; }

    public string NotImportedToggleText => IsNotImportedListShown ? "Hide them" : "Show them";

    // The same words in both clients (docs/IMPORT_EXPORT.md).
    public string CopyExtrasText { get; } = ImportTexts.OptionCopyExtrasToNotes;

    public string ImportDuplicatesText { get; } = ImportTexts.OptionImportDuplicates;

    /// <summary>
    /// Completes when the preview shows what the choices ask for, or the import was closed. For
    /// tests, and for nothing else.
    /// </summary>
    public Task PreviewRefresh => _previewing;

    partial void OnCopyExtrasToNotesChanged(bool value) => ChoicesChanged();

    partial void OnImportDuplicatesChanged(bool value) => ChoicesChanged();

    private void ChoicesChanged()
    {
        if (!_resettingChoices && _file is not null)
        {
            _previewing = RefreshPreviewAsync();
        }
    }

    private ImportOptions Choices() => new(CopyExtrasToNotes, ImportDuplicates);

    private bool CanChooseImportFile() => !IsImportBusy;

    /// <summary>Asks for a file, reads it, and shows what importing it would do. Writes nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanChooseImportFile))]
    private async Task ChooseImportFileAsync()
    {
        if (_disposed || IsImportBusy)
        {
            return;
        }

        // A new import starts from nothing: what the last one read is let go of first.
        CloseImport();
        int flow = _importFlow;

        string? path;
        try
        {
            path = await _files.PickImportOpenAsync().ConfigureAwait(true);
        }
        catch (FileLocationNotLocalException)
        {
            if (flow == _importFlow)
            {
                ImportStatus = NotLocalLocation;
            }

            return;
        }

        // Called off; or the vault was locked while the dialog was open.
        if (string.IsNullOrEmpty(path) || flow != _importFlow)
        {
            return;
        }

        string name = Path.GetFileName(path);
        ImportFileName = name;
        IsReadingImport = true;
        try
        {
            ParsedImport file = await _import.ReadAsync(path).ConfigureAwait(true);
            if (flow != _importFlow)
            {
                return; // locked meanwhile: what was read is let go of
            }

            ImportPlan plan = await _import.PreviewAsync(file, Choices()).ConfigureAwait(true);
            if (flow != _importFlow)
            {
                return;
            }

            _file = file;
            Show(plan);
            IsReadingImport = false;
        }
        catch (Exception ex)
        {
            // Reading and comparing change nothing, so whatever went wrong, nothing was imported.
            // The vault stays open: a file that cannot be read is no reason to lock it.
            if (flow == _importFlow)
            {
                CloseImport();
                ImportFileName = name;
                ImportStatus = DescribeReadFailure(ex);
            }
        }
    }

    /// <summary>Asks for the preview again, for the choices as they are now and the vault as it is now.</summary>
    private async Task RefreshPreviewAsync()
    {
        if (_file is not { } file)
        {
            return;
        }

        int flow = _importFlow;
        int request = ++_previewRequest;

        // From here until the answer is shown, the preview on screen is not what would be imported.
        IsPreviewCurrent = false;
        try
        {
            ImportPlan plan = await _import.PreviewAsync(file, Choices()).ConfigureAwait(true);
            if (flow == _importFlow && request == _previewRequest)
            {
                Show(plan);
            }
        }
        catch (Exception ex)
        {
            if (flow == _importFlow && request == _previewRequest)
            {
                string? name = ImportFileName;
                CloseImport();
                ImportFileName = name;
                ImportStatus = DescribeReadFailure(ex);
            }
        }
    }

    private void Show(ImportPlan plan)
    {
        _plan = plan;
        var preview = ImportPreview.From(plan);
        if (preview.NotImportedEntries.Count == 0)
        {
            IsNotImportedListShown = false;
        }

        Preview = preview;
        IsPreviewCurrent = true;
        ConfirmImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmImport()
        => _plan is not null && Preview is { CanBeImported: true } && IsPreviewCurrent && !IsImportBusy;

    /// <summary>Adds the logins the preview shows, in one write of the vault.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmImport))]
    private async Task ConfirmImportAsync()
    {
        // Enforced here and not only by what the view enables: nothing is added that a preview which
        // is on screen, and current, did not show.
        if (_disposed || _plan is not { } plan || !CanConfirmImport() || plan.ToAdd == 0 || plan.ExceedsVaultItemLimit)
        {
            return;
        }

        int flow = _importFlow;
        ImportStatus = null;
        ImportReminder = null;
        IsApplyingImport = true;
        string? failure = null;
        try
        {
            ImportResult result = await _import.ApplyAsync(plan).ConfigureAwait(true);
            if (flow != _importFlow)
            {
                return; // locked meanwhile: the next unlock shows what the vault holds
            }

            string? name = ImportFileName;
            CloseImport();
            ImportFileName = name;
            ImportStatus = DescribeResult(result, plan);
            ImportReminder = ImportTexts.FileStillHoldsPasswords;
            return;
        }
        catch (ImportRefusedException ex)
        {
            failure = ex.Message; // the vault would hold more items than a vault may
        }
        catch (VaultTooLargeException ex)
        {
            failure = string.Create(
                CultureInfo.CurrentCulture,
                $"With these logins the vault would be larger than {ex.LimitBytes / (1024 * 1024)} MiB, which is the most BitosX can store. Nothing was imported.");
        }
        catch (VaultChangedExternallyException)
        {
            failure = "The vault changed on disk while it was open. Nothing was imported. Lock and reopen the vault, then try again.";
        }
        catch (VaultInconsistentStateException ex)
        {
            failure = ex.Message;
        }
        catch (VaultException)
        {
            // What the vault says about an item it does not accept can quote that item.
            failure = "The file holds an entry the vault does not accept. Nothing was imported.";
        }
        catch (BitosXCryptographicException)
        {
            failure = "The vault could not be processed. Nothing was imported.";
        }
        catch (UnauthorizedAccessException)
        {
            failure = "BitosX does not have permission to write the vault file. Nothing was imported.";
        }
        catch (IOException)
        {
            failure = "Could not save the vault. Nothing was imported.";
        }
        catch (OperationCanceledException)
        {
            failure = "The vault was locked before the import finished. Unlock it to see what it holds.";
        }
        catch (InvalidOperationException)
        {
            failure = "The vault is locked. Nothing was imported.";
        }

        if (flow != _importFlow)
        {
            return;
        }

        IsApplyingImport = false;
        ImportStatus = failure;

        // The vault may not be what the preview was made against; show what holds now.
        _previewing = RefreshPreviewAsync();
    }

    private bool CanCancelImport() => !IsApplyingImport;

    /// <summary>Closes the import. Nothing was written, and what was read is let go of.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport()
    {
        if (!IsApplyingImport)
        {
            CloseImport();
        }
    }

    [RelayCommand]
    private void ToggleNotImportedList() => IsNotImportedListShown = !IsNotImportedListShown;

    /// <summary>Lets go of everything an import holds and has said.</summary>
    private void CloseImport()
    {
        _importFlow++;
        _previewRequest++;
        _file = null;
        _plan = null;
        Preview = null;
        IsPreviewCurrent = false;
        IsNotImportedListShown = false;
        ImportFileName = null;
        ImportStatus = null;
        ImportReminder = null;
        IsReadingImport = false;
        IsApplyingImport = false;

        // Each file starts with both choices off.
        _resettingChoices = true;
        CopyExtrasToNotes = false;
        ImportDuplicates = false;
        _resettingChoices = false;

        ConfirmImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What an import did, said from what was done rather than from what the preview promised.</summary>
    private static string DescribeResult(ImportResult result, ImportPlan previewed)
    {
        if (result.Added == previewed.ToAdd)
        {
            return ImportPreview.Count(result.Added, "login was", "logins were") + " added to the vault.";
        }

        // The vault changed between the preview and the write; the file was compared with it again.
        const string Changed = "The vault changed after the preview was made, so the file was compared with it again: ";
        return result.Added == 0
            ? Changed + "nothing was added, because everything in the file is in the vault already."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{Changed}{ImportPreview.Count(result.Added, "login was", "logins were")} added, not {previewed.ToAdd:N0}.");
    }

    /// <summary>
    /// What is said when a file could not be read or compared. A refusal carries a fixed text that
    /// is made to be shown; everything else is told apart by its kind only.
    /// </summary>
    private static string DescribeReadFailure(Exception exception) => exception switch
    {
        ImportRefusedException { Line: { } line } refused => string.Create(
            CultureInfo.CurrentCulture, $"{refused.Message} The fault is on line {line:N0} of the file."),
        ImportRefusedException refused => refused.Message,
        FileNotFoundException or DirectoryNotFoundException => "The file was not found. It may have been moved or deleted.",
        UnauthorizedAccessException => "BitosX does not have permission to read that file.",
        IOException => "The file could not be read.",
        OperationCanceledException => "Reading the file was called off.",
        InvalidOperationException => "The vault is locked. Nothing was imported.",
        _ => "The file could not be read. Nothing was imported.",
    };

    // ---- Export ----

    /// <summary>The export is open: its warning is shown, and it can be written or called off.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteExportCommand))]
    public partial bool IsExportOpen { get; private set; }

    /// <summary>CSV is chosen; otherwise BitosX JSON is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExportJson))]
    public partial bool IsExportCsv { get; set; } = true;

    /// <summary>BitosX JSON is chosen: the format that holds everything, every address included.</summary>
    public bool IsExportJson
    {
        get => !IsExportCsv;
        set => IsExportCsv = !value;
    }

    /// <summary>How many items have more than one address, and that only JSON keeps them all.</summary>
    [ObservableProperty]
    public partial string? ExportSeveralUrlsNote { get; private set; }

    /// <summary>The acknowledgement that the file is not encrypted. Without it nothing is written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteExportCommand))]
    public partial bool ExportAcknowledged { get; set; }

    /// <summary>The master password, typed again for this export. Emptied after every attempt.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteExportCommand))]
    public partial string ExportPassword { get; set; } = string.Empty;

    /// <summary>The master password is being checked and the file written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial string? ExportStatus { get; private set; }

    // The same words in both clients (docs/IMPORT_EXPORT.md).
    public string ExportWarning { get; } = ExportTexts.Warning;

    public string ExportAcknowledgementText { get; } = ExportTexts.Acknowledgement;

    private bool CanOpenExport() => !IsExportOpen && !IsExporting;

    /// <summary>Opens the export: shows the warning and asks for the acknowledgement and the master password.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenExport))]
    private void OpenExport()
    {
        if (_disposed || !CanOpenExport())
        {
            return;
        }

        CloseExport();
        ExportStatus = null;
        UpdateSeveralUrlsNote();
        IsExportOpen = true;
    }

    private bool CanWriteExport()
        => IsExportOpen && ExportAcknowledged && ExportPassword.Length > 0 && !IsExporting;

    /// <summary>
    /// Asks where to save, then checks the master password and writes the file in one step. A wrong
    /// password writes nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWriteExport))]
    private async Task WriteExportAsync()
    {
        // Enforced here and not only by what the view enables.
        if (_disposed || !CanWriteExport())
        {
            return;
        }

        int flow = _exportFlow;
        ExportFormat format = IsExportJson ? ExportFormat.BitosXJson : ExportFormat.Csv;
        ExportStatus = null;

        string? path;
        try
        {
            path = await _files
                .PickExportSaveAsync(VaultExporter.SuggestedFileName(format, _time.GetLocalNow()), Extension(format))
                .ConfigureAwait(true);
        }
        catch (FileLocationNotLocalException)
        {
            if (flow == _exportFlow)
            {
                ExportStatus = NotLocalLocation;
            }

            return;
        }

        // Called off in the dialog: no password was checked and nothing was written. Or the vault
        // was locked while the dialog was open: then nothing that was typed is used any more.
        if (string.IsNullOrEmpty(path) || flow != _exportFlow || !CanWriteExport())
        {
            return;
        }

        IsExporting = true;
        try
        {
            ExportResult result = await _export.ExportAsync(path, format, ExportPassword).ConfigureAwait(true);
            if (flow == _exportFlow)
            {
                CloseExport();
                ExportStatus = DescribeResult(result);
            }
        }
        catch (VaultAuthenticationException)
        {
            Failed(flow, "The master password is incorrect. Nothing was exported.");
        }
        catch (KeyDerivationResourceException ex)
        {
            Failed(flow, ex.Message + " Nothing was exported.");
        }
        catch (BitosXCryptographicException)
        {
            Failed(flow, "The vault could not be read to check the master password. Nothing was exported.");
        }
        catch (VaultChangedExternallyException)
        {
            Failed(flow, "The vault changed on disk while it was open, so the master password could not be checked. Nothing was exported. Lock and reopen the vault, then try again.");
        }
        catch (ArgumentException)
        {
            Failed(flow, "The export cannot be saved there. Choose another name or folder; the vault file itself is never written over.");
        }
        catch (UnauthorizedAccessException)
        {
            Failed(flow, "BitosX does not have permission to write to that location. Nothing was exported.");
        }
        catch (IOException)
        {
            Failed(flow, "Could not write the export file. Nothing was exported.");
        }
        catch (OperationCanceledException)
        {
            Failed(flow, "The vault was locked before the export was written. Nothing was exported.");
        }
        catch (InvalidOperationException)
        {
            Failed(flow, "The vault is locked. Nothing was exported.");
        }
        finally
        {
            // Whatever the outcome, what was typed has done its job.
            if (flow == _exportFlow)
            {
                ExportPassword = string.Empty;
                IsExporting = false;
            }
        }
    }

    private void Failed(int flow, string status)
    {
        if (flow == _exportFlow)
        {
            ExportStatus = status;
        }
    }

    private bool CanCancelExport() => !IsExporting;

    /// <summary>Closes the export. Nothing was written.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelExport))]
    private void CancelExport()
    {
        if (!IsExporting)
        {
            CloseExport();
            ExportStatus = null;
        }
    }

    /// <summary>Lets go of what an export holds: the acknowledgement and what was typed.</summary>
    private void CloseExport()
    {
        _exportFlow++;
        IsExportOpen = false;
        ExportAcknowledged = false;
        ExportPassword = string.Empty;
        IsExporting = false;
        IsExportCsv = true;
    }

    private void UpdateSeveralUrlsNote()
    {
        int count = VaultExporter.CountItemsWithSeveralUrls(_vault.Items);
        const string OnlyJson = " A CSV file holds only the first address of each item; only the JSON file keeps them all.";
        ExportSeveralUrlsNote = count switch
        {
            0 => null,
            1 => "1 item in this vault has more than one address." + OnlyJson,
            _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} items in this vault have more than one address.{OnlyJson}"),
        };
    }

    private static string Extension(ExportFormat format) => format == ExportFormat.BitosXJson ? "json" : "csv";

    private static string DescribeResult(ExportResult result)
    {
        string format = result.Format == ExportFormat.BitosXJson ? "BitosX JSON" : "CSV";
        string text = ImportPreview.Count(result.ItemCount, "item was", "items were")
            + $" exported as {format}. The file is not encrypted: delete it when you no longer need it.";
        if (result.Format == ExportFormat.Csv && result.ItemsWithSeveralUrls > 0)
        {
            text += result.ItemsWithSeveralUrls == 1
                ? " 1 item has more than one address; the file holds only the first."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $" {result.ItemsWithSeveralUrls:N0} items have more than one address; the file holds only the first of each.");
        }

        return text;
    }

    // ---- The page, and the vault ----

    /// <summary>
    /// Called when the page is shown again. The vault may have changed while another page was
    /// shown: a preview is made again against what it holds now, and so is the count of items with
    /// several addresses.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (IsExportOpen)
        {
            UpdateSeveralUrlsNote();
        }

        if (_file is not null && !IsImportBusy)
        {
            _previewing = RefreshPreviewAsync();
        }
    }

    // The vault says so from whichever thread locked it; the card changes on the UI thread.
    private void OnVaultStateChanged(object? sender, VaultState state)
    {
        if (state != VaultState.Unlocked)
        {
            _ui.Run(CloseEverything);
        }
    }

    /// <summary>
    /// Closes the import and the export and lets go of everything they hold: the logins of the
    /// file, the plan, the file's name, what was typed into the password box, and every status text.
    /// </summary>
    private void CloseEverything()
    {
        CloseImport();
        CloseExport();
        ExportStatus = null;
        ExportSeveralUrlsNote = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vault.StateChanged -= OnVaultStateChanged;
        CloseEverything();
    }
}
