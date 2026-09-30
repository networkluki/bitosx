using Avalonia.Threading;
using BitosX.App.ViewModels;
using BitosX.Core.Services;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.Logging;

namespace BitosX.App.Services;

/// <summary>
/// What happens when a fault nobody foresaw reaches the user interface. Left alone, such an
/// exception ends the application on the spot — and with it the timer that was going to clear a
/// copied secret from the clipboard, which would then stay there. Instead the application fails
/// closed and keeps running: the vault is locked, a copied secret is cleared, and the user is told.
/// </summary>
/// <remarks>
/// <para>
/// Locking discards the key and every screen that showed vault contents; the unlock screen that
/// follows is built afresh. Nothing is written: what is on disk is what the last completed save
/// left there.
/// </para>
/// <para>
/// A fault that keeps coming back cannot be recovered from this way. After
/// <see cref="MaxFaults"/> within <see cref="FaultWindow"/> the next one is let through, and ends
/// the application as it would have without this class.
/// </para>
/// </remarks>
public sealed partial class UnexpectedErrorGuard : IDisposable
{
    /// <summary>How many faults are recovered from within <see cref="FaultWindow"/>.</summary>
    public const int MaxFaults = 3;

    /// <summary>The time within which faults are counted together.</summary>
    public static readonly TimeSpan FaultWindow = TimeSpan.FromSeconds(10);

    /// <summary>What the user is told.</summary>
    public const string Notice =
        "Something went wrong in BitosX, so the vault was locked as a precaution. "
        + "The vault file is as it was last saved. Unlock the vault to continue.";

    private readonly IVaultService _vault;
    private readonly IClipboardService _clipboard;
    private readonly AppNotice _notice;
    private readonly TimeProvider _time;
    private readonly ILogger<UnexpectedErrorGuard> _logger;
    private readonly Queue<DateTimeOffset> _faults = new();
    private bool _attached;

    public UnexpectedErrorGuard(
        IVaultService vault,
        IClipboardService clipboard,
        AppNotice notice,
        TimeProvider time,
        ILogger<UnexpectedErrorGuard> logger)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _notice = notice ?? throw new ArgumentNullException(nameof(notice));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts handling faults that reach the UI thread.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => e.Handled = Recover(e.Exception);

    /// <summary>
    /// Fails closed after <paramref name="fault"/>. Returns whether the application may go on; when
    /// it may not, the fault is to be let through.
    /// </summary>
    public bool Recover(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        // Fail closed first, and whatever else happens.
        try
        {
            _vault.Lock();
        }
        catch (Exception ex)
        {
            LogRecoveryFailed(_logger, ex);
        }

        _ = ClearClipboardAsync();

        DateTimeOffset now = _time.GetUtcNow();
        while (_faults.Count > 0 && now - _faults.Peek() > FaultWindow)
        {
            _faults.Dequeue();
        }

        // The type only: a message could quote what the user typed.
        string kind = fault.GetType().Name;

        _faults.Enqueue(now);
        if (_faults.Count > MaxFaults)
        {
            LogGivingUp(_logger, kind);
            return false;
        }

        LogRecovered(_logger, kind);
        _notice.Show(Notice);
        return true;
    }

    private async Task ClearClipboardAsync()
    {
        if (!_clipboard.HasPendingSecret)
        {
            return;
        }

        try
        {
            await _clipboard.ClearIfUnchangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRecoveryFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_attached)
        {
            _attached = false;
            Dispatcher.UIThread.UnhandledException -= OnUnhandledException;
        }
    }

    [LoggerMessage(EventId = 1120, EventName = "unexpected_error_recovered", Level = LogLevel.Error,
        Message = "An unexpected error ({ExceptionType}) reached the user interface. The vault was locked.")]
    private static partial void LogRecovered(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 1121, EventName = "unexpected_error_giving_up", Level = LogLevel.Critical,
        Message = "Unexpected errors keep occurring ({ExceptionType}). The vault was locked; the application ends.")]
    private static partial void LogGivingUp(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 1122, EventName = "unexpected_error_recovery_failed", Level = LogLevel.Error,
        Message = "A step of recovering from an unexpected error failed.")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);
}
