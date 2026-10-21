using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using BitosX.App.Services;
using BitosX.App.Views;
using BitosX.Core.Services;
using BitosX.Platform.Clipboard;
using BitosX.Platform.Files;
using BitosX.Platform.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BitosX.App.Tests;

/// <summary>
/// The whole application, put together the way <see cref="App"/> does it — the same services, the
/// same main window, the same cryptography with the same settings, the same file storage — in a
/// data directory of its own and without a display.
/// </summary>
/// <remarks>
/// Replaced, because they reach outside the process: the operating system's session signals (so a
/// test can lock the workstation), the clock that automatic locking reads (so a test need not wait
/// a minute, and the policy is asked every tenth of a second instead of every fifteen seconds),
/// the file dialogs, and how long a copied value stays on the clipboard.
/// </remarks>
internal sealed class WholeApplication : IAsyncDisposable
{
    private ServiceProvider? _services;
    private MainWindowSession? _session;

    public WholeApplication()
    {
        Directory = Path.Combine(Path.GetTempPath(), "BitosX.WholeApp", Guid.NewGuid().ToString("N"));
        Location = AppLocation.In(Directory);
    }

    /// <summary>The data directory. It does not exist until the application writes to it.</summary>
    public string Directory { get; }

    public AppLocation Location { get; }

    public SessionSignals Signals { get; } = new();

    public MovableClock Clock { get; } = new();

    public ScriptedFilePicker Files { get; } = new();

    /// <summary>The BitosX server, scripted: one account, two sessions, no network.</summary>
    public ScriptedServer Server { get; } = new();

    /// <summary>How long a copied value stays on the clipboard; 30 seconds in the application.</summary>
    public TimeSpan ClipboardDelay { get; set; } = TimeSpan.FromSeconds(30);

    public MainWindow Window => _session?.Window ?? throw new InvalidOperationException("The application is not running.");

    public IServiceProvider Services => _services ?? throw new InvalidOperationException("The application is not running.");

    public IVaultService Vault => Services.GetRequiredService<IVaultService>();

    /// <summary>What the window shows: the create, unlock or main screen.</summary>
    public Control? Screen => Window.GetVisualDescendants()
        .OfType<UserControl>()
        .FirstOrDefault(c => c is CreateVaultView or UnlockView or UnlockedShellView);

    /// <summary>The page shown in the main screen.</summary>
    public Control? Page => Window.GetVisualDescendants()
        .OfType<UserControl>()
        .FirstOrDefault(c => c is VaultPageView or GeneratorPageView or SecurityPageView or SettingsPageView or AccountPageView);

    /// <summary>The vault file as it is on disk.</summary>
    public byte[] VaultFile => File.ReadAllBytes(Location.VaultFilePath);

