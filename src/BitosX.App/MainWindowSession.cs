using Avalonia.Controls;
using BitosX.App.Services;
using BitosX.ApiClient;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.DependencyInjection;

namespace BitosX.App;

/// <summary>The window the services find when they need one: for the clipboard and for file dialogs.</summary>
public sealed class MainWindowReference
{
    /// <summary>The main window, once it has been created.</summary>
    public Window? Window { get; set; }
}

/// <summary>
/// The main window together with everything that is attached to it: the view model, automatic
/// locking, and clearing the clipboard when the user leaves or the window is closed. This is the
/// one place where that is put together, so the application and the tests that run the whole
/// application use the same composition.
/// </summary>
public sealed class MainWindowSession : IDisposable
{
    /// <summary>How long closing BitosX waits for the server to hear that the account session ended.</summary>
    public static readonly TimeSpan ExitSignOutBudget = TimeSpan.FromSeconds(2);

    private readonly MainWindowViewModel _viewModel;
    private readonly AutoLockService _autoLock;
    private readonly ClipboardSessionGuard _clipboardGuard;
    private readonly UnexpectedErrorGuard _errorGuard;
    private readonly IAccountSession _account;
    private bool _disposed;

    private MainWindowSession(
        MainWindow window,
        MainWindowViewModel viewModel,
        AutoLockService autoLock,
        ClipboardSessionGuard clipboardGuard,
        UnexpectedErrorGuard errorGuard,
        IAccountSession account)
    {
        Window = window;
        _viewModel = viewModel;
        _autoLock = autoLock;
        _clipboardGuard = clipboardGuard;
        _errorGuard = errorGuard;
        _account = account;
    }

    /// <summary>The main window. Showing it decides the first screen from what is in storage.</summary>
    public MainWindow Window { get; }

    /// <summary>Creates the main window on top of <paramref name="services"/>.</summary>
    public static MainWindowSession Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        MainWindowViewModel viewModel = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Title = services.GetRequiredService<AppLocation>().WindowTitle,
        };
        services.GetRequiredService<MainWindowReference>().Window = window;

        AutoLockService autoLock = services.GetRequiredService<AutoLockService>();
        autoLock.Attach(window);

        // Clipboard hygiene beyond the timed clear: on workstation lock / suspend, and on exit.
        ClipboardSessionGuard clipboardGuard = services.GetRequiredService<ClipboardSessionGuard>();
        services.GetRequiredService<ExitClipboardCleaner>().Attach(window);

        // A fault nobody foresaw locks the vault instead of ending the application.
        UnexpectedErrorGuard errorGuard = services.GetRequiredService<UnexpectedErrorGuard>();
        errorGuard.Attach();

        // The account session follows the vault: it is created here so that it is listening when
        // the vault locks, and it is forgotten when the window closes.
        IAccountSession account = services.GetRequiredService<IAccountSession>();

        window.Opened += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true);
        return new MainWindowSession(window, viewModel, autoLock, clipboardGuard, errorGuard, account);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing BitosX ends the account session on the server too, best effort and briefly: the
        // tokens are forgotten at once, and the server is given a couple of seconds to hear about it.
        try
        {
            using var budget = new CancellationTokenSource(ExitSignOutBudget);
            _account.EndSessionAsync(budget.Token).Wait(ExitSignOutBudget + TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // EndSessionAsync does not throw; nothing here may keep the application from closing.
        }

        (_account as IDisposable)?.Dispose();
        _errorGuard.Dispose();
        _clipboardGuard.Dispose();
        _autoLock.Dispose();
        _viewModel.Dispose();
    }
}
