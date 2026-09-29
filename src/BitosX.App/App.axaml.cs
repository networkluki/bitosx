using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Diagnostics;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BitosX.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ServiceConfiguration.Build();

            ILogger logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("BitosX.App");
            VaultLog.ApplicationStarted(logger);

            MainWindowViewModel viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };

            AutoLockService autoLock = _services.GetRequiredService<AutoLockService>();
            autoLock.Attach(window);

            // Clipboard hygiene beyond the timed clear: on workstation lock / suspend, and on exit.
            ClipboardSessionGuard clipboardGuard = _services.GetRequiredService<ClipboardSessionGuard>();
            _services.GetRequiredService<ExitClipboardCleaner>().Attach(window);

            window.Opened += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                clipboardGuard.Dispose();
                autoLock.Dispose();
                viewModel.Dispose();
                _services?.Dispose(); // also wipes the vault key held by the vault service
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
