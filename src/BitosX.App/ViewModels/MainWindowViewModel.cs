using Avalonia.Threading;
using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace BitosX.App.ViewModels;

/// <summary>
/// Top-level shell view model. Switches the visible screen based on the vault lifecycle state:
/// no vault -> create, locked -> unlock, unlocked -> the main application shell.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IVaultService _vaultService;
    private bool _disposed;

    [ObservableProperty]
    public partial ViewModelBase? Content { get; set; }

    public MainWindowViewModel(IServiceProvider services, IVaultService vaultService)
    {
        _services = services;
        _vaultService = vaultService;
        _vaultService.StateChanged += OnStateChanged;
    }

    /// <summary>Determines the initial screen from storage. Call once after the window is shown.</summary>
    public async Task InitializeAsync()
    {
        await _vaultService.RefreshStateAsync().ConfigureAwait(true);
        UpdateContent(_vaultService.State);
    }

    private void OnStateChanged(object? sender, VaultState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateContent(state);
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdateContent(state));
        }
    }

    private void UpdateContent(VaultState state)
    {
        (Content as IDisposable)?.Dispose();

        Content = state switch
        {
            VaultState.Unlocked => ActivatorUtilities.CreateInstance<UnlockedShellViewModel>(_services),
            VaultState.Locked => ActivatorUtilities.CreateInstance<UnlockViewModel>(_services),
            _ => ActivatorUtilities.CreateInstance<CreateVaultViewModel>(_services),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vaultService.StateChanged -= OnStateChanged;
        (Content as IDisposable)?.Dispose();
    }
}
