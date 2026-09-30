using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BitosX.ClipboardCheck;

/// <summary>What the system's own tools find on the clipboard.</summary>
/// <param name="Types">The names of the formats on the clipboard.</param>
/// <param name="Text">The text on the clipboard, or <see langword="null"/> if there is none.</param>
/// <param name="Marker">The content of the marker format, or <see langword="null"/> if it is not there.</param>
internal sealed record ClipboardContent(IReadOnlyList<string> Types, string? Text, byte[]? Marker);

/// <summary>
/// Reads the clipboard with the tools of the system, not with the library the application uses:
/// <c>xclip</c> on Linux, the pasteboard of AppKit (through <c>osascript</c>) on macOS. They run
/// as processes of their own while the window of this tool keeps answering — on X11 it is the
/// application that owns the clipboard which hands out its content.
/// </summary>
internal static class SystemClipboard
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // Lists the types of the general pasteboard, its text, and the content of one type in base64.
    private const string PasteboardScript = """
        ObjC.import('AppKit');
        function run(argv) {
            var pasteboard = $.NSPasteboard.generalPasteboard;
            var types = ObjC.deepUnwrap(pasteboard.types) || [];
            var text = pasteboard.stringForType('public.utf8-plain-text');
            var marker = pasteboard.dataForType(argv[0]);
            var encoded = marker.isNil() ? null : ObjC.unwrap(marker.base64EncodedStringWithOptions(0));
            return JSON.stringify({ types: types, text: text.isNil() ? null : ObjC.unwrap(text), marker: encoded });
        }
        """;

    /// <summary>Whether the tools this class needs are there.</summary>
    public static async Task<string?> MissingToolAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            return await ExistsAsync("xclip", "-version").ConfigureAwait(true) ? null : "xclip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return await ExistsAsync("osascript", "-e", "1").ConfigureAwait(true) ? null : "osascript";
        }

        return "a way to read the clipboard of this system";
    }

    /// <summary>Reads the clipboard. <paramref name="markerType"/> names the format of the marker.</summary>
    public static async Task<ClipboardContent> ReadAsync(string markerType)
    {
        if (OperatingSystem.IsMacOS())
        {
            return await ReadPasteboardAsync(markerType).ConfigureAwait(true);
        }

        return await ReadSelectionAsync(markerType).ConfigureAwait(true);
    }

    /// <summary>Places plain text on the clipboard the way any other application does, without a marker.</summary>
    public static async Task SetPlainTextAsync(string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("pbcopy", [], text).ConfigureAwait(true);
        }
        else
        {
            // xclip stays in the background to hand out the content, as the owner of a selection does.
            await RunAsync("xclip", ["-selection", "clipboard", "-in"], text, waitForOutput: false).ConfigureAwait(true);
        }
    }

    private static async Task<ClipboardContent> ReadSelectionAsync(string markerType)
    {
        (int code, byte[] targets) = await RunAsync("xclip", ["-selection", "clipboard", "-out", "-target", "TARGETS"]).ConfigureAwait(true);
        string[] types = code == 0
            ? Encoding.UTF8.GetString(targets).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        string? text = null;
        if (types.Contains("UTF8_STRING", StringComparer.Ordinal))
        {
            (int textCode, byte[] bytes) = await RunAsync("xclip", ["-selection", "clipboard", "-out", "-target", "UTF8_STRING"]).ConfigureAwait(true);
            text = textCode == 0 ? Encoding.UTF8.GetString(bytes) : null;
        }

        byte[]? marker = null;
        if (types.Contains(markerType, StringComparer.Ordinal))
        {
            (int markerCode, byte[] bytes) = await RunAsync("xclip", ["-selection", "clipboard", "-out", "-target", markerType]).ConfigureAwait(true);
            marker = markerCode == 0 ? bytes : null;
        }

        return new ClipboardContent(types, text, marker);
    }

    private static async Task<ClipboardContent> ReadPasteboardAsync(string markerType)
    {
        (int code, byte[] output) = await RunAsync("osascript", ["-l", "JavaScript", "-e", PasteboardScript, markerType]).ConfigureAwait(true);
        if (code != 0)
        {
            throw new InvalidOperationException("The pasteboard could not be read (osascript exited with " + code + ").");
        }

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        string[] types = [.. root.GetProperty("types").EnumerateArray().Select(t => t.GetString() ?? string.Empty)];
        string? text = root.GetProperty("text").GetString();
        string? encoded = root.GetProperty("marker").GetString();
        return new ClipboardContent(types, text, encoded is null ? null : Convert.FromBase64String(encoded));
    }

    private static async Task<bool> ExistsAsync(string tool, params string[] arguments)
    {
        try
        {
            (int code, _) = await RunAsync(tool, arguments).ConfigureAwait(true);
            return code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, byte[] Output)> RunAsync(
        string tool, IReadOnlyList<string> arguments, string? input = null, bool waitForOutput = true)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = waitForOutput,
            RedirectStandardError = waitForOutput,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start " + tool + ".");

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(true);
            process.StandardInput.Close();
        }

        using var content = new MemoryStream();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            if (waitForOutput)
            {
                Task errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardOutput.BaseStream.CopyToAsync(content, timeout.Token).ConfigureAwait(true);
                await errors.ConfigureAwait(true);
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(tool + " did not finish in time.");
        }

        return (process.ExitCode, content.ToArray());
    }
}
