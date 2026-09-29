using Avalonia.Controls;
using Avalonia.Threading;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.Logging;

namespace BitosX.App.Services;

/// <summary>
/// Clears a copied secret from the clipboard when the application is closed. The timed clear lives
/// in the process; without this, a value copied less than <see cref="IClipboardService.ClearDelay"/>
/// before closing the window would stay on the clipboard indefinitely.
/// </summary>
/// <remarks>
/// Clipboard access is asynchronous, so the first close request is held back for the moment it
/// takes to clear, and the window is then closed for real. The wait is bounded: a clipboard that
/// does not respond never prevents the application from closing. As with the timed clear, only the
/// value BitosX copied is removed, never content the user has since replaced.
/// </remarks>
public sealed partial class ExitClipboardCleaner
{
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(2);

    private readonly IClipboardService _clipboard;
    private readonly ILogger<ExitClipboardCleaner> _logger;
    private bool _handled;

    public ExitClipboardCleaner(IClipboardService clipboard, ILogger<ExitClipboardCleaner> logger)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Clears the clipboard when <paramref name="window"/> is closed.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_handled || sender is not Window window || !_clipboard.HasPendingSecret)
        {
            return;
        }

        _handled = true;

        // The operating system is shutting down: do not stand in its way, just try.
        bool hold = e.CloseReason != WindowCloseReason.OSShutdown;
        if (hold)
        {
            e.Cancel = true;
        }

        try
        {
            await _clipboard.ClearIfUnchangedAsync().WaitAsync(ClearTimeout).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogClearFailed(_logger, ex);
        }
        finally
        {
            if (hold)
            {
                // Posted rather than called, so it never runs inside the close request being cancelled.
                Dispatcher.UIThread.Post(window.Close);
            }
        }
    }

    [LoggerMessage(EventId = 1110, EventName = "clipboard_clear_on_exit_failed", Level = LogLevel.Warning,
        Message = "Clearing the clipboard on exit failed.")]
    private static partial void LogClearFailed(ILogger logger, Exception exception);
}
