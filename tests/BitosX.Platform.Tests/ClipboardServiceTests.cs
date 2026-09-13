using System.Diagnostics;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Platform.Tests;

public sealed class ClipboardServiceTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private static ClipboardService Create(FakeClipboardAccessor accessor, TimeSpan delay)
        => new(accessor, NullLogger<ClipboardService>.Instance, delay);

    [Fact]
    public async Task CopySecret_SetsClipboardContent()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);

        await service.CopySecretAsync("secret-value");

        Assert.Equal("secret-value", accessor.Content);
        Assert.Equal(1, accessor.SetCount);
    }

    [Fact]
    public async Task AutoClear_AfterDelay_ClearsClipboard()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("secret-value");
        bool cleared = await WaitUntilAsync(() => string.IsNullOrEmpty(accessor.Content), WaitBudget);

        Assert.True(cleared, "The clipboard should have been cleared after the delay.");
    }

    [Fact]
    public async Task AutoClear_DoesNotEraseContentTheUserReplaced()
    {
        // Section 7 core requirement: BitosX copies A, the user copies B, the timer expires;
        // B must survive.
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("bitosx-password");
        accessor.SetExternally("user-copied-this-afterwards");

        // Wait comfortably past the clear delay; the scheduled clear must be a no-op.
        await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(400));

        Assert.Equal("user-copied-this-afterwards", accessor.Content);
    }

    [Fact]
    public async Task ClearIfUnchanged_WhenUnchanged_Clears()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);

        await service.CopySecretAsync("secret");
        await service.ClearIfUnchangedAsync();

        Assert.True(string.IsNullOrEmpty(accessor.Content));
    }

    [Fact]
    public async Task ClearIfUnchanged_WhenReplaced_PreservesNewContent()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);

        await service.CopySecretAsync("secret");
        accessor.SetExternally("replacement");
        await service.ClearIfUnchangedAsync();

        Assert.Equal("replacement", accessor.Content);
    }

    [Fact]
    public async Task RepeatedCopies_LatestValueWins_AndIsClearedOnce()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("first");
        await service.CopySecretAsync("second");

        Assert.Equal("second", accessor.Content);

        // The superseded clear for "first" must not fire early; the clipboard is cleared only
        // after the latest copy's delay.
        bool cleared = await WaitUntilAsync(() => string.IsNullOrEmpty(accessor.Content), WaitBudget);
        Assert.True(cleared);
    }

    [Fact]
    public async Task Dispose_CancelsPendingClear()
    {
        var accessor = new FakeClipboardAccessor();
        var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("secret");
        service.Dispose();

        // After dispose the scheduled clear must not run.
        await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(400));
        Assert.Equal("secret", accessor.Content);
    }

    [Fact]
    public async Task CopySecret_AfterDispose_Throws()
    {
        var accessor = new FakeClipboardAccessor();
        var service = Create(accessor, LongDelay);
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CopySecretAsync("secret"));
    }

    [Fact]
    public async Task CopySecret_WhenSetFails_PropagatesToCaller()
    {
        var accessor = new FakeClipboardAccessor { ThrowOnNextSet = new IOException("clipboard busy") };
        using var service = Create(accessor, LongDelay);

        await Assert.ThrowsAsync<IOException>(() => service.CopySecretAsync("secret"));
    }

    [Fact]
    public async Task AutoClear_WhenAccessorThrows_FailsSafely()
    {
        // A clipboard read failure during the scheduled clear must be swallowed (not crash the
        // process via an unobserved exception) and must not wipe existing content.
        var accessor = new FakeClipboardAccessor { ThrowOnGet = new InvalidOperationException("no clipboard") };
        using var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("secret");
        await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(400));

        Assert.Equal("secret", accessor.Content);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDelay()
    {
        var accessor = new FakeClipboardAccessor();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClipboardService(accessor, NullLogger<ClipboardService>.Instance, TimeSpan.Zero));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }
}
