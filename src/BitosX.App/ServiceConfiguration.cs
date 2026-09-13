using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Generation;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Core.Storage;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Platform.SecureStorage;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BitosX.App;

/// <summary>Composition root: wires up services and view models for dependency injection.</summary>
public static class ServiceConfiguration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Cryptography
        services.AddSingleton(VaultCryptor.CreateDefault());
        services.AddSingleton(Argon2Parameters.Default);

        // Storage (default per-user location)
        services.AddSingleton(FileLocalVaultStorage.CreateDefault());
        services.AddSingleton<ILocalVaultStorage>(sp => sp.GetRequiredService<FileLocalVaultStorage>());

        // Core services
        services.AddSingleton<IVaultService, VaultService>();
        services.AddSingleton<IPasswordGenerator, PasswordGenerator>();
        services.AddSingleton<ISecurityAnalyzer, SecurityAnalyzer>();

        // Settings (non-secret preferences, stored separately from the vault)
        services.AddSingleton<IAppSettingsStore>(_ => FileAppSettingsStore.CreateDefault());
        services.AddSingleton(sp => sp.GetRequiredService<IAppSettingsStore>().Load());
        services.AddSingleton(TimeProvider.System);

        // Platform
        services.AddSingleton<IClipboardAccessor>(_ => new AvaloniaClipboardAccessor(GetMainWindowClipboard));
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ISecureStorage, UnavailableSecureStorage>();
        services.AddSingleton<ISystemSessionEvents>(_ =>
            OperatingSystem.IsWindows() ? new WindowsSystemSessionEvents() : new NullSystemSessionEvents());

        // Auto-lock (policy in Core, Avalonia glue in the App)
        services.AddSingleton<AutoLockController>();
        services.AddSingleton<AutoLockService>();

        // API (local-only abstraction)
        services.AddSingleton<IBitosXApiClient, LocalOnlyApiClient>();

        // View models
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private static IClipboard? GetMainWindowClipboard()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
}
