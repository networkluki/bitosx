using BitosX.Core.Diagnostics;
using BitosX.Core.Settings;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>Why the vault was automatically locked (used for logging; never contains secrets).</summary>
public enum AutoLockReason
{
    /// <summary>No in-app activity for the configured timeout.</summary>
    Inactivity,

    /// <summary>The OS reported the workstation/session was locked.</summary>
    SessionLocked,

    /// <summary>The OS reported the system is suspending/sleeping.</summary>
    SystemSuspending,

    /// <summary>The window was minimized and lock-on-minimize is enabled.</summary>
    WindowMinimized,
}

/// <summary>
/// Platform-agnostic auto-lock policy. It decides <em>when</em> to lock; the host (UI layer) is
/// responsible for feeding it activity, a periodic inactivity check, and OS session/power signals.
/// This keeps all timing and OS/UI concerns out of the core.
/// </summary>
/// <remarks>
/// Only armed while the vault is <see cref="VaultState.Unlocked"/> (tracked via
/// <see cref="IVaultService.StateChanged"/>). Locking simply calls the existing
/// <see cref="IVaultService.Lock"/> — the vault format, key handling, and state machine are
/// unchanged. Not thread-safe: all members are expected to be called on the UI thread, the same
/// thread <see cref="VaultService"/> is used from.
/// </remarks>
public sealed class AutoLockController : IDisposable
{
    private readonly IVaultService _vault;
    private readonly TimeProvider _time;
    private readonly ILogger<AutoLockController> _logger;
    private AppSettings _settings;
    private bool _armed;
    private DateTimeOffset _lastActivityUtc;
    private bool _disposed;

    public AutoLockController(
        IVaultService vault,
        AppSettings settings,
        TimeProvider timeProvider,
        ILogger<AutoLockController> logger)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = (settings ?? AppSettings.Default).Normalized();

        _armed = _vault.State == VaultState.Unlocked;
        _lastActivityUtc = _time.GetUtcNow();
        _vault.StateChanged += OnVaultStateChanged;
    }

    /// <summary>The settings currently in effect.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Whether an unlocked vault is currently being watched for auto-lock.</summary>
    public bool IsArmed => _armed;

    /// <summary>Applies updated settings (e.g. after the user changes them).</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = (settings ?? AppSettings.Default).Normalized();
    }

    /// <summary>Records user activity, resetting the inactivity clock. Ignored while locked.</summary>
    public void NotifyActivity()
    {
        if (_armed)
        {
            _lastActivityUtc = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Checks whether the inactivity timeout has elapsed and locks if so. Intended to be called
    /// periodically by the host (e.g. a UI timer). No-op when disarmed or inactivity lock is off.
    /// </summary>
    public void CheckInactivity()
    {
        if (!_armed || !_settings.AutoLockOnInactivity)
        {
            return;
        }

        if (_time.GetUtcNow() - _lastActivityUtc >= _settings.InactivityTimeout)
        {
            LockNow(AutoLockReason.Inactivity);
        }
    }

    /// <summary>The OS reported the workstation/session was locked.</summary>
    public void OnSessionLocked()
    {
        if (_armed)
        {
            LockNow(AutoLockReason.SessionLocked);
        }
    }

    /// <summary>The OS reported the system is suspending/sleeping.</summary>
    public void OnSystemSuspending()
    {
        if (_armed)
        {
            LockNow(AutoLockReason.SystemSuspending);
        }
    }

    /// <summary>The window was minimized. Locks only when lock-on-minimize is enabled.</summary>
    public void OnWindowMinimized()
    {
        if (_armed && _settings.LockOnMinimize)
        {
            LockNow(AutoLockReason.WindowMinimized);
        }
    }

    private void OnVaultStateChanged(object? sender, VaultState state)
    {
        _armed = state == VaultState.Unlocked;
        if (_armed)
        {
            _lastActivityUtc = _time.GetUtcNow();
        }
    }

    private void LockNow(AutoLockReason reason)
    {
        if (!_armed)
        {
            return;
        }

        // Disarm first so the Lock() -> StateChanged callback and any concurrent trigger are no-ops.
        _armed = false;
        VaultLog.VaultAutoLocked(_logger, reason);
        _vault.Lock();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vault.StateChanged -= OnVaultStateChanged;
    }
}
