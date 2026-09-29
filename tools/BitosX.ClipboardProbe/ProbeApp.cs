using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Themes.Fluent;
using BitosX.App.Services;
using BitosX.Platform.Clipboard;
using Microsoft.Extensions.Logging.Abstractions;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;

namespace BitosX.ClipboardProbe;

/// <summary>
/// The child process of the probe: a minimal window that copies a marker through the same classes
/// the real application uses (<see cref="AvaloniaClipboardAccessor"/>, <see cref="ClipboardService"/>,
/// <see cref="ExitClipboardCleaner"/>) and records what it observes.
/// </summary>
internal sealed class ProbeApp : Application
{
    public const string MarkerPrefix = "BITOSX-TEST-";
    public const string UserContent = MarkerPrefix + "USER-CONTENT";

    public static string Scenario { get; set; } = string.Empty;
    public static string Marker { get; set; } = string.Empty;
    public static string ResultPath { get; set; } = string.Empty;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window
        {
            Width = 420,
            Height = 120,
            Title = "BitosX clipboard probe",
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                Text = "Checking clipboard behaviour. This window closes by itself.",
            },
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(400).ConfigureAwait(true);
                window.Activate();
                await RunAsync(window).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Record("error", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                window.Close();
            }
        };

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(Window window)
    {
        var accessor = new AvaloniaClipboardAccessor(() => window.Clipboard);

        switch (Scenario)
        {
            case "copy":
            {
                using var service = new ClipboardService(accessor, NullLogger<ClipboardService>.Instance);
                await service.CopySecretAsync(Marker).ConfigureAwait(true);

                Record("formats", string.Join('|', NativeClipboard.FormatNames()));
                Record("pasteable", NativeClipboard.Text() == Marker);
                Record("readback", await accessor.GetTextAsync().ConfigureAwait(true) == Marker);
                await Task.Delay(1500).ConfigureAwait(true); // let the history service notice the change
                await RecordHistoryAsync().ConfigureAwait(true);

                await service.ClearIfUnchangedAsync().ConfigureAwait(true);
                Record("cleared", NativeClipboard.FormatNames().Count == 0);
                break;
            }

            case "plain":
            {
                // Control: what the application did before 0.5.1.
                IClipboard clipboard = window.Clipboard
                    ?? throw new InvalidOperationException("The window has no clipboard.");
                await clipboard.SetTextAsync(Marker).ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                await RecordHistoryAsync().ConfigureAwait(true);
                await clipboard.ClearAsync().ConfigureAwait(true);
                break;
            }

            case "timed":
            {
                using var service = new ClipboardService(
                    accessor, NullLogger<ClipboardService>.Instance, TimeSpan.FromSeconds(2));
                await service.CopySecretAsync(Marker).ConfigureAwait(true);
                Record("present", NativeClipboard.Text() == Marker);
                await Task.Delay(4500).ConfigureAwait(true);
                Record("cleared", NativeClipboard.Text() != Marker && !service.HasPendingSecret);
                break;
            }

            case "exit-guarded":
            case "exit-unguarded":
            case "exit-replaced":
            {
                // Not disposed: the process exits right after, exactly as the application does.
                var service = new ClipboardService(accessor, NullLogger<ClipboardService>.Instance);
                if (Scenario != "exit-unguarded")
                {
                    new ExitClipboardCleaner(service, NullLogger<ExitClipboardCleaner>.Instance).Attach(window);
                }

                await service.CopySecretAsync(Marker).ConfigureAwait(true);

                // The user pastes it once. Windows hands out clipboard content on request, and
                // content that has been requested is what stays behind when its owner exits.
                Record("pasted", NativeClipboard.Text() == Marker);
                if (Scenario == "exit-replaced")
                {
                    NativeClipboard.SetText(UserContent); // the user copies something else
                }

                break;
            }

            case "purge":
                await PurgeHistoryAsync().ConfigureAwait(true);
                break;
        }
    }

    private static async Task RecordHistoryAsync()
    {
        if (!WinClipboard.IsHistoryEnabled())
        {
            Record("history", "disabled");
            return;
        }

        var result = await WinClipboard.GetHistoryItemsAsync();
        bool found = false;
        foreach (var item in result.Items)
        {
            if (item.Content.Contains(WinFormats.Text) && await item.Content.GetTextAsync() == Marker)
            {
                found = true;
            }
        }

        Record("history", found ? "recorded" : "absent");
    }

    private static async Task PurgeHistoryAsync()
    {
        if (!WinClipboard.IsHistoryEnabled())
        {
            return;
        }

        var result = await WinClipboard.GetHistoryItemsAsync();
        foreach (var item in result.Items)
        {
            if (item.Content.Contains(WinFormats.Text)
                && (await item.Content.GetTextAsync()).StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                WinClipboard.DeleteItemFromHistory(item);
            }
        }
    }

    private static void Record(string key, object value)
        => File.AppendAllText(ResultPath, $"{key}={value}{Environment.NewLine}");
}
