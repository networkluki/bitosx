using BitosX.Platform.Session;
using Microsoft.Extensions.Logging;

namespace BitosX.Platform.Clipboard;

/// <summary>
/// Clears a secret BitosX copied as soon as the operating system reports that the user is leaving:
/// the workstation is being locked, or the system is going to sleep. The timed clear alone is not
/// enough for these — timers stop while a system is suspended, so a secret copied just before the
/// lid is closed would still be on the clipboard when it is opened again.
/// </summary>
/// <remarks>
/// This acts on the OS signals only, not on the vault locking: locking the vault (manually, on
/// minimize, or on inactivity) deliberately leaves a just-copied value on the clipboard for the rest
/// of its normal lifetime, because "copy, switch to the other window, paste" is the intended use.
/// Like the timed clear, it only removes the value BitosX copied, never content the user has since
/// replaced. Only effective where <see cref="ISystemSessionEvents"/> can observe the signals.
/// </remarks>
public sealed class ClipboardSessionGuard : IDisposable
{
    private readonly ISystemSessionEvents _sessionEvents;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<ClipboardSessionGuard> _logger;
    private bool _disposed;

    public ClipboardSessionGuard(
        ISystemSessionEvents sessionEvents,
        IClipboardService clipboard,
        ILogger<ClipboardSessionGuard> logger)
    {
        _sessionEvents = sessionEvents ?? throw new ArgumentNullException(nameof(sessionEvents));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _sessionEvents.SessionLocked += OnUserLeaving;
        _sessionEvents.Suspending += OnUserLeaving;
    }

    // Raised on an OS thread; must return promptly and never throw into it.
    private void OnUserLeaving(object? sender, EventArgs e) => _ = ClearAsync();

    private async Task ClearAsync()
    {
        if (!_clipboard.HasPendingSecret)
        {
            return;
        }

        try
        {
            await _clipboard.ClearIfUnchangedAsync().ConfigureAwait(false);
            ClipboardLog.ClearedOnSessionEvent(_logger);
        }
        catch (Exception ex)
        {
            ClipboardLog.ClearFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionEvents.SessionLocked -= OnUserLeaving;
        _sessionEvents.Suspending -= OnUserLeaving;
    }
}
