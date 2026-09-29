namespace BitosX.Platform.Clipboard;

/// <summary>
/// Copies sensitive values to the clipboard and schedules them to be cleared after a delay.
/// </summary>
/// <remarks>
/// LIMITATION: Clearing the clipboard here cannot remove a copy that something else has already
/// taken: a clipboard manager, or another application that observes clipboard changes. On Windows
/// the copied value is marked so that the operating system's own clipboard history and cloud
/// clipboard leave it alone; there is no equivalent on the other platforms yet.
/// See docs/SECURITY_MODEL.md.
/// </remarks>
public interface IClipboardService
{
    /// <summary>How long after a copy the clipboard is automatically cleared.</summary>
    TimeSpan ClearDelay { get; }

    /// <summary>
    /// Whether a value BitosX copied may still be on the clipboard — it has been copied and has
    /// neither been cleared nor found to be replaced yet.
    /// </summary>
    bool HasPendingSecret { get; }

    /// <summary>
    /// Copies <paramref name="value"/> to the clipboard and schedules an automatic clear.
    /// Any previously scheduled clear is cancelled and rescheduled.
    /// </summary>
    Task CopySecretAsync(string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the clipboard immediately, but only if it still contains the last value BitosX
    /// copied (so content the user has since replaced is preserved).
    /// </summary>
    Task ClearIfUnchangedAsync(CancellationToken cancellationToken = default);
}
