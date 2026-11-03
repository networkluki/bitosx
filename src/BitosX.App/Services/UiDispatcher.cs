using Avalonia.Threading;

namespace BitosX.App.Services;

/// <summary>
/// Where a view model applies an update that a service reports from another thread. The account
/// session, for one, raises its events on whichever thread a server call completed on; anything
/// bound to the window must change on the UI thread.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> now when called on the UI thread, otherwise queues it there.</summary>
    void Run(Action action);
}

/// <summary>The application's UI thread, through Avalonia's dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
