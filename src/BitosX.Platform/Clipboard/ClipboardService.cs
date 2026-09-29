using Microsoft.Extensions.Logging;

namespace BitosX.Platform.Clipboard;

/// <summary>
/// Clipboard policy: copies a secret and schedules it to be cleared after <see cref="ClearDelay"/>.
/// When the timer fires it clears the clipboard only if it still holds the value BitosX copied,
/// so it does not erase content the user copied afterwards.
/// </summary>
public sealed class ClipboardService : IClipboardService, IDisposable
{
    private static readonly TimeSpan DefaultClearDelay = TimeSpan.FromSeconds(30);

    // The clipboard is a shared resource that another application may be holding at the moment the
    // timer fires. One failed attempt must not leave the secret there for good.
    private const int ClearAttempts = 4;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IClipboardAccessor _accessor;
    private readonly ILogger<ClipboardService> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pendingClear;
    private string? _lastCopiedValue;
    private bool _disposed;

    public ClipboardService(IClipboardAccessor accessor, ILogger<ClipboardService> logger)
        : this(accessor, logger, DefaultClearDelay)
    {
    }

    public ClipboardService(IClipboardAccessor accessor, ILogger<ClipboardService> logger, TimeSpan clearDelay)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(clearDelay, TimeSpan.Zero);
        ClearDelay = clearDelay;
    }

    public TimeSpan ClearDelay { get; }

    public bool HasPendingSecret
    {
        get
        {
            lock (_gate)
            {
                return _lastCopiedValue is not null;
            }
        }
    }

    public async Task CopySecretAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _accessor.SetSecretTextAsync(value, cancellationToken).ConfigureAwait(false);

        CancellationTokenSource cts;
        lock (_gate)
        {
            _pendingClear?.Cancel();
            _pendingClear?.Dispose();
            cts = new CancellationTokenSource();
            _pendingClear = cts;
            _lastCopiedValue = value;
        }

        // Fire-and-forget the delayed clear; failures are swallowed and logged, never thrown to the caller.
        _ = ClearAfterDelayAsync(cts.Token);
    }

    public async Task ClearIfUnchangedAsync(CancellationToken cancellationToken = default)
    {
        string? expected;
        lock (_gate)
        {
            expected = _lastCopiedValue;
        }

        if (expected is null)
        {
            return;
        }

        string? current = await _accessor.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(current, expected, StringComparison.Ordinal))
        {
            // Clear via the platform clear operation. Setting an empty string does not reliably
            // remove clipboard content on Windows, which left copied secrets on the clipboard.
            await _accessor.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (string.Equals(_lastCopiedValue, expected, StringComparison.Ordinal))
            {
                _lastCopiedValue = null;
            }
        }
    }

    private async Task ClearAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ClearDelay, token).ConfigureAwait(false);

            TimeSpan retryDelay = ClearDelay < MaxRetryDelay ? ClearDelay : MaxRetryDelay;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await ClearIfUnchangedAsync(token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < ClearAttempts)
                {
                    ClipboardLog.ClearRetrying(_logger, attempt, ex);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer copy superseded this scheduled clear; nothing to do.
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
        lock (_gate)
        {
            _pendingClear?.Cancel();
            _pendingClear?.Dispose();
            _pendingClear = null;
            _lastCopiedValue = null;
        }
    }
}

internal static partial class ClipboardLog
{
    [LoggerMessage(EventId = 1100, EventName = "clipboard_clear_failed", Level = LogLevel.Warning,
        Message = "Scheduled clipboard clear failed.")]
    public static partial void ClearFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1101, EventName = "clipboard_clear_retrying", Level = LogLevel.Information,
        Message = "Clipboard clear attempt {Attempt} failed; retrying.")]
    public static partial void ClearRetrying(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(EventId = 1102, EventName = "clipboard_cleared_on_session_event", Level = LogLevel.Information,
        Message = "Clipboard cleared because the session was locked or the system is suspending.")]
    public static partial void ClearedOnSessionEvent(ILogger logger);
}
