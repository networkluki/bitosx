using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace BitosX.App.ViewModels;

/// <summary>Main application shell shown when the vault is unlocked. Hosts navigation and pages.</summary>
/// <remarks>
/// Disposed when the vault locks (the main window disposes the content it replaces); it disposes the
/// pages it created, so none of them keeps listening to the account session, which outlives them.
/// </remarks>
public sealed partial class UnlockedShellViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IVaultService _vaultService;

    private VaultPageViewModel? _vaultPage;
    private GeneratorPageViewModel? _generatorPage;
    private SecurityPageViewModel? _securityPage;
    private SettingsPageViewModel? _settingsPage;
    private AccountPageViewModel? _accountPage;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultSelected))]
    [NotifyPropertyChangedFor(nameof(IsGeneratorSelected))]
    [NotifyPropertyChangedFor(nameof(IsSecuritySelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAccountSelected))]
    [NotifyPropertyChangedFor(nameof(VaultNavName))]
    [NotifyPropertyChangedFor(nameof(GeneratorNavName))]
    [NotifyPropertyChangedFor(nameof(SecurityNavName))]
    [NotifyPropertyChangedFor(nameof(SettingsNavName))]
    [NotifyPropertyChangedFor(nameof(AccountNavName))]
    public partial ViewModelBase? CurrentPage { get; set; }

    public bool IsVaultSelected => CurrentPage is VaultPageViewModel;
    public bool IsGeneratorSelected => CurrentPage is GeneratorPageViewModel;
    public bool IsSecuritySelected => CurrentPage is SecurityPageViewModel;
    public bool IsSettingsSelected => CurrentPage is SettingsPageViewModel;
    public bool IsAccountSelected => CurrentPage is AccountPageViewModel;

    // What a screen reader says for each navigation button: the page, and which one is shown — the
    // highlight alone is something only the eye sees.
    public string VaultNavName => NavName("Vault", IsVaultSelected);
    public string GeneratorNavName => NavName("Generator", IsGeneratorSelected);
    public string SecurityNavName => NavName("Security Center", IsSecuritySelected);
    public string SettingsNavName => NavName("Settings", IsSettingsSelected);
    public string AccountNavName => NavName("Account", IsAccountSelected);

    private static string NavName(string page, bool selected) => selected ? page + ", current page" : page;

    public UnlockedShellViewModel(IServiceProvider services, IVaultService vaultService)
    {
        _services = services;
        _vaultService = vaultService;
        ShowVault();
    }

    [RelayCommand]
    private void ShowVault()
    {
        _vaultPage ??= ActivatorUtilities.CreateInstance<VaultPageViewModel>(_services);
        _ = _vaultPage.RefreshAsync();
        CurrentPage = _vaultPage;
    }

    [RelayCommand]
    private void ShowGenerator()
    {
        _generatorPage ??= ActivatorUtilities.CreateInstance<GeneratorPageViewModel>(_services);
        CurrentPage = _generatorPage;
    }

    [RelayCommand]
    private void ShowSecurity()
    {
        _securityPage ??= ActivatorUtilities.CreateInstance<SecurityPageViewModel>(_services);
        _securityPage.Analyze();
        CurrentPage = _securityPage;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        _settingsPage ??= ActivatorUtilities.CreateInstance<SettingsPageViewModel>(_services);
        CurrentPage = _settingsPage;
    }

    [RelayCommand]
    private void ShowAccount()
    {
        _accountPage ??= ActivatorUtilities.CreateInstance<AccountPageViewModel>(_services);
        _ = _accountPage.RefreshAsync();
        CurrentPage = _accountPage;
    }

    [RelayCommand]
    private void Lock() => _vaultService.Lock();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (object? page in new object?[] { _vaultPage, _generatorPage, _securityPage, _settingsPage, _accountPage })
        {
            (page as IDisposable)?.Dispose();
        }
    }
}
