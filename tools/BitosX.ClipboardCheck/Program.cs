using Avalonia;

namespace BitosX.ClipboardCheck;

/// <summary>
/// Diagnostic-only check of how a copied secret behaves on the real clipboard of Linux or macOS,
/// using the application's clipboard code. Unit tests cannot cover this: they run against a
/// clipboard in memory.
/// </summary>
/// <remarks>
/// <para>
/// Usage: <c>dotnet run -c Release --project tools/BitosX.ClipboardCheck</c>. On Linux it needs
/// <c>xclip</c> and a display (<c>xvfb-run</c> provides one where there is none).
/// </para>
/// <para>
/// The check uses the clipboard while it runs (about fifteen seconds). It only ever places its
/// own marker texts ("BITOSX-TEST-…") there, prints nothing else it finds, and puts the text that
/// was on the clipboard back when it is done. Exit code 0 means every check passed.
/// </para>
/// <para>
/// What this shows is what is on the clipboard. Whether a clipboard manager honors the marker is
/// a question about that clipboard manager.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--hold"
            && int.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out int seconds)
            && seconds is > 0 and <= 60)
        {
            // Copies one text, keeps it for a while, and says which: for checking what a
            // clipboard manager makes of it (see tools/ci/clipboard-managers.sh).
            CheckApp.Hold = TimeSpan.FromSeconds(seconds);
            CheckApp.Unmarked = args.Contains("--unmarked", StringComparer.Ordinal);
            int held = AppBuilder.Configure<CheckApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            if (held != 0 || CheckApp.Fault is not null || CheckApp.HeldText is null)
            {
                Console.Error.WriteLine("Nothing was copied: " + (CheckApp.Fault ?? "the window ended with code " + held));
                return 1;
            }

            Console.WriteLine(CheckApp.HeldText);
            return 0;
        }

        Console.WriteLine("BitosX clipboard check (diagnostic only)");
        Console.WriteLine("----------------------------------------");

        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("On Windows, use tools/BitosX.ClipboardProbe, which also checks the clipboard history.");
            return 2;
        }

        string? missing = SystemClipboard.MissingToolAsync().GetAwaiter().GetResult();
        if (missing is not null)
        {
            Console.Error.WriteLine("This check needs " + missing + ", which was not found.");
            return 2;
        }

        Console.WriteLine("System: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        int exit = AppBuilder.Configure<CheckApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
        if (exit != 0)
        {
            Console.Error.WriteLine("The window ended with code " + exit + ".");
            return 1;
        }

        foreach ((string check, bool passed, string detail) in CheckApp.Results)
        {
            Console.WriteLine((passed ? "PASS  " : "FAIL  ") + check + (detail.Length > 0 ? "  [" + detail + "]" : string.Empty));
        }

        if (CheckApp.Fault is not null)
        {
            Console.WriteLine("FAIL  The check could not be completed: " + CheckApp.Fault);
            return 1;
        }

        int failed = CheckApp.Results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine(failed == 0 && CheckApp.Results.Count > 0
            ? $"RESULT: all {CheckApp.Results.Count} checks passed."
            : $"RESULT: {failed} of {CheckApp.Results.Count} check(s) FAILED.");
        return failed == 0 && CheckApp.Results.Count > 0 ? 0 : 1;
    }
}
