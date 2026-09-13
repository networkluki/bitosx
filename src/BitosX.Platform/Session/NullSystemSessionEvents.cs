namespace BitosX.Platform.Session;

/// <summary>
/// Default <see cref="ISystemSessionEvents"/> for platforms where session-lock and suspend cannot
/// be reliably detected. It reports both as unsupported and never raises an event, rather than
/// inventing behavior. Inactivity timeout and manual lock still work regardless.
/// </summary>
public sealed class NullSystemSessionEvents : ISystemSessionEvents
{
    public bool IsSessionLockSupported => false;

    public bool IsSuspendSupported => false;

#pragma warning disable CS0067 // Events are intentionally never raised on unsupported platforms.
    public event EventHandler? SessionLocked;
    public event EventHandler? Suspending;
#pragma warning restore CS0067

    public void Start()
    {
        // Nothing to observe on this platform.
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
