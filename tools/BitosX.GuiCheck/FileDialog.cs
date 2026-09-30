using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace BitosX.GuiCheck;

/// <summary>
/// Answers the file dialog of Windows that the application opens for a backup: enters a path and
/// confirms, as a user does. The dialog belongs to Windows, not to the application; it is found
/// as a window of the application's process.
/// </summary>
internal static class FileDialog
{
    private const string DialogClass = "#32770";

    // The file name box of a "save" dialog and of an "open" dialog, and the button that confirms.
    private static readonly string[] FileNameBoxes = ["1001", "1148"];
    private const string ConfirmButton = "1";

    /// <summary>Enters <paramref name="path"/> into the file dialog of the process and confirms.</summary>
    public static void Answer(int processId, string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        AutomationElement dialog = WaitFor(processId, timeout)
            ?? throw new TimeoutException("The file dialog did not open.");

        // A dialog fills in its own suggestion after it has appeared, and on a slow machine well
        // after. The path is entered until it is what the box holds, and only then confirmed.
        string last = "nothing was entered";
        while (watch.Elapsed < timeout)
        {
            try
            {
                if (Find(processId) is not { } current)
                {
                    return; // confirmed, and closed
                }

                dialog = current;
                if (FileNameBox(dialog) is not { } box)
                {
                    last = "the dialog has no box for the file name yet";
                    Thread.Sleep(250);
                    continue;
                }

                var value = (ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern);
                if (!string.Equals(value.Current.Value, path, StringComparison.OrdinalIgnoreCase))
                {
                    Enter(box, value, path);
                    Thread.Sleep(400);
                    last = "the path was entered";
                    continue; // look again: is it still what the box holds?
                }

                AutomationElement? confirm = dialog.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, ConfirmButton),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                if (confirm is null)
                {
                    last = "the dialog has no button to confirm with";
                    Thread.Sleep(250);
                    continue;
                }

                ((InvokePattern)confirm.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                last = "the path was entered and confirmed";

                var closing = Stopwatch.StartNew();
                while (Find(processId) is not null && closing.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // The dialog changed while it was being read; look again.
                last = ex.GetType().Name;
                Thread.Sleep(250);
            }
        }

        if (Find(processId) is null)
        {
            return;
        }

        throw new TimeoutException($"The file dialog did not close ({last}). {WhatIsThere(dialog)}");
    }

    /// <summary>Whether a file dialog of the process is open.</summary>
    public static bool IsOpen(int processId) => Find(processId) is not null;

    /// <summary>
    /// Enters the path the way typing does: everything in the box is selected and the characters
    /// are sent to it one by one. The dialog takes the name it saves under from what was typed; a
    /// text that is merely placed in the box can be left unnoticed, and the dialog then saves
    /// under its own suggestion, elsewhere.
    /// </summary>
    private static void Enter(AutomationElement box, ValuePattern value, string path)
    {
        nint edit = new(box.Current.NativeWindowHandle);
        if (edit == 0)
        {
            value.SetValue(path);
            return;
        }

        NativeMethods.SendMessageW(edit, NativeMethods.SelectAll, 0, -1);
        foreach (char character in path)
        {
            NativeMethods.SendMessageW(edit, NativeMethods.Character, character, 0);
        }
    }

    private static class NativeMethods
    {
        public const uint SelectAll = 0x00B1; // EM_SETSEL, with 0 and -1
        public const uint Character = 0x0102; // WM_CHAR

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    }

    private static AutomationElement? FileNameBox(AutomationElement dialog)
        => FileNameBoxes
            .Select(id => dialog.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))))
            .FirstOrDefault(found => found is not null);

    // The kinds and names of what the dialog shows: its own texts, and the names of files in a
    // directory of this tool.
    private static string WhatIsThere(AutomationElement dialog)
    {
        try
        {
            string[] shown =
            [
                .. dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Where(element => element.Current.ControlType == ControlType.Text
                                      || element.Current.ControlType == ControlType.Button
                                      || element.Current.ControlType == ControlType.Edit
                                      || element.Current.ControlType == ControlType.Window)
                    .Select(element => $"{element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)} '{element.Current.AutomationId}' \"{element.Current.Name}\"")
                    .Take(40),
            ];
            return "In the dialog: " + string.Join("; ", shown);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return "What the dialog showed could not be read (" + ex.GetType().Name + ").";
        }
    }

    private static AutomationElement? WaitFor(int processId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (Find(processId) is { } dialog)
            {
                return dialog;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static AutomationElement? Find(int processId)
    {
        try
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ClassNameProperty, DialogClass));

            // A dialog is a window of its own, listed at the top or under the window that owns it.
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition)
                   ?? AutomationElement.RootElement
                       .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId))
                       .Cast<AutomationElement>()
                       .Select(window => window.FindFirst(TreeScope.Children, condition))
                       .FirstOrDefault(found => found is not null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
