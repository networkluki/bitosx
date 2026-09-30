using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Themes.Fluent;
using BitosX.App.Services;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.ClipboardCheck;

/// <summary>
/// A minimal window that copies a marker text through the classes the application uses
/// (<see cref="AvaloniaClipboardAccessor"/>, <see cref="ClipboardService"/>,
/// <see cref="ExitClipboardCleaner"/>) and has the system's own tools say what is on the clipboard.
/// </summary>
internal sealed class CheckApp : Application
{
    public const string MarkerPrefix = "BITOSX-TEST-";

    /// <summary>The checks that were made; filled while the application runs.</summary>
    public static List<(string Check, bool Passed, string Detail)> Results { get; } = [];

    /// <summary>
    /// When set, the tool only copies one text, keeps it on the clipboard for this long, and ends:
    /// for checking what something else — a clipboard manager — makes of it.
    /// </summary>
    public static TimeSpan? Hold { get; set; }

    /// <summary>With <see cref="Hold"/>: copy the text without the marker, as a control.</summary>
    public static bool Unmarked { get; set; }

    /// <summary>With <see cref="Hold"/>: the text that was copied.</summary>
    public static string? HeldText { get; private set; }

    /// <summary>What went wrong with the run itself, if anything did.</summary>
    public static string? Fault { get; private set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window
        {
            Width = 420,
            Height = 120,
            Title = "BitosX clipboard check",
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                Text = "Checking the clipboard. This window closes by itself.",
            },
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(true);
                window.Activate();
                await (Hold is { } hold ? HoldAsync(window, hold) : RunAsync(window)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Fault = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                window.Close();
            }
        };

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task HoldAsync(Window window, TimeSpan hold)
    {
        IClipboard clipboard = window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");
        string text = MarkerPrefix + (Unmarked ? "UNMARKED-" : "MARKED-") + Guid.NewGuid().ToString("N");

        if (Unmarked)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        else
        {
            var accessor = new AvaloniaClipboardAccessor(
                () => window.Clipboard,
                SensitiveClipboardData.CurrentPlatform,
                NullLogger<AvaloniaClipboardAccessor>.Instance);
            await accessor.SetSecretTextAsync(text).ConfigureAwait(true);
        }

        HeldText = text;
        await Task.Delay(hold).ConfigureAwait(true);
        await clipboard.ClearAsync().ConfigureAwait(true);
    }

    private static async Task RunAsync(Window window)
    {
        IClipboard clipboard = window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");
        ClipboardPlatform platform = SensitiveClipboardData.CurrentPlatform;
        (string markerType, byte[] markerValue) = platform switch
        {
            ClipboardPlatform.MacOS => (SensitiveClipboardData.MacConcealedType, new byte[] { 1 }),
            ClipboardPlatform.Linux => (
                SensitiveClipboardData.LinuxPasswordHintFormat,
                Encoding.ASCII.GetBytes(SensitiveClipboardData.LinuxPasswordHintValue)),
            _ => throw new PlatformNotSupportedException("This check is for Linux and macOS."),
        };

        // What the user had on the clipboard is put back at the end.
        string? original = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        var refusals = new RefusalCounter();
        var accessor = new AvaloniaClipboardAccessor(() => window.Clipboard, platform, refusals);
        try
        {
            // 1. A copied secret: the text, and the marker of this system.
            string secret = MarkerPrefix + Guid.NewGuid().ToString("N");
            using (var service = new ClipboardService(accessor, NullLogger<ClipboardService>.Instance, TimeSpan.FromSeconds(2)))
            {
                await service.CopySecretAsync(secret).ConfigureAwait(true);
                ClipboardContent copied = await SystemClipboard.ReadAsync(markerType).ConfigureAwait(true);

                Record("The copied text can be pasted", copied.Text == secret, Describe(copied.Text, secret));
                Record(
                    $"It carries the marker {markerType}",
                    copied.Marker is not null,
                    "formats on the clipboard: " + string.Join(", ", copied.Types));
                Record(
                    "The marker has the value it should have",
                    copied.Marker is not null && copied.Marker.AsSpan().SequenceEqual(markerValue),
                    copied.Marker is null ? "no marker" : "value: " + Convert.ToHexString(copied.Marker));
                Record(
                    "The marker does not repeat the secret",
                    copied.Marker is not null && !Encoding.UTF8.GetString(copied.Marker).Contains(secret, StringComparison.Ordinal),
                    string.Empty);
                Record(
                    "The clipboard accepted the marker (no fall back to an unmarked copy)",
                    refusals.Count == 0,
                    refusals.Count + " refusal(s)");
                Record(
                    "The application reads back what it copied",
                    await accessor.GetTextAsync().ConfigureAwait(true) == secret,
                    string.Empty);

                // 2. The timed clear.
                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
                ClipboardContent afterDelay = await SystemClipboard.ReadAsync(markerType).ConfigureAwait(true);
                Record(
                    "After its time the text is gone from the clipboard",
                    afterDelay.Text != secret && !service.HasPendingSecret,
                    Describe(afterDelay.Text, null));
            }

            // 3. What the user copied afterwards is left alone.
            string replaced = MarkerPrefix + Guid.NewGuid().ToString("N");
            const string UserContent = MarkerPrefix + "USER-CONTENT";
            using (var service = new ClipboardService(accessor, NullLogger<ClipboardService>.Instance, TimeSpan.FromSeconds(2)))
            {
                await service.CopySecretAsync(replaced).ConfigureAwait(true);
                await SystemClipboard.SetPlainTextAsync(UserContent).ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
                ClipboardContent kept = await SystemClipboard.ReadAsync(markerType).ConfigureAwait(true);
                Record(
                    "What was copied by someone else afterwards is not cleared",
                    kept.Text == UserContent,
                    Describe(kept.Text, UserContent));
                Record(
                    "An ordinary copy carries no marker (control)",
                    kept.Marker is null,
                    "formats on the clipboard: " + string.Join(", ", kept.Types));
            }

            // 4. Clearing at once, as on workstation lock, suspend and exit.
            string cleared = MarkerPrefix + Guid.NewGuid().ToString("N");
            using (var service = new ClipboardService(accessor, NullLogger<ClipboardService>.Instance))
            {
                await service.CopySecretAsync(cleared).ConfigureAwait(true);
                await service.ClearIfUnchangedAsync().ConfigureAwait(true);
                ClipboardContent afterClear = await SystemClipboard.ReadAsync(markerType).ConfigureAwait(true);
                Record(
                    "Cleared at once when asked to",
                    afterClear.Text != cleared && afterClear.Marker is null,
                    Describe(afterClear.Text, null));
            }
        }
        finally
        {
            if (original is not null && !original.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                await clipboard.SetTextAsync(original).ConfigureAwait(true);
            }
            else
            {
                await clipboard.ClearAsync().ConfigureAwait(true);
            }
        }
    }

    private static void Record(string check, bool passed, string detail) => Results.Add((check, passed, detail));

    // Only ever texts this tool placed itself are printed; anything else is described, not shown.
    private static string Describe(string? found, string? expected)
    {
        if (found is null)
        {
            return "no text on the clipboard";
        }

        if (found == expected)
        {
            return "as expected";
        }

        return found.StartsWith(MarkerPrefix, StringComparison.Ordinal)
            ? "found " + found
            : "found a text of " + found.Length + " characters that is not from this tool";
    }

    /// <summary>Counts how often the clipboard refused the marker (event 1111 of the accessor).</summary>
    private sealed class RefusalCounter : ILogger<AvaloniaClipboardAccessor>
    {
        public int Count { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1111)
            {
                Count++;
            }
        }
    }
}
