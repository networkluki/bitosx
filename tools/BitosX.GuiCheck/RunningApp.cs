using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace BitosX.GuiCheck;

/// <summary>
/// The real application, started as a process of its own on a data directory of its own, and
/// operated through the accessibility interface of Windows. Nothing here reaches into the
/// application: what is known about it is what any other program can see of its window.
/// </summary>
internal sealed class RunningApp : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;

    private RunningApp(Process process, nint handle)
    {
        _process = process;
        Handle = handle;
    }

    /// <summary>The window of the application.</summary>
    public nint Handle { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    private AutomationElement Window => AutomationElement.FromHandle(Handle);

    /// <summary>The title of the window.</summary>
    public string Title => Window.Current.Name;

    /// <summary>Starts the application on <paramref name="dataDirectory"/> and waits for its window.</summary>
    public static RunningApp Start(string executable, string dataDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--data-directory");
        start.ArgumentList.Add(dataDirectory);
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("The application could not be started.");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartTimeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The application ended at once, with code " + process.ExitCode + ".");
            }

            process.Refresh();
            if (process.MainWindowHandle != 0)
            {
                var app = new RunningApp(process, process.MainWindowHandle);
                if (app.Screen() is not null)
                {
                    return app;
                }
            }

            Thread.Sleep(100);
        }

        process.Kill();
        throw new TimeoutException("The application did not show a window.");
    }

    /// <summary>Starts the application and returns how it ended, without waiting for a window.</summary>
    public static int Run(string executable, IEnumerable<string> arguments, TimeSpan timeout, out string errors)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The application could not be started.");
        Task<string> reading = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill();
            errors = string.Empty;
            return -1;
        }

        errors = reading.GetAwaiter().GetResult().Trim();
        return process.ExitCode;
    }

    // ---- What the window shows ----

    /// <summary>The screen that is shown: "create", "unlock" or "main"; <see langword="null"/> while there is none.</summary>
    public string? Screen()
    {
        if (Shows("CreateButton"))
        {
            return "create";
        }

        if (Shows("UnlockButton"))
        {
            return "unlock";
        }

        return Shows("LockButton") ? "main" : null;
    }

    public IReadOnlyList<AutomationElement> FindAll(string id)
    {
        try
        {
            AutomationElementCollection found = Window.FindAll(
                TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            return [.. found.Cast<AutomationElement>()];
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return [];
        }
    }

    public AutomationElement? TryFind(string id, int index = 0)
    {
        IReadOnlyList<AutomationElement> found = FindAll(id);
        return index < found.Count ? found[index] : null;
    }

    public bool Shows(string id) => TryFind(id) is not null;

    public AutomationElement Find(string id, int index = 0)
    {
        AutomationElement? found = null;
        WaitUntil(() => (found = TryFind(id, index)) is not null, $"'{id}' is shown");
        return found!;
    }

    /// <summary>The text of a text element.</summary>
    public string Text(string id, int index = 0) => Find(id, index).Current.Name;

    /// <summary>The text of a text element, or <see langword="null"/> if it is not shown.</summary>
    public string? TextOrNull(string id, int index = 0)
    {
        try
        {
            return TryFind(id, index)?.Current.Name;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Every text the window shows, as the accessibility interface hands it out: names and values.</summary>
    public IReadOnlyList<string> EverythingShown()
    {
        var texts = new List<string>();
        foreach (AutomationElement element in Window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                if (!string.IsNullOrEmpty(element.Current.Name))
                {
                    texts.Add(element.Current.Name);
                }

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                    && ((ValuePattern)pattern).Current.Value is { Length: > 0 } value)
                {
                    texts.Add(value);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
            }
        }

        return texts;
    }

    /// <summary>A description of everything in the window, for finding one's way.</summary>
    public string Describe()
    {
        var description = new StringBuilder();
        foreach (AutomationElement element in Window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                AutomationElement.AutomationElementInformation current = element.Current;
                string patterns = string.Join(
                    ',', element.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", string.Empty, StringComparison.Ordinal)));
                description.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)} "
                    + $"id='{current.AutomationId}' name='{current.Name}' enabled={current.IsEnabled} focus={current.HasKeyboardFocus} [{patterns}]");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
            }
        }

        return description.ToString();
    }

    // ---- Operating it ----

    /// <summary>Enters text into a text box.</summary>
    public void Type(string id, string text)
        => ((ValuePattern)Find(id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>What a text box hands out as its content.</summary>
    public string Value(string id)
        => ((ValuePattern)Find(id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    /// <summary>Activates a button.</summary>
    public void Press(string id, int index = 0)
    {
        AutomationElement button = Find(id, index);
        if (!button.Current.IsEnabled)
        {
            throw new InvalidOperationException($"The button '{id}' is disabled.");
        }

        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    public bool IsEnabled(string id) => Find(id).Current.IsEnabled;

    public bool HasFocus(string id) => Find(id).Current.HasKeyboardFocus;

    public bool IsChecked(string id)
        => ((TogglePattern)Find(id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    /// <summary>Checks or unchecks a check box.</summary>
    public void SetChecked(string id, bool value)
    {
        if (IsChecked(id) != value)
        {
            ((TogglePattern)Find(id).GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        }
    }

    /// <summary>
    /// A key pressed and released in the window. Sent to this window only — never to whatever
    /// window happens to be in front, as simulated keyboard input would be.
    /// </summary>
    public void Key(int virtualKey)
    {
        NativeMethods.PostMessageW(Handle, NativeMethods.KeyDown, (nuint)virtualKey, 0x00000001);
        Thread.Sleep(50);
        NativeMethods.PostMessageW(Handle, NativeMethods.KeyUp, (nuint)virtualKey, unchecked((nint)0xC0000001));
        Thread.Sleep(150);
    }

    /// <summary>The mouse moved inside the window. Sent to this window only.</summary>
    public void MoveMouse(int x, int y)
        => NativeMethods.PostMessageW(Handle, NativeMethods.MouseMove, 0, (y << 16) | (x & 0xFFFF));

    /// <summary>
    /// Where the mouse of the machine is, if it is over this window and nothing is in front of it
    /// there; otherwise <see langword="null"/>. Somebody may be sitting at the machine: what they
    /// do with the mouse over the window is activity, and the application is right to count it.
    /// </summary>
    public (int X, int Y)? MouseOfTheMachine()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.Point position))
        {
            return null;
        }

        nint under = NativeMethods.WindowFromPoint(position);
        return under != 0 && NativeMethods.GetAncestor(under, NativeMethods.Root) == Handle
            ? (position.X, position.Y)
            : null;
    }

    public void Minimize() => NativeMethods.ShowWindow(Handle, NativeMethods.ShowMinimized);

    public void Restore() => NativeMethods.ShowWindow(Handle, NativeMethods.ShowRestored);

    /// <summary>
    /// Tells the application that the workstation was locked, with the message Windows sends for
    /// it. The workstation itself is not locked.
    /// </summary>
    public bool SignalWorkstationLocked()
        => PostToSystemEventsWindow(NativeMethods.SessionChange, NativeMethods.SessionLock);

    /// <summary>
    /// Tells the application that the system is going to sleep, with the message Windows sends for
    /// it. The system itself does not go to sleep.
    /// </summary>
    public bool SignalSuspending()
        => PostToSystemEventsWindow(NativeMethods.PowerBroadcast, NativeMethods.Suspend);

    // .NET receives the session and power messages of Windows in a hidden window of its own.
    private bool PostToSystemEventsWindow(uint message, nuint parameter)
    {
        bool posted = false;
        NativeMethods.EnumWindows(
            (window, _) =>
            {
                uint thread = NativeMethods.GetWindowThreadProcessId(window, out uint process);
                if (thread != 0 && process == (uint)_process.Id && ClassName(window).StartsWith(".NET-BroadcastEventWindow", StringComparison.Ordinal))
                {
                    posted |= NativeMethods.PostMessageW(window, message, parameter, (nint)_process.SessionId);
                }

                return true;
            },
            0);
        return posted;
    }

    private static string ClassName(nint window)
    {
        char[] name = new char[256];
        int length = NativeMethods.GetClassNameW(window, name, name.Length);
        return new string(name, 0, Math.Max(0, length));
    }

    /// <summary>Waits until <paramref name="condition"/> holds, as a person waits for the screen to change.</summary>
    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? DefaultTimeout;
        while (true)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // The screen changed while it was being read; look again.
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The application ended (code {_process.ExitCode}) while waiting for: {what}.");
            }

            if (watch.Elapsed > limit)
            {
                throw new TimeoutException(
                    $"Waited {limit.TotalSeconds:0} s, but this did not happen: {what}. {WhatIsThere()}");
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// What the window shows, in a line, for saying what was there instead of what was waited for:
    /// the screen, a notice if there is one, and the names of the controls. No content.
    /// </summary>
    public string WhatIsThere()
    {
        try
        {
            string[] names =
            [
                .. Window.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Select(element => element.Current.AutomationId)
                    .Where(id => !string.IsNullOrEmpty(id) && !id.StartsWith("PART_", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal),
            ];
            return $"There was: the {Screen() ?? "unknown"} screen"
                   + (TextOrNull("NoticeText") is { } notice ? $", the notice \"{notice}\"" : string.Empty)
                   + $", with {string.Join(' ', names)}.";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return "What was there instead could not be read (" + ex.GetType().Name + ").";
        }
    }

    public void WaitForScreen(string screen, TimeSpan? timeout = null)
        => WaitUntil(() => Screen() == screen, $"the {screen} screen is shown", timeout);

    public void WaitForText(string id, string text, TimeSpan? timeout = null)
        => WaitUntil(
            () => TextOrNull(id)?.Contains(text, StringComparison.Ordinal) ?? false,
            $"'{id}' says \"{text}\"",
            timeout);

    /// <summary>Closes the window the way a user does, and returns how the application ended.</summary>
    public int Close()
    {
        if (!_process.HasExited)
        {
            _process.CloseMainWindow();
            if (!_process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                _process.Kill();
                _process.WaitForExit();
                return -1;
            }
        }

        return _process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private static class NativeMethods
    {
        public const uint KeyDown = 0x0100; // WM_KEYDOWN
        public const uint KeyUp = 0x0101; // WM_KEYUP
        public const uint MouseMove = 0x0200; // WM_MOUSEMOVE
        public const uint PowerBroadcast = 0x0218; // WM_POWERBROADCAST
        public const uint SessionChange = 0x02B1; // WM_WTSSESSION_CHANGE
        public const nuint Suspend = 0x0004; // PBT_APMSUSPEND
        public const nuint SessionLock = 0x7; // WTS_SESSION_LOCK
        public const int ShowMinimized = 6; // SW_MINIMIZE
        public const int ShowRestored = 9; // SW_RESTORE
        public const uint Root = 2; // GA_ROOT

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        public delegate bool EnumWindowsCallback(nint window, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point position);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint WindowFromPoint(Point position);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetAncestor(nint window, uint which);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetWindowThreadProcessId(nint window, out uint process);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetClassNameW(nint window, [Out] char[] name, int capacity);
    }
}
