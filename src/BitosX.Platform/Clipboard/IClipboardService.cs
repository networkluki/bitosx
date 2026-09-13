namespace BitosX.Platform.Clipboard;

/// <summary>
/// Copies sensitive values to the clipboard and schedules them to be cleared after a delay.
/// </summary>
/// <remarks>
/// LIMITATION: Clearing the clipboard here does NOT defeat operating-system clipboard history,
/// clipboard-sync features, or other applications that observe clipboard changes. A copied
/// secret may persist elsewhere regardless of this timed clear. See docs/SECURITY_MODEL.md.
/// </remarks>
public interface IClipboardService
{
    /// <summary>How long after a copy the clipboard is automatically cleared.</summary>
    TimeSpan ClearDelay { get; }

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
