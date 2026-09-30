using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using BitosX.Platform.Instance;

namespace BitosX.App;

sealed class Program
{
    /// <summary>Exit code when another instance is already running on the same vault.</summary>
    public const int AlreadyRunningExitCode = 2;

    /// <summary>Exit code when the command line cannot be used (the <c>EX_USAGE</c> of sysexits.h).</summary>
    public const int UsageErrorExitCode = 64;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        AppLocation location;
        try
        {
            location = AppLocation.FromArguments(args);
        }
        catch (AppLocationException ex)
        {
            // Never the default vault instead of the one that was asked for.
            Console.Error.WriteLine(ex.Message);
            return UsageErrorExitCode;
        }

        // One instance per vault: a second one would hold its own copy of the contents and could
        // only ever be refused when it tried to save over the first one's changes.
        using SingleInstanceGuard? instance = SingleInstanceGuard.TryAcquire(location.VaultFilePath);
        if (instance is null)
        {
            if (OperatingSystem.IsWindows())
            {
                ShowRunningInstance(location.WindowTitle);
            }

            return AlreadyRunningExitCode;
        }

        return BuildAvaloniaApp(location).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(AppLocation.Default);

    private static AppBuilder BuildAvaloniaApp(AppLocation location)
        => AppBuilder.Configure(() => new App(location))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Brings the window of the instance that is already running to the front. Instances on other
    /// vaults are told apart by their window title.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ShowRunningInstance(string windowTitle)
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            foreach (Process other in Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id
                        || other.SessionId != current.SessionId
                        || !string.Equals(other.MainWindowTitle, windowTitle, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    nint window = other.MainWindowHandle;
                    if (window != 0)
                    {
                        if (NativeMethods.IsIconic(window))
                        {
                            NativeMethods.ShowWindow(window, NativeMethods.Restore);
                        }

                        NativeMethods.SetForegroundWindow(window);
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Only a courtesy; the second instance exits either way.
        }
    }

    private static class NativeMethods
    {
        public const int Restore = 9; // SW_RESTORE

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(nint window);
    }
}
