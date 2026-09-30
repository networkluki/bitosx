using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BitosX.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BitosX.App;

public partial class App : Application
{
    private readonly AppLocation _location;
    private ServiceProvider? _services;

    /// <summary>The application at the default per-user location. Also used by the visual designer.</summary>
    public App()
        : this(AppLocation.Default)
    {
    }

    /// <summary>The application with its files at <paramref name="location"/>.</summary>
    public App(AppLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ServiceConfiguration.Build(_location);

            ILogger logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("BitosX.App");
            VaultLog.ApplicationStarted(logger);

            MainWindowSession session = MainWindowSession.Create(_services);
            desktop.MainWindow = session.Window;
            desktop.ShutdownRequested += (_, _) =>
            {
                session.Dispose();
                _services?.Dispose(); // also wipes the vault key held by the vault service
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