    /// <summary>Starts the application and waits for its first screen.</summary>
    public async Task StartAsync(double width = 1000, double height = 680)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("The application is already running.");
        }

        _services = ServiceConfiguration.Build(Location, services =>
        {
            services.AddSingleton<ISystemSessionEvents>(Signals);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IFilePicker>(Files);
            services.AddSingleton<BitosX.ApiClient.IBitosXApiClient>(Server);
            services.AddSingleton(sp => new AutoLockService(
                sp.GetRequiredService<AutoLockController>(),
                sp.GetRequiredService<ISystemSessionEvents>(),
                TimeSpan.FromMilliseconds(100)));
            services.AddSingleton<IClipboardService>(sp => new ClipboardService(
                sp.GetRequiredService<IClipboardAccessor>(),
                sp.GetRequiredService<ILogger<ClipboardService>>(),
                ClipboardDelay));
        });

        _session = MainWindowSession.Create(_services);
        Window.Width = width;
        Window.Height = height;
        Window.Show();
        await HeadlessUi.WaitUntilAsync(() => Screen is not null, "the first screen is shown").ConfigureAwait(true);
    }

    /// <summary>Closes the window and ends the application, as closing it on a desktop does.</summary>
    public async Task StopAsync()
    {
        if (_session is null)
        {
            return;
        }

        MainWindow window = _session.Window;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        await HeadlessUi.WaitUntilAsync(() => closed, "the window closes").ConfigureAwait(true);

        _session.Dispose();
        _session = null;
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(true);
            _services = null;
        }
    }

    /// <summary>Closes the application and starts it again on the same data directory.</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    public T Find<T>(string name)
        where T : Control
        => HeadlessUi.Find<T>(Window, name);

    /// <summary>The names of the items in the vault list, from the top.</summary>
    public IReadOnlyList<string> ItemNames =>
    [
        .. Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Name == "ItemName" && t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty),
    ];

    /// <summary>The card of the item called <paramref name="itemName"/> in the vault list.</summary>
    public Control Item(string itemName)
    {
        TextBlock[] found =
        [
            .. Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "ItemName" && t.Text == itemName && t.IsEffectivelyVisible),
        ];
        if (found.Length != 1)
        {
            throw new InvalidOperationException($"Expected one item '{itemName}' in the list, found {found.Length}.");
        }

        return found[0].GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
    }

    /// <summary>The control called <paramref name="name"/> on the card of an item.</summary>
    public T FindInItem<T>(string itemName, string name)
        where T : Control
        => HeadlessUi.Find<T>(Item(itemName), name);

    public string? Text(string name) => Find<TextBlock>(name).Text;

    public bool Shows(string name)
        => Window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    public void Type(string textBoxName, string text) => HeadlessUi.Retype(Window, Find<TextBox>(textBoxName), text);

    public void Press(string buttonName) => HeadlessUi.Press(Window, Find<Button>(buttonName));

    public void Click(string controlName) => HeadlessUi.Click(Window, Find<Control>(controlName));

    public Task WaitForAsync<TScreen>()
        where TScreen : Control
        => HeadlessUi.WaitUntilAsync(
            () => Screen is TScreen || Page is TScreen,
            $"the {typeof(TScreen).Name} is shown");

    public Task WaitForTextAsync(string name, string text)
        => HeadlessUi.WaitUntilAsync(
            () => Shows(name) && (Text(name)?.Contains(text, StringComparison.Ordinal) ?? false),
            $"'{name}' says \"{text}\"");

    /// <summary>What is on the clipboard of the window.</summary>
    public async Task<string?> ClipboardTextAsync()
        => await Window.Clipboard!.TryGetTextAsync().ConfigureAwait(true);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(true);
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The operating system's session signals, raised by the test.</summary>
    public sealed class SessionSignals : ISystemSessionEvents
    {
        public bool IsSessionLockSupported => true;

        public bool IsSuspendSupported => true;

        public bool Started { get; private set; }

        public event EventHandler? SessionLocked;

        public event EventHandler? Suspending;

        public void Start() => Started = true;

        /// <summary>The workstation is locked. Raised on another thread, as the operating system does.</summary>
        public Task LockWorkstationAsync() => Task.Run(() => SessionLocked?.Invoke(this, EventArgs.Empty));

        /// <summary>The system is going to sleep. Raised on another thread, as the operating system does.</summary>
        public Task SuspendAsync() => Task.Run(() => Suspending?.Invoke(this, EventArgs.Empty));

        public void Dispose()
        {
        }
    }

    /// <summary>The real clock, which can be moved forward.</summary>
    public sealed class MovableClock : TimeProvider
    {
        private TimeSpan _ahead;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _ahead;

        public void MoveForward(TimeSpan time) => _ahead += time;
    }

    /// <summary>File dialogs that answer what the test tells them to, and count how often they are asked.</summary>
    public sealed class ScriptedFilePicker : IFilePicker
    {
        public string? SavePath { get; set; }

        public string? OpenPath { get; set; }

        public int TimesAsked { get; private set; }

        public Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            TimesAsked++;
            return Task.FromResult(SavePath);
        }

        public Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default)
        {
            TimesAsked++;
            return Task.FromResult(OpenPath);
        }
    }
}
