namespace BitosX.Core.Services;

/// <summary>
/// UI-agnostic state machine that gates a destructive restore commit. A commit is permitted only
/// when a backup has been successfully validated (a preview is pending) AND the user has explicitly
/// acknowledged the destructive replacement. Any change to the selection, password, or validation —
/// and cancel or commit — resets it. This is a plain, testable helper; enforcement of the gate at
/// the actual commit site is the caller's responsibility (defense in depth).
/// </summary>
public sealed class RestoreConfirmationState
{
    /// <summary>Whether a validated backup is currently pending confirmation.</summary>
    public bool HasPreview { get; private set; }

    /// <summary>Whether the user has explicitly acknowledged the destructive replacement.</summary>
    public bool Acknowledged { get; private set; }

    /// <summary>The destructive commit is allowed only when both a preview is pending and it is acknowledged.</summary>
    public bool CanCommit => HasPreview && Acknowledged;

    /// <summary>A backup validated successfully; a fresh preview is pending with acknowledgement reset.</summary>
    public void SetPrepared()
    {
        HasPreview = true;
        Acknowledged = false;
    }

    /// <summary>Sets the acknowledgement. Acknowledgement cannot be set while no preview is pending.</summary>
    public void SetAcknowledged(bool value)
    {
        Acknowledged = HasPreview && value;
    }

    /// <summary>Clears all pending state (on file/password change, re-validate, validation failure, cancel, or commit).</summary>
    public void Reset()
    {
        HasPreview = false;
        Acknowledged = false;
    }
}
