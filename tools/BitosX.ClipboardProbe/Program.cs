using System.Diagnostics;
using System.Globalization;
using Avalonia;
using BitosX.App.Services;

namespace BitosX.ClipboardProbe;

/// <summary>
/// Diagnostic-only check of how a copied secret behaves on the real Windows clipboard, using the
/// production clipboard code. Unit tests cannot cover this: they run against an in-memory clipboard,
/// and the one clipboard defect this project has had was invisible to them.
/// </summary>
/// <remarks>
/// Usage: <c>dotnet run -c Release --project tools/BitosX.ClipboardProbe</c>.
/// The probe uses the clipboard while it runs (a few seconds). It only ever places its own marker
/// strings ("BITOSX-TEST-…") there, never reads out or prints anything else, puts the text that was
/// on the clipboard back when it is done, and removes its markers from the clipboard history.
/// Exit code 0 means every check passed.
/// </remarks>
internal static class Program
{
    private static readonly string[] SensitiveFormats =
    [
        SensitiveClipboardData.ExcludeFromMonitoringFormat,
        SensitiveClipboardData.ClipboardHistoryFormat,
        SensitiveClipboardData.CloudClipboardFormat,
        SensitiveClipboardData.ViewerIgnoreFormat,
    ];

    private static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--child")
        {
            ProbeApp.Scenario = args[1];
            ProbeApp.Marker = args[2];
            ProbeApp.ResultPath = args[3];
            return AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe checks Windows clipboard behaviour and only runs on Windows.");
            return 2;
        }

        Console.WriteLine("BitosX clipboard probe (diagnostic only)");
        Console.WriteLine("----------------------------------------");

        string? original = NativeClipboard.Text();
        try
        {
            RunChecks();
        }
        finally
        {
            Child("purge");
            if (original is not null)
            {
                NativeClipboard.SetText(original);
            }
            else
            {
                NativeClipboard.Clear();
            }

            Console.WriteLine();
            Console.WriteLine(original is not null
                ? "The text that was on the clipboard has been put back."
                : "The clipboard held no text before the probe; it has been left empty.");
        }

        Console.WriteLine(_failures == 0 ? "RESULT: all checks passed." : $"RESULT: {_failures} check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunChecks()
    {
        // 1. A copied secret: pasteable, marked as sensitive, and kept out of the clipboard history.
        Dictionary<string, string> copy = Child("copy");
        string[] formats = copy.GetValueOrDefault("formats", string.Empty).Split('|');
        Check("A copied secret can be pasted by other applications", copy.GetValueOrDefault("pasteable") == "True");
        Check("BitosX can read its own copy back (needed for the 'unchanged' guard)", copy.GetValueOrDefault("readback") == "True");
        foreach (string format in SensitiveFormats)
        {
            Check($"The clipboard carries the marker '{format}'", formats.Contains(format, StringComparer.Ordinal));
        }

        Check("Clearing removes everything BitosX placed on the clipboard", copy.GetValueOrDefault("cleared") == "True");

        // 2. Clipboard history, with a control that proves the check can fail.
        Dictionary<string, string> plain = Child("plain");
        string control = plain.GetValueOrDefault("history", "unknown");
        if (control == "disabled")
        {
            Skip("Clipboard history is turned off on this machine; the history checks cannot be made");
        }
        else
        {
            Check("Control: an unmarked copy IS recorded in the clipboard history", control == "recorded");
            Check("A copied secret is NOT recorded in the clipboard history", copy.GetValueOrDefault("history") == "absent");
        }

        // 3. The timed clear works on the real clipboard.
        Dictionary<string, string> timed = Child("timed");
        Check("The secret is on the clipboard right after copying", timed.GetValueOrDefault("present") == "True");
        Check("The timed clear removes it", timed.GetValueOrDefault("cleared") == "True");

        // 4. Closing the application, with a control that shows what the cleaner prevents.
        Dictionary<string, string> unguardedRun = Child("exit-unguarded");
        string unguarded = MarkerOf(unguardedRun);
        Check("The secret was pasted once before closing", unguardedRun.GetValueOrDefault("pasted") == "True");
        Check(
            "Control: without the exit cleaner a pasted secret survives the application closing",
            NativeClipboard.Text() == unguarded);
        NativeClipboard.Clear();

        string guarded = MarkerOf(Child("exit-guarded"));
        Check("With the exit cleaner the secret is gone once the application has closed", NativeClipboard.Text() != guarded);
        Check("and the clipboard is left empty", NativeClipboard.FormatNames().Count == 0);

        Child("exit-replaced");
        Check(
            "Closing does not erase what the user copied after the secret",
            NativeClipboard.Text() == ProbeApp.UserContent);
        NativeClipboard.Clear();
    }

    private static string MarkerOf(Dictionary<string, string> result) => result.GetValueOrDefault("marker", string.Empty);

    /// <summary>Runs one scenario in a child process and returns what it recorded.</summary>
    private static Dictionary<string, string> Child(string scenario)
    {
        string marker = ProbeApp.MarkerPrefix + scenario.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..8];
        string resultPath = Path.Combine(Path.GetTempPath(), "bitosx-clipboard-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["marker"] = marker };

        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--child");
            start.ArgumentList.Add(scenario);
            start.ArgumentList.Add(marker);
            start.ArgumentList.Add(resultPath);

            using Process process = Process.Start(start)!;
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill();
                result["error"] = "timed out";
            }

            Thread.Sleep(300); // let the clipboard settle after the process has gone

            if (File.Exists(resultPath))
            {
                foreach (string line in File.ReadAllLines(resultPath))
                {
                    int separator = line.IndexOf('=', StringComparison.Ordinal);
                    if (separator > 0)
                    {
                        result[line[..separator]] = line[(separator + 1)..];
                    }
                }
            }
        }
        finally
        {
            File.Delete(resultPath);
        }

        if (result.TryGetValue("error", out string? error))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [ERROR] scenario '{scenario}': {error}"));
            _failures++;
        }

        return result;
    }

    private static void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {description}");
        if (!passed)
        {
            _failures++;
        }
    }

    private static void Skip(string description) => Console.WriteLine($"  [SKIP] {description}");
}
