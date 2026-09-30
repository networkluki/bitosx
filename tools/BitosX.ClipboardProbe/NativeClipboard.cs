using System.Runtime.InteropServices;
using System.Text;

namespace BitosX.ClipboardProbe;

/// <summary>
/// Reads the clipboard the way any other Windows application would (Win32), independently of the
/// UI framework BitosX uses to write to it.
/// </summary>
internal static class NativeClipboard
{
    private const uint UnicodeText = 13; // CF_UNICODETEXT
    private const uint Moveable = 0x0002; // GMEM_MOVEABLE

    /// <summary>The names of the formats currently on the clipboard.</summary>
    public static IReadOnlyList<string> FormatNames()
    {
        var names = new List<string>();
        if (!Open())
        {
            return names;
        }

        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                char[] name = new char[256];
                int length = GetClipboardFormatNameW(format, name, name.Length);
                names.Add(length > 0 ? new string(name, 0, length) : "#" + format);
            }
        }
        finally
        {
            CloseClipboard();
        }

        return names;
    }

    /// <summary>The clipboard text, or <see langword="null"/> if the clipboard holds no text.</summary>
    public static string? Text()
    {
        if (!Open())
        {
            return null;
        }

        try
        {
            nint handle = GetClipboardData(UnicodeText);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Replaces the clipboard content with plain text (used to put the user's text back).</summary>
    public static bool SetText(string text)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            nint memory = GlobalAlloc(Moveable, (nuint)bytes.Length);
            if (memory == 0)
            {
                return false;
            }

            nint pointer = GlobalLock(memory);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(memory);
            return SetClipboardData(UnicodeText, memory) != 0; // the clipboard now owns the memory
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Replaces the clipboard content with text that Windows is asked to keep out of the clipboard
    /// history, so that a check leaves nothing behind there.
    /// </summary>
    public static bool SetTextOutsideHistory(string text)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            return Place(UnicodeText, Encoding.Unicode.GetBytes(text + "\0"))
                   && Place(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), BitConverter.GetBytes(1));
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Place(uint format, byte[] bytes)
    {
        nint memory = GlobalAlloc(Moveable, (nuint)bytes.Length);
        if (format == 0 || memory == 0)
        {
            return false;
        }

        nint pointer = GlobalLock(memory);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(memory);
        return SetClipboardData(format, memory) != 0; // the clipboard now owns the memory
    }

    /// <summary>Empties the clipboard.</summary>
    public static void Clear()
    {
        if (Open())
        {
            EmptyClipboard();
            CloseClipboard();
        }
    }

    private static bool Open()
    {
        // Another application may hold the clipboard for a moment.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint newOwner);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetClipboardFormatNameW(uint format, [Out] char[] name, int capacity);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
}
