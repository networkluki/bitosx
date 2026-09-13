using Avalonia.Input.Platform;
using BitosX.Platform.Clipboard;

namespace BitosX.App.Services;

/// <summary>
/// Avalonia-backed implementation of <see cref="IClipboardAccessor"/>. Resolves the platform
/// clipboard lazily so it works regardless of when the main window becomes available.
/// </summary>
public sealed class AvaloniaClipboardAccessor : IClipboardAccessor
{
    private readonly Func<IClipboard?> _clipboardProvider;

    public AvaloniaClipboardAccessor(Func<IClipboard?> clipboardProvider)
    {
        _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        IClipboard? clipboard = _clipboardProvider();
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
        }
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        IClipboard? clipboard = _clipboardProvider();
        if (clipboard is null)
        {
            return null;
        }

        // Avalonia 12 exposes clipboard text via the TryGetTextAsync extension.
        return await clipboard.TryGetTextAsync().ConfigureAwait(false);
    }
}
