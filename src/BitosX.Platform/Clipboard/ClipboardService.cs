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

    public async Task CopySecretAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _accessor.SetTextAsync(value, cancellationToken).ConfigureAwait(false);

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
            await ClearIfUnchangedAsync(token).ConfigureAwait(false);
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
}
