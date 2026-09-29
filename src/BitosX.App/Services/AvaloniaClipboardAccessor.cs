using Avalonia.Input.Platform;
using Avalonia.Threading;
using BitosX.Platform.Clipboard;

namespace BitosX.App.Services;

/// <summary>
/// Avalonia-backed implementation of <see cref="IClipboardAccessor"/>. Resolves the platform
/// clipboard lazily so it works regardless of when the main window becomes available.
/// </summary>
/// <remarks>
/// Avalonia clipboard access must happen on the UI thread. The clipboard *policy*
/// (<c>ClipboardService</c>) schedules its timed clear on a thread-pool thread, so every
/// operation here is marshalled onto the UI thread. Without this, the delayed clear ran on a
/// background thread, the clipboard read returned nothing, the "unchanged" guard failed, and the
/// clear was silently skipped — leaving copied secrets on the clipboard.
/// </remarks>
public sealed class AvaloniaClipboardAccessor : IClipboardAccessor
{
    private readonly Func<IClipboard?> _clipboardProvider;

    public AvaloniaClipboardAccessor(Func<IClipboard?> clipboardProvider)
    {
        _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
    }

    public Task SetSecretTextAsync(string text, CancellationToken cancellationToken = default)
        => RunOnUiThreadAsync(async () =>
        {
            IClipboard? clipboard = _clipboardProvider();
            if (clipboard is not null)
            {
                // The clipboard takes ownership of the data object and disposes it when it is replaced.
                await clipboard.SetDataAsync(SensitiveClipboardData.Create(text)).ConfigureAwait(true);
            }

            return true;
        });

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
        => RunOnUiThreadAsync(async () =>
        {
            IClipboard? clipboard = _clipboardProvider();
            if (clipboard is null)
            {
                return (string?)null;
            }

            // Avalonia 12 exposes clipboard text via the TryGetTextAsync extension.
            return await clipboard.TryGetTextAsync().ConfigureAwait(true);
        });

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => RunOnUiThreadAsync(async () =>
        {
            IClipboard? clipboard = _clipboardProvider();
            if (clipboard is not null)
            {
                // Use the platform's dedicated clear; setting an empty string does not reliably
                // remove clipboard content on Windows.
                await clipboard.ClearAsync().ConfigureAwait(true);
            }

            return true;
        });

    /// <summary>Runs <paramref name="action"/> on the Avalonia UI thread and returns its result.</summary>
    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => _ = ForwardAsync(action, tcs));
        return tcs.Task;

        static async Task ForwardAsync(Func<Task<T>> a, TaskCompletionSource<T> tcs)
        {
            try
            {
                tcs.SetResult(await a().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }
}
