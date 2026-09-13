using System.Reflection;
using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
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
    private bool _loading;

    public string VaultLocation { get; }
    public string KdfDescription { get; }
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
    public partial string CurrentPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ChangePasswordStatus { get; set; }

    public SettingsPageViewModel(
        IVaultService vaultService,
        FileLocalVaultStorage storage,
        IClipboardService clipboard,
        IBitosXApiClient apiClient,
        Argon2Parameters kdfParameters,
        AppSettings settings,
        IAppSettingsStore settingsStore,
        AutoLockService autoLock,
        ISystemSessionEvents sessionEvents)
    {
        _vaultService = vaultService;
        _settingsStore = settingsStore;
        _autoLock = autoLock;

        VaultLocation = storage.VaultFilePath;
        KdfDescription = $"Argon2id — {kdfParameters.MemoryKiB / 1024} MiB, {kdfParameters.Iterations} iterations, parallelism {kdfParameters.DegreeOfParallelism}";
        AlgorithmDescription = "AES-256-GCM (authenticated encryption)";
        ClipboardClearDescription = $"Copied secrets are cleared after {clipboard.ClearDelay.TotalSeconds:0} seconds.";
        ServerIntegrationDescription = apiClient.IsServerIntegrationEnabled
            ? "Enabled"
            : "Disabled — this build is local-only. Nothing is sent to any server.";
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0.0";

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

        _settingsStore.Save(updated);
        _autoLock.UpdateSettings(updated);
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
        catch (IOException)
        {
            ChangePasswordStatus = "Could not save the vault. The master password was not changed.";
        }
    }
}
