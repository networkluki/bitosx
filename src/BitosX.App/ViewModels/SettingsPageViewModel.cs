using System.Reflection;
using BitosX.ApiClient;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Storage;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The Settings page: read-only environment and security information for this milestone.</summary>
public sealed partial class SettingsPageViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;

    public string VaultLocation { get; }
    public string KdfDescription { get; }
    public string AlgorithmDescription { get; }
    public string ClipboardClearDescription { get; }
    public string ServerIntegrationDescription { get; }
    public string AppVersion { get; }

    public SettingsPageViewModel(
        IVaultService vaultService,
        FileLocalVaultStorage storage,
        IClipboardService clipboard,
        IBitosXApiClient apiClient,
        Argon2Parameters kdfParameters)
    {
        _vaultService = vaultService;

        VaultLocation = storage.VaultFilePath;
        KdfDescription = $"Argon2id — {kdfParameters.MemoryKiB / 1024} MiB, {kdfParameters.Iterations} iterations, parallelism {kdfParameters.DegreeOfParallelism}";
        AlgorithmDescription = "AES-256-GCM (authenticated encryption)";
        ClipboardClearDescription = $"Copied secrets are cleared after {clipboard.ClearDelay.TotalSeconds:0} seconds (configurable in a future release).";
        ServerIntegrationDescription = apiClient.IsServerIntegrationEnabled
            ? "Enabled"
            : "Disabled — this build is local-only. Nothing is sent to any server.";
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
    }

    [RelayCommand]
    private void LockVault() => _vaultService.Lock();
}
