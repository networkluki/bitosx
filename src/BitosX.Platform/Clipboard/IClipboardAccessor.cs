namespace BitosX.Platform.Clipboard;

/// <summary>
/// Low-level, platform-provided clipboard access. Implemented in the UI layer (Avalonia),
/// which owns the actual clipboard. This keeps the clipboard *policy* (timed clearing,
/// not clobbering replaced content) free of any UI-framework dependency.
/// </summary>
public interface IClipboardAccessor
{
    /// <summary>Sets the clipboard text.</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Reads the current clipboard text, or <see langword="null"/> if unavailable.</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);
}
