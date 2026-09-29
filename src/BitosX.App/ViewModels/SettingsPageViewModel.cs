using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Platform.Files;
using BitosX.Platform.Session;
using BitosX.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The Settings page: environment/security information plus auto-lock preferences.</summary>
public sealed partial class SettingsPageViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly AutoLockService _autoLock;
    private readonly IVaultBackupService _backupService;
    private readonly IFilePicker _filePicker;
    private readonly RestoreConfirmationState _restore = new();
    private BackupPreview? _pendingRestore;
    private string? _restoreFilePath;
    private bool _loading;

    public string VaultLocation { get; }
    public string KdfDescription { get; }

    /// <summary>Set when the open vault is protected by weaker key derivation than this build uses for new vaults.</summary>
    public string? KdfWarning { get; }
    public string AlgorithmDescription { get; }
    public string ClipboardClearDescription { get; }
    public string ServerIntegrationDescription { get; }
    public string AppVersion { get; }

    /// <summary>Honest per-platform status for the OS-driven lock triggers (may be NOT VERIFIED).</summary>
    public string SessionLockSupport { get; }
    public string SuspendSupport { get; }

    [ObservableProperty]
    public partial bool AutoLockOnInactivity { get; set; }

    [ObservableProperty]
    public partial int InactivityMinutes { get; set; }

    [ObservableProperty]
    public partial bool LockOnMinimize { get; set; }

    [ObservableProperty]
    public partial string? AutoLockStatus { get; set; }

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPasswordStrengthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ChangePasswordStatus { get; set; }

    [ObservableProperty]
    public partial string RotatePassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RotateStatus { get; set; }

    [ObservableProperty]
    public partial string? BackupStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBackup))]
    public partial string? SelectedBackupName { get; set; }

    [ObservableProperty]
    public partial string RestoreBackupPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestorePending))]
    public partial string? RestoreSummary { get; set; }

    [ObservableProperty]
    public partial string? RestoreStatus { get; set; }

    public bool HasSelectedBackup => SelectedBackupName is not null;

    public bool IsRestorePending => RestoreSummary is not null;

    /// <summary>
    /// The explicit "I understand this will replace my current vault" acknowledgement. Two-way bound
    /// to the checkbox; gates the destructive commit. Cannot be set true without a pending preview.
    /// </summary>
    public bool RestoreAcknowledged
    {
        get => _restore.Acknowledged;
        set
        {
            if (_restore.Acknowledged == value)
            {
                return;
            }

            _restore.SetAcknowledged(value);
            OnPropertyChanged();
            ConfirmRestoreCommand.NotifyCanExecuteChanged();
        }
    }

    // Called after any change to _restore that isn't via the RestoreAcknowledged setter, to keep the
    // checkbox and the Replace button's enablement in sync.
    private void SyncRestoreConfirmation()
    {
        OnPropertyChanged(nameof(RestoreAcknowledged));
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnRestoreBackupPasswordChanged(string value)
    {
        // Changing the backup password invalidates any prepared restore, so a stale preview can
        // never be committed under a different password.
        if (_pendingRestore is not null || _restore.HasPreview)
        {
            _pendingRestore = null;
            _restore.Reset();
            RestoreSummary = null;
            SyncRestoreConfirmation();
        }
    }

    public SettingsPageViewModel(
        IVaultService vaultService,
        FileLocalVaultStorage storage,
        IClipboardService clipboard,
        IBitosXApiClient apiClient,
        Argon2Parameters kdfParameters,
        AppSettings settings,
        IAppSettingsStore settingsStore,
        AutoLockService autoLock,
        ISystemSessionEvents sessionEvents,
        IVaultBackupService backupService,
        IFilePicker filePicker)
    {
        _vaultService = vaultService;
        _settingsStore = settingsStore;
        _autoLock = autoLock;
        _backupService = backupService;
        _filePicker = filePicker;

        VaultLocation = storage.VaultFilePath;
        // Describe the vault that is actually open, not what a new vault would get.
        Argon2Parameters inUse = vaultService.KdfParameters ?? kdfParameters;
        KdfDescription = $"Argon2id — {inUse.MemoryKiB / 1024.0:0.##} MiB, {inUse.Iterations} iterations, parallelism {inUse.DegreeOfParallelism}";
        if (inUse.MemoryKiB < kdfParameters.MemoryKiB || inUse.Iterations < kdfParameters.Iterations)
        {
            KdfWarning =
                "This vault uses weaker key-derivation settings than BitosX uses for new vaults "
                + $"({kdfParameters.MemoryKiB / 1024.0:0.##} MiB, {kdfParameters.Iterations} iterations). "
                + "It is easier to attack if the file is stolen. Changing the master password does not change these settings.";
        }
        AlgorithmDescription = "AES-256-GCM (authenticated encryption)";
        ClipboardClearDescription = $"Copied secrets are cleared after {clipboard.ClearDelay.TotalSeconds:0} seconds.";
        ServerIntegrationDescription = apiClient.IsServerIntegrationEnabled
            ? "Enabled"
            : "Disabled — this build is local-only. Nothing is sent to any server.";
        AppVersion = AppVersionInfo.Display;

        SessionLockSupport = sessionEvents.IsSessionLockSupported
            ? "Supported — the vault locks when you lock your workstation."
            : "Not available on this platform.";
        SuspendSupport = sessionEvents.IsSuspendSupported
            ? "Supported — the vault locks when the system sleeps."
            : "Not available on this platform.";

        // Seed the editable controls from current settings without triggering a save.
        _loading = true;
        AutoLockOnInactivity = settings.AutoLockOnInactivity;
        InactivityMinutes = (int)Math.Round(settings.InactivityTimeout.TotalMinutes);
        LockOnMinimize = settings.LockOnMinimize;
        _loading = false;
    }

    partial void OnNewPasswordChanged(string value)
        => NewPasswordStrengthText = string.IsNullOrEmpty(value)
            ? string.Empty
            : $"Strength: {PasswordStrength.Estimate(value)}";

    partial void OnAutoLockOnInactivityChanged(bool value) => Persist();

    partial void OnInactivityMinutesChanged(int value) => Persist();

    partial void OnLockOnMinimizeChanged(bool value) => Persist();

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        int minutes = Math.Clamp(InactivityMinutes, 1, 1440);
        var updated = new AppSettings
        {
            AutoLockOnInactivity = AutoLockOnInactivity,
            InactivityTimeout = TimeSpan.FromMinutes(minutes),
            LockOnMinimize = LockOnMinimize,
        }.Normalized();

        // Apply first: the running session must honor the choice even if it cannot be saved.
        _autoLock.UpdateSettings(updated);

        try
        {
            _settingsStore.Save(updated);
            AutoLockStatus = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutoLockStatus = "These settings apply now but could not be saved, so they will be lost when BitosX closes.";
        }
    }

    [RelayCommand]
    private void LockVault() => _vaultService.Lock();

    [RelayCommand]
    private async Task ChangeMasterPasswordAsync()
    {
        ChangePasswordStatus = null;

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ChangePasswordStatus = "The new password and its confirmation do not match.";
            return;
        }

        try
        {
            await _vaultService.ChangeMasterPasswordAsync(CurrentPassword, NewPassword).ConfigureAwait(true);
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            ChangePasswordStatus = "Master password changed.";
        }
        catch (VaultAuthenticationException)
        {
            ChangePasswordStatus = "The current password is incorrect.";
        }
        catch (VaultChangedExternallyException ex)
        {
            ChangePasswordStatus = ex.Message;
        }
        catch (VaultInconsistentStateException ex)
        {
            ChangePasswordStatus = ex.Message;
        }
        catch (BitosXCryptographicException)
        {
            ChangePasswordStatus = "The vault could not be processed. The master password was not changed.";
        }
        catch (ArgumentException ex)
        {
            ChangePasswordStatus = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            ChangePasswordStatus = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            ChangePasswordStatus = "BitosX does not have permission to write the vault file. The master password was not changed.";
        }
        catch (OperationCanceledException)
        {
            ChangePasswordStatus = "The vault was locked before the change finished. Unlock it to see which password applies.";
        }
        catch (IOException)
        {
            ChangePasswordStatus = "Could not save the vault. The master password was not changed.";
        }
    }

    [RelayCommand]
    private async Task RotateVaultKeyAsync()
    {
        RotateStatus = null;

        try
        {
            await _vaultService.RotateVaultKeyAsync(RotatePassword).ConfigureAwait(true);
            RotatePassword = string.Empty;
            RotateStatus = "Vault key rotated. Your data was re-encrypted under a new key; your master password is unchanged.";
        }
        catch (VaultAuthenticationException)
        {
            RotateStatus = "The current password is incorrect. The vault key was not rotated.";
        }
        catch (VaultChangedExternallyException)
        {
            RotateStatus = "The vault changed on disk while it was open. Lock and reopen the vault before rotating the key.";
        }
        catch (VaultInconsistentStateException)
        {
            RotateStatus = "The vault could not be verified after a disk error and has been locked. Reopen it and check your data — restore from a backup if it will not open.";
        }
        catch (BitosXCryptographicException)
        {
            RotateStatus = "The vault could not be processed. The vault key was not rotated.";
        }
        catch (InvalidOperationException ex)
        {
            RotateStatus = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            RotateStatus = "BitosX does not have permission to write the vault file. The vault key was not rotated.";
        }
        catch (IOException)
        {
            RotateStatus = "Could not save the vault. The vault key was not rotated.";
        }
    }

    // ---- Backup & Restore ----

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        BackupStatus = null;
        string suggested = $"BitosX-backup-{DateTime.Now:yyyy-MM-dd}.bitosx-backup";
        string? path = await _filePicker.PickBackupSaveAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return; // user cancelled
        }

        try
        {
            await _backupService.ExportAsync(path).ConfigureAwait(true);
            BackupStatus = "Encrypted backup exported. Keep it somewhere safe — it is protected by your current master password.";
        }
        catch (InvalidOperationException ex)
        {
            BackupStatus = ex.Message;
        }
        catch (ArgumentException ex)
        {
            BackupStatus = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            BackupStatus = "BitosX does not have permission to write to that location. The vault was not changed.";
        }
        catch (IOException)
        {
            BackupStatus = "Could not write the backup file. The vault was not changed.";
        }
        catch (BitosXCryptographicException)
        {
            BackupStatus = "The current vault could not be read for export.";
        }
    }

    [RelayCommand]
    private async Task ChooseRestoreFileAsync()
    {
        RestoreStatus = null;
        RestoreSummary = null;
        _pendingRestore = null;
        _restore.Reset();
        SyncRestoreConfirmation();
        string? path = await _filePicker.PickBackupOpenAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return; // cancelled
        }

        _restoreFilePath = path;
        SelectedBackupName = Path.GetFileName(path);
        RestoreStatus = "Enter the backup's master password, then validate it.";
    }

    [RelayCommand]
    private async Task ValidateRestoreAsync()
    {
        RestoreStatus = null;
        RestoreSummary = null;
        _pendingRestore = null;
        _restore.Reset();
        SyncRestoreConfirmation();

        if (string.IsNullOrEmpty(_restoreFilePath))
        {
            RestoreStatus = "Choose a backup file first.";
            return;
        }

        try
        {
            BackupPreview preview = await _backupService
                .PrepareRestoreAsync(_restoreFilePath, RestoreBackupPassword)
                .ConfigureAwait(true);
            _pendingRestore = preview;
            _restore.SetPrepared();
            RestoreSummary =
                $"Format v{preview.FormatVersion} · {preview.ItemCount} item(s) · {preview.FileName} · {preview.FileSizeBytes} bytes";
            SyncRestoreConfirmation();
        }
        catch (VaultAuthenticationException)
        {
            RestoreStatus = "The backup password is incorrect, or the backup is corrupted.";
        }
        catch (UnsupportedVaultFormatException)
        {
            RestoreStatus = "This file is not a supported BitosX backup.";
        }
        catch (BitosXCryptographicException)
        {
            RestoreStatus = "The backup could not be validated.";
        }
        catch (VaultValidationException)
        {
            RestoreStatus = "The backup's contents are invalid.";
        }
        catch (InvalidOperationException ex)
        {
            RestoreStatus = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            RestoreStatus = "BitosX does not have permission to read the backup file.";
        }
        catch (IOException)
        {
            RestoreStatus = "Could not read the backup file.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmRestore))]
    private async Task ConfirmRestoreAsync()
    {
        // Commit-side guard (defense in depth): the destructive commit enforces its own invariant and
        // must not rely on CanExecute / UI enablement. Fail closed if the state is not exactly right.
        if (_pendingRestore is null || !RestoreAcknowledged)
        {
            return;
        }

        BackupPreview preview = _pendingRestore;
        try
        {
            await _backupService.CommitRestoreAsync(preview).ConfigureAwait(true);
            // On success the vault locks and the shell navigates to the Unlock screen automatically.
        }
        catch (UnauthorizedAccessException)
        {
            RestoreStatus = "BitosX does not have permission to replace the vault. Your current vault is unchanged.";
        }
        catch (IOException)
        {
            RestoreStatus = "Could not replace the vault. Your current vault is unchanged.";
        }
        catch (InvalidOperationException ex)
        {
            RestoreStatus = ex.Message;
        }
        finally
        {
            // Reset after success or failure so no stale, acknowledged state survives.
            CancelRestore();
        }
    }

    private bool CanConfirmRestore() => _restore.CanCommit;

    [RelayCommand]
    private void CancelRestore()
    {
        _pendingRestore = null;
        _restoreFilePath = null;
        SelectedBackupName = null;
        RestoreSummary = null;
        RestoreBackupPassword = string.Empty;
        _restore.Reset();
        SyncRestoreConfirmation();
    }
}
