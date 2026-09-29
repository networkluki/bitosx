using BitosX.Platform.Clipboard;

namespace BitosX.Platform.Tests;

/// <summary>
/// In-memory <see cref="IClipboardAccessor"/> for testing <see cref="ClipboardService"/> without
/// touching a real OS clipboard. Tracks call counts and can simulate failures.
/// </summary>
internal sealed class FakeClipboardAccessor : IClipboardAccessor
{
    private readonly Lock _gate = new();
    private string? _content;
    private int _failingGets;
    private int _failingClears;

    public int SetCount { get; private set; }
    public int GetCount { get; private set; }
    public int ClearCount { get; private set; }

    /// <summary>When set, the next <see cref="SetSecretTextAsync"/> throws this exception.</summary>
    public Exception? ThrowOnNextSet { get; set; }

    /// <summary>When set, <see cref="GetTextAsync"/> throws this exception.</summary>
    public Exception? ThrowOnGet { get; set; }

    /// <summary>The current clipboard content as observed by the fake.</summary>
    public string? Content
    {
        get { lock (_gate) { return _content; } }
    }

    /// <summary>Simulates an external application (or the user) replacing the clipboard content.</summary>
    public void SetExternally(string? value)
    {
        lock (_gate)
        {
            _content = value;
        }
    }

    /// <summary>The next <paramref name="count"/> reads fail, as when another application holds the clipboard.</summary>
    public void FailNextGets(int count) => Interlocked.Exchange(ref _failingGets, count);

    /// <summary>The next <paramref name="count"/> clears fail, as when another application holds the clipboard.</summary>
    public void FailNextClears(int count) => Interlocked.Exchange(ref _failingClears, count);

    public Task SetSecretTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Exception? toThrow = ThrowOnNextSet;
        if (toThrow is not null)
        {
            ThrowOnNextSet = null;
            throw toThrow;
        }

        lock (_gate)
        {
            _content = text;
            SetCount++;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet is not null)
        {
            throw ThrowOnGet;
        }

        lock (_gate)
        {
            GetCount++;
            if (_failingGets > 0)
            {
                _failingGets--;
                throw new IOException("The clipboard is in use by another application.");
            }

            return Task.FromResult(_content);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_failingClears > 0)
            {
                _failingClears--;
                throw new IOException("The clipboard is in use by another application.");
            }

            _content = null;
            ClearCount++;
        }

        return Task.CompletedTask;
    }
}
