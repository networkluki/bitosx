namespace BitosX.Core.Settings;

/// <summary>
/// Non-secret application settings persisted outside the encrypted vault. Contains only
/// preferences (never keys, passwords, or vault contents). Immutable; edits produce a new
/// instance via <c>with</c>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>On-disk schema version for the settings file.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Lower bound for the inactivity timeout (guards against a near-zero, unusable value).</summary>
    public static readonly TimeSpan MinInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the inactivity timeout.</summary>
    public static readonly TimeSpan MaxInactivityTimeout = TimeSpan.FromHours(24);

    /// <summary>Whether the vault locks automatically after a period of no in-app activity.</summary>
    public bool AutoLockOnInactivity { get; init; } = true;

    /// <summary>
    /// How long the app may be idle (no input to the BitosX window) before locking.
    /// Production default is 15 minutes; the setting is user-configurable (1 minute is a valid
    /// choice, useful for testing).
    /// </summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether the vault locks when the window is minimized. Opt-in; disabled by default.</summary>
    public bool LockOnMinimize { get; init; }

    /// <summary>The built-in default settings.</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>Returns a copy with values clamped into their accepted ranges.</summary>
    public AppSettings Normalized()
    {
        TimeSpan timeout = InactivityTimeout;
        if (timeout < MinInactivityTimeout)
        {
            timeout = MinInactivityTimeout;
        }
        else if (timeout > MaxInactivityTimeout)
        {
            timeout = MaxInactivityTimeout;
        }

        return timeout == InactivityTimeout ? this : this with { InactivityTimeout = timeout };
    }
}
