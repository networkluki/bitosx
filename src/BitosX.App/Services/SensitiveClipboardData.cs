using System.Text;
using Avalonia.Input;

namespace BitosX.App.Services;

/// <summary>The operating system whose clipboard conventions apply.</summary>
public enum ClipboardPlatform
{
    /// <summary>No known convention: the text is copied without a marker.</summary>
    Other = 0,

    /// <summary>Windows 10 or later.</summary>
    Windows,

    /// <summary>macOS.</summary>
    MacOS,

    /// <summary>Linux and other systems with an X11 or Wayland clipboard.</summary>
    Linux,
}

/// <summary>
/// Builds the clipboard content for a copied secret: the text itself plus the marker that, on the
/// platform at hand, tells the operating system and clipboard managers not to keep a copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows.</b> Without the markers, Windows stores every copied password in the clipboard
/// history (Win+V) — and, if the user has it turned on, uploads it to the cloud clipboard — where it
/// stays long after BitosX has cleared the clipboard itself. The formats are documented by Microsoft
/// ("Cloud Clipboard and Clipboard History Formats"); "Clipboard Viewer Ignore" is the older
/// convention that third-party clipboard managers honor. Verified against the real clipboard.
/// </para>
/// <para>
/// <b>macOS.</b> <c>org.nspasteboard.ConcealedType</c> is the convention published at
/// nspasteboard.org. Verified to be on the real pasteboard next to the text
/// (tools/BitosX.ClipboardCheck). No clipboard manager for macOS has been asked what it does with it.
/// </para>
/// <para>
/// <b>Linux.</b> <c>x-kde-passwordManagerHint</c> with the value <c>secret</c> is the convention
/// introduced by KDE's clipboard manager. Verified to be on the real clipboard (X11) next to the
/// text, and that KDE's clipboard manager does not record a copy that carries it. CopyQ 7.1.0
/// does record it.
/// </para>
/// <para>
/// All of these are requests. The operating system and some clipboard managers honor them; others
/// do not, and an application that reads the clipboard directly still sees the text. Pasting is
/// unaffected. See docs/SECURITY_MODEL.md.
/// </para>
/// </remarks>
public static class SensitiveClipboardData
{
    /// <summary>Windows: presence of this format keeps the content out of clipboard history and sync entirely.</summary>
    public const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Windows: a DWORD of 0 keeps the content out of the clipboard history.</summary>
    public const string ClipboardHistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>Windows: a DWORD of 0 keeps the content from being uploaded to the cloud clipboard.</summary>
    public const string CloudClipboardFormat = "CanUploadToCloudClipboard";

    /// <summary>Windows: presence of this format asks third-party clipboard managers to ignore the content.</summary>
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    /// <summary>macOS: presence of this type marks the content as a password or other secret.</summary>
    public const string MacConcealedType = "org.nspasteboard.ConcealedType";

    /// <summary>Linux: with the value <see cref="LinuxPasswordHintValue"/>, marks the content as a secret.</summary>
    public const string LinuxPasswordHintFormat = "x-kde-passwordManagerHint";

    /// <summary>The value that goes with <see cref="LinuxPasswordHintFormat"/>.</summary>
    public const string LinuxPasswordHintValue = "secret";

    /// <summary>The platform the application is running on.</summary>
    public static ClipboardPlatform CurrentPlatform
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return ClipboardPlatform.Windows;
            }

            if (OperatingSystem.IsMacOS())
            {
                return ClipboardPlatform.MacOS;
            }

            return OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()
                ? ClipboardPlatform.Linux
                : ClipboardPlatform.Other;
        }
    }

    /// <summary>Creates the clipboard content for <paramref name="text"/> on the current platform.</summary>
    public static DataTransfer Create(string text) => Create(text, CurrentPlatform);

    /// <summary>Creates the clipboard content for <paramref name="text"/>.</summary>
    /// <param name="text">The secret to place on the clipboard.</param>
    /// <param name="platform">The platform whose marker to add.</param>
    public static DataTransfer Create(string text, ClipboardPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(text);

        var item = new DataTransferItem();
        item.SetText(text);

        switch (platform)
        {
            case ClipboardPlatform.Windows:
                Mark(item, ExcludeFromMonitoringFormat, BitConverter.GetBytes(1));
                Mark(item, ClipboardHistoryFormat, BitConverter.GetBytes(0));
                Mark(item, CloudClipboardFormat, BitConverter.GetBytes(0));
                Mark(item, ViewerIgnoreFormat, BitConverter.GetBytes(1));
                break;

            case ClipboardPlatform.MacOS:
                // Only the presence of the type matters; the secret itself is never repeated in it.
                Mark(item, MacConcealedType, [1]);
                break;

            case ClipboardPlatform.Linux:
                Mark(item, LinuxPasswordHintFormat, Encoding.ASCII.GetBytes(LinuxPasswordHintValue));
                break;
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    /// <summary>
    /// Whether a copy may fall back to plain text when the clipboard refuses the marked content.
    /// On macOS and Linux, where there are more kinds of clipboard than the two the marker has been
    /// verified on (macOS 15, and X11): there copying must keep working, and an unmarked copy is
    /// what every version before 0.6.0 made. Never on Windows, where an unmarked copy would put the
    /// secret into the clipboard history — the copy fails instead, and the user is told.
    /// </summary>
    public static bool MayCopyWithoutMarker(ClipboardPlatform platform)
        => platform is ClipboardPlatform.MacOS or ClipboardPlatform.Linux;

    /// <summary>
    /// Places <paramref name="text"/> on a clipboard through <paramref name="place"/>, with the
    /// marker of <paramref name="platform"/>. If the clipboard refuses the marked content and
    /// <see cref="MayCopyWithoutMarker"/> allows it, the text is placed without the marker.
    /// </summary>
    /// <returns>Whether the text was placed with its marker.</returns>
    public static async Task<bool> PlaceAsync(string text, ClipboardPlatform platform, Func<DataTransfer, Task> place)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(place);

        try
        {
            await place(Create(text, platform)).ConfigureAwait(true);
            return true;
        }
        catch (Exception) when (MayCopyWithoutMarker(platform))
        {
            await place(Create(text, ClipboardPlatform.Other)).ConfigureAwait(true);
            return false;
        }
    }

    private static void Mark(DataTransferItem item, string format, byte[] value)
        => item.Set(DataFormat.CreateBytesPlatformFormat(format), value);
}
