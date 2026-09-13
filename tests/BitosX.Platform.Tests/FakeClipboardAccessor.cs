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

    public int SetCount { get; private set; }
    public int GetCount { get; private set; }
    public int ClearCount { get; private set; }

    /// <summary>When set, the next <see cref="SetTextAsync"/> throws this exception.</summary>
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

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
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
            return Task.FromResult(_content);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _content = null;
            ClearCount++;
        }

        return Task.CompletedTask;
    }
}
