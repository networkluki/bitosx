namespace BitosX.Platform.Clipboard;

/// <summary>
/// Low-level, platform-provided clipboard access. Implemented in the UI layer (Avalonia),
/// which owns the actual clipboard. This keeps the clipboard *policy* (timed clearing,
/// not clobbering replaced content) free of any UI-framework dependency.
/// </summary>
public interface IClipboardAccessor
{
    /// <summary>
    /// Places a secret on the clipboard as text. Where the platform has a way to say so, the content
    /// is marked as sensitive so that the operating system keeps it out of clipboard history and
    /// clipboard sync; other applications can still paste it normally.
    /// </summary>
    Task SetSecretTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Reads the current clipboard text, or <see langword="null"/> if unavailable.</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the clipboard using the platform's dedicated clear operation. This is preferred over
    /// setting an empty string, which does not reliably remove clipboard content on all platforms
    /// (notably Windows).
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
