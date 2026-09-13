using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BitosX.Platform.Session;

/// <summary>
/// Windows <see cref="ISystemSessionEvents"/> using <see cref="SystemEvents"/>: workstation lock via
/// <see cref="SystemEvents.SessionSwitch"/> and suspend via <see cref="SystemEvents.PowerModeChanged"/>.
/// Both are reliably detectable on Windows desktop sessions.
/// </summary>
/// <remarks>
/// Only construct this when <see cref="OperatingSystem.IsWindows"/> is true. <see cref="SystemEvents"/>
/// raises callbacks on its own thread, so consumers must marshal to the UI thread before touching UI
/// or the vault service.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemSessionEvents : ISystemSessionEvents
{
    private bool _started;
    private bool _disposed;

    public bool IsSessionLockSupported => true;

    public bool IsSuspendSupported => true;

    public event EventHandler? SessionLocked;

    public event EventHandler? Suspending;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Treat lock and disconnect (switch user / RDP disconnect) as "the workstation is no longer
        // in front of the user" and lock the vault.
        if (e.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)
        {
            SessionLocked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Suspending?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
