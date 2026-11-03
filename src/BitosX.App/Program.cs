using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using BitosX.ApiClient;
using BitosX.App.Services;
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
        ApiServer? server;
        try
        {
            location = AppLocation.FromArguments(args);
            server = ApiServerSelection.FromArguments(args);
        }
        catch (AppLocationException ex)
        {
            // Never the default vault instead of the one that was asked for.
            return UsageError(ex.Message, args);
        }
        catch (ApiServerSelectionException ex)
        {
            // Never the production server instead of the one that was asked for.
            return UsageError(ex.Message, args);
        }

        // One instance per vault: a second one would hold its own copy of the contents and could
        // only ever be refused when it tried to save over the first one's changes.
        using SingleInstanceGuard? instance = SingleInstanceGuard.TryAcquire(location.VaultFilePath);
        if (instance is null)
        {
            // On Windows the running window comes to the front. Elsewhere it cannot be reached from
            // here, so the user is told rather than seeing nothing happen.
            if (!(OperatingSystem.IsWindows() && ShowRunningInstance(location.WindowTitle)))
            {
                const string AlreadyRunning = "BitosX is already running on this vault. Switch to its window: it may be minimized or on another workspace.";
                Console.Error.WriteLine(AlreadyRunning);
                if (!IsErrorStreamCaptured())
                {
                    StartupNotice.Show(location.WindowTitle, AlreadyRunning, args);
                }
            }

            return AlreadyRunningExitCode;
        }

        return BuildAvaloniaApp(location, server).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>A command line that cannot be used: said on the standard error stream and in a window, since a shortcut has no console.</summary>
    private static int UsageError(string message, string[] args)
    {
        Console.Error.WriteLine(message);
        if (!IsErrorStreamCaptured())
        {
            StartupNotice.Show("BitosX did not start", message + Environment.NewLine + Environment.NewLine + "Nothing was opened or changed.", args);
        }

        return UsageErrorExitCode;
    }

    /// <summary>
    /// Whether someone reads the standard error stream: a script or a test that captured it, or a
    /// terminal. Then the message is already where it is read, and a window would only keep the
    /// process from ending. On Windows a GUI application started from a shortcut has no error
    /// handle at all, which <see cref="Console.IsErrorRedirected"/> also reports as "redirected", so
    /// the handle's own type decides there: a pipe, a file or a console means read. Elsewhere a terminal
    /// shows the message; a desktop launcher does not, so the window is shown when stderr is not one.
    /// </summary>
    private static bool IsErrorStreamCaptured()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                nint handle = NativeMethods.GetStdHandle(NativeMethods.StdErrorHandle);
                if (handle == 0 || handle == -1)
                {
                    return false;
                }

                uint type = NativeMethods.GetFileType(handle) & ~NativeMethods.FileTypeRemote;
                return type is NativeMethods.FileTypeDisk or NativeMethods.FileTypePipe or NativeMethods.FileTypeChar;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        return !Console.IsErrorRedirected;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(AppLocation.Default, ApiServer.Production);

    private static AppBuilder BuildAvaloniaApp(AppLocation location, ApiServer? server)
        => AppBuilder.Configure(() => new App(location, server))
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
    /// <returns>Whether a window was found and brought to the front.</returns>
    [SupportedOSPlatform("windows")]
    private static bool ShowRunningInstance(string windowTitle)
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
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Only a courtesy; the second instance exits either way.
        }

        return false;
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

        public const int StdErrorHandle = -12; // STD_ERROR_HANDLE
        public const uint FileTypeDisk = 0x0001; // FILE_TYPE_DISK
        public const uint FileTypeChar = 0x0002; // FILE_TYPE_CHAR (a console)
        public const uint FileTypePipe = 0x0003; // FILE_TYPE_PIPE
        public const uint FileTypeRemote = 0x8000; // FILE_TYPE_REMOTE (flag)

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetFileType(nint file);
    }
}
