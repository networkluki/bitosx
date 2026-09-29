using BitosX.Platform.Clipboard;
using BitosX.Platform.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Platform.Tests;

/// <summary>
/// Tests for the clipboard protections added in 0.5.1: the timed clear survives a clipboard that is
/// momentarily busy, and a copied secret is removed when the user locks the workstation or the
/// system goes to sleep.
/// </summary>
public sealed class ClipboardHardeningTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private static ClipboardService Create(FakeClipboardAccessor accessor, TimeSpan delay)
        => new(accessor, NullLogger<ClipboardService>.Instance, delay);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    // ---- Pending state ----

    [Fact]
    public async Task HasPendingSecret_TracksTheCopiedValue()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        Assert.False(service.HasPendingSecret);

        await service.CopySecretAsync("secret");
        Assert.True(service.HasPendingSecret);

        await service.ClearIfUnchangedAsync();
        Assert.False(service.HasPendingSecret);
        Assert.Null(accessor.Content);
    }

    [Fact]
    public async Task HasPendingSecret_IsFalse_OnceTheUserHasReplacedTheContent()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        await service.CopySecretAsync("secret");
        accessor.SetExternally("something the user copied");

        await service.ClearIfUnchangedAsync();

        Assert.False(service.HasPendingSecret);
        Assert.Equal("something the user copied", accessor.Content);
    }

    [Fact]
    public async Task HasPendingSecret_IsFalse_AfterTheTimedClear()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);

        await service.CopySecretAsync("secret");

        Assert.True(await WaitUntilAsync(() => !service.HasPendingSecret));
        Assert.Null(accessor.Content);
    }

    // ---- A busy clipboard ----

    [Fact]
    public async Task AutoClear_WhenTheClipboardIsBusy_RetriesUntilItSucceeds()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);
        await service.CopySecretAsync("secret");
        accessor.FailNextGets(2);

        // Before the fix a single failed attempt left the secret on the clipboard for good.
        Assert.True(await WaitUntilAsync(() => accessor.Content is null));
        Assert.False(service.HasPendingSecret);
    }

    [Fact]
    public async Task AutoClear_WhenClearingFails_RetriesUntilItSucceeds()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);
        await service.CopySecretAsync("secret");
        accessor.FailNextClears(3);

        Assert.True(await WaitUntilAsync(() => accessor.Content is null));
        Assert.False(service.HasPendingSecret);
    }

    [Fact]
    public async Task AutoClear_WhenTheClipboardStaysUnavailable_GivesUp_AndTheSecretIsStillPending()
    {
        var accessor = new FakeClipboardAccessor { ThrowOnGet = new IOException("clipboard unavailable") };
        using var service = Create(accessor, ShortDelay);
        await service.CopySecretAsync("secret");

        await Task.Delay(ShortDelay * 8);

        // Still known to be pending, so clearing on exit or on workstation lock will try again.
        Assert.True(service.HasPendingSecret);

        accessor.ThrowOnGet = null;
        await service.ClearIfUnchangedAsync();
        Assert.Null(accessor.Content);
    }

    [Fact]
    public async Task AutoClear_Retry_IsAbandoned_WhenANewerValueIsCopied()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, ShortDelay);
        await service.CopySecretAsync("first");
        accessor.FailNextGets(1);
        await Task.Delay(ShortDelay + TimeSpan.FromMilliseconds(30));

        await service.CopySecretAsync("second");

        // The retry belonging to "first" must not clear "second" before its own delay has passed.
        Assert.Equal("second", accessor.Content);
        Assert.True(await WaitUntilAsync(() => accessor.Content is null));
    }

    // ---- Workstation lock / suspend ----

    private sealed class FakeSessionEvents : ISystemSessionEvents
    {
        public bool IsSessionLockSupported => true;
        public bool IsSuspendSupported => true;
        public event EventHandler? SessionLocked;
        public event EventHandler? Suspending;

        public int Subscribers =>
            (SessionLocked?.GetInvocationList().Length ?? 0) + (Suspending?.GetInvocationList().Length ?? 0);

        public void Start()
        {
        }

        public void RaiseSessionLocked() => SessionLocked?.Invoke(this, EventArgs.Empty);

        public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private static ClipboardSessionGuard Guard(FakeSessionEvents events, IClipboardService clipboard)
        => new(events, clipboard, NullLogger<ClipboardSessionGuard>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserLeaving_ClearsTheCopiedSecret_WithoutWaitingForTheTimer(bool workstationLock)
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        var events = new FakeSessionEvents();
        using ClipboardSessionGuard guard = Guard(events, service);
        await service.CopySecretAsync("secret");

        if (workstationLock)
        {
            events.RaiseSessionLocked();
        }
        else
        {
            events.RaiseSuspending();
        }

        Assert.True(await WaitUntilAsync(() => accessor.Content is null));
        Assert.False(service.HasPendingSecret);
    }

    [Fact]
    public async Task UserLeaving_DoesNotEraseContentTheUserCopiedAfterwards()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        var events = new FakeSessionEvents();
        using ClipboardSessionGuard guard = Guard(events, service);
        await service.CopySecretAsync("secret");
        accessor.SetExternally("something the user copied");

        events.RaiseSessionLocked();

        Assert.True(await WaitUntilAsync(() => !service.HasPendingSecret));
        Assert.Equal("something the user copied", accessor.Content);
    }

    [Fact]
    public void UserLeaving_WithNothingCopied_DoesNotTouchTheClipboard()
    {
        var accessor = new FakeClipboardAccessor();
        accessor.SetExternally("something the user copied");
        using var service = Create(accessor, LongDelay);
        var events = new FakeSessionEvents();
        using ClipboardSessionGuard guard = Guard(events, service);

        events.RaiseSessionLocked();
        events.RaiseSuspending();

        Assert.Equal(0, accessor.GetCount);
        Assert.Equal(0, accessor.ClearCount);
        Assert.Equal("something the user copied", accessor.Content);
    }

    [Fact]
    public async Task UserLeaving_WhenTheClipboardFails_DoesNotThrowIntoTheOperatingSystemCallback()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        var events = new FakeSessionEvents();
        using ClipboardSessionGuard guard = Guard(events, service);
        await service.CopySecretAsync("secret");
        accessor.ThrowOnGet = new InvalidOperationException("no clipboard");

        Exception? failure = Record.Exception(events.RaiseSessionLocked);
        await Task.Delay(100);

        Assert.Null(failure);
        Assert.True(service.HasPendingSecret); // nothing was cleared, and that is still known
    }

    [Fact]
    public async Task Guard_AfterDispose_NoLongerReacts()
    {
        var accessor = new FakeClipboardAccessor();
        using var service = Create(accessor, LongDelay);
        var events = new FakeSessionEvents();
        ClipboardSessionGuard guard = Guard(events, service);
        Assert.Equal(2, events.Subscribers);

        guard.Dispose();
        guard.Dispose(); // idempotent
        await service.CopySecretAsync("secret");
        events.RaiseSessionLocked();
        await Task.Delay(100);

        Assert.Equal(0, events.Subscribers);
        Assert.Equal("secret", accessor.Content);
    }
}
