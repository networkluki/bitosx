namespace BitosX.Platform.Session;

/// <summary>
/// Source of operating-system session and power signals relevant to auto-locking: the workstation
/// being locked and the system suspending/sleeping. Implementations expose capability flags so the
/// app can honestly report what is and is not reliably detectable on the current platform, rather
/// than pretending to support an event it cannot observe.
/// </summary>
public interface ISystemSessionEvents : IDisposable
{
    /// <summary>Whether workstation/session-lock detection is reliably supported on this platform.</summary>
    bool IsSessionLockSupported { get; }

    /// <summary>Whether system suspend/sleep detection is reliably supported on this platform.</summary>
    bool IsSuspendSupported { get; }

    /// <summary>Raised when the OS reports the workstation/session was locked (if supported).</summary>
    event EventHandler? SessionLocked;

    /// <summary>Raised when the OS reports the system is suspending/sleeping (if supported).</summary>
    event EventHandler? Suspending;

    /// <summary>Begins listening for OS signals. Safe to call once; a no-op on unsupported platforms.</summary>
    void Start();
}
