using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Generation;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Core.Storage;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Platform.Files;
using BitosX.Platform.SecureStorage;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BitosX.App;

/// <summary>Composition root: wires up services and view models for dependency injection.</summary>
public static class ServiceConfiguration
{
    /// <summary>The services of the application at the default per-user location, on the production server.</summary>
    public static ServiceProvider Build() => Build(AppLocation.Default, ApiServer.Production);

    /// <summary>The services of the application with its files at <paramref name="location"/>, on the production server.</summary>
    /// <param name="location">Where the vault and the settings are kept.</param>
    /// <param name="configure">Replaces services after the defaults are registered; for tests.</param>
    public static ServiceProvider Build(AppLocation location, Action<IServiceCollection>? configure = null)
        => Build(location, ApiServer.Production, configure);

    /// <summary>The services of the application with its files at <paramref name="location"/>.</summary>
    /// <param name="location">Where the vault and the settings are kept.</param>
    /// <param name="server">The BitosX server to talk to, or <see langword="null"/> for a local-only run.</param>
    /// <param name="configure">Replaces services after the defaults are registered; for tests.</param>
    public static ServiceProvider Build(AppLocation location, ApiServer? server, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(location);

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton(location);

        // Cryptography
        services.AddSingleton(VaultCryptor.CreateDefault());
        // What new vaults are created with; an existing vault keeps its own settings.
        services.AddSingleton(KeyDerivationProfile.ForNewVaults.Parameters);

        // Storage
        services.AddSingleton(new FileLocalVaultStorage(location.VaultFilePath));
        services.AddSingleton<ILocalVaultStorage>(sp => sp.GetRequiredService<FileLocalVaultStorage>());
        services.AddSingleton<IVaultBackup>(sp => new FileVaultBackup(sp.GetRequiredService<FileLocalVaultStorage>().VaultFilePath));

        // Core services
        services.AddSingleton<IVaultService>(sp => new VaultService(
            sp.GetRequiredService<VaultCryptor>(),
            sp.GetRequiredService<ILocalVaultStorage>(),
            sp.GetRequiredService<Argon2Parameters>(),
            fallbackCreationParameters: KeyDerivationProfile.Standard.Parameters,
            sp.GetRequiredService<ILogger<VaultService>>()));
        services.AddSingleton<IVaultBackupService, VaultBackupService>();
        services.AddSingleton<IPasswordGenerator, PasswordGenerator>();
        services.AddSingleton<GeneratorSettings>();
        services.AddSingleton<ISecurityAnalyzer, SecurityAnalyzer>();
        services.AddSingleton<IKeyDerivationBenchmark>(_ => new KeyDerivationBenchmark(new Argon2idKeyDerivation()));

        // Settings (non-secret preferences, stored separately from the vault)
        services.AddSingleton<IAppSettingsStore>(_ => new FileAppSettingsStore(location.SettingsFilePath));
        services.AddSingleton(sp => sp.GetRequiredService<IAppSettingsStore>().Load());
        services.AddSingleton(TimeProvider.System);

        // Platform
        services.AddSingleton<MainWindowReference>();
        services.AddSingleton<IClipboardAccessor>(sp =>
        {
            MainWindowReference mainWindow = sp.GetRequiredService<MainWindowReference>();
            return new AvaloniaClipboardAccessor(
                () => mainWindow.Window?.Clipboard,
                SensitiveClipboardData.CurrentPlatform,
                sp.GetRequiredService<ILogger<AvaloniaClipboardAccessor>>());
        });
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ClipboardSessionGuard>();
        services.AddSingleton<ExitClipboardCleaner>();
        services.AddSingleton<AppNotice>();
        services.AddSingleton<UnexpectedErrorGuard>();
        services.AddSingleton<ISecureStorage, UnavailableSecureStorage>();
        services.AddSingleton<IFilePicker>(sp =>
        {
            MainWindowReference mainWindow = sp.GetRequiredService<MainWindowReference>();
            return new AvaloniaFilePicker(() => mainWindow.Window);
        });
        services.AddSingleton<ILinkOpener>(sp =>
        {
            MainWindowReference mainWindow = sp.GetRequiredService<MainWindowReference>();
            return new AvaloniaLinkOpener(() => mainWindow.Window);
        });
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ISystemSessionEvents>(_ =>
            OperatingSystem.IsWindows() ? new WindowsSystemSessionEvents() : new NullSystemSessionEvents());

        // Auto-lock (policy in Core, Avalonia glue in the App)
        services.AddSingleton<AutoLockController>();
        services.AddSingleton<AutoLockService>();

        // The BitosX server: account sign-in, and the encrypted envelope up and down (docs/SYNC.md).
        // Nothing decrypted ever goes through it.
        services.AddSingleton<IBitosXApiClient>(sp => server is null
            ? new LocalOnlyApiClient()
            : HttpBitosXApiClient.Create(server, AppVersionInfo.Display, sp.GetRequiredService<ILogger<HttpBitosXApiClient>>()));
        services.AddSingleton<IAccountSession>(sp => new AccountSession(
            sp.GetRequiredService<IBitosXApiClient>(),
            sp.GetRequiredService<IVaultService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AccountSession>>()));
        services.AddSingleton<ISyncStateStore>(_ => new FileSyncStateStore(location.SyncStateFilePath));
        services.AddSingleton<IVaultSyncService>(sp => new VaultSyncService(
            sp.GetRequiredService<IAccountSession>(),
            sp.GetRequiredService<IVaultService>(),
            sp.GetRequiredService<ILocalVaultStorage>(),
            sp.GetRequiredService<IVaultBackupService>(),
            sp.GetRequiredService<ISyncStateStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<VaultSyncService>>()));

        // View models
        services.AddSingleton<MainWindowViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
