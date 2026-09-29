using Avalonia.Input;

namespace BitosX.App.Services;

/// <summary>
/// Builds the clipboard content for a copied secret: the text itself plus, on Windows, the marker
/// formats that tell the operating system and clipboard managers not to keep a copy of it.
/// </summary>
/// <remarks>
/// <para>
/// Without these, Windows stores every copied password in the clipboard history (Win+V) — and, if
/// the user has it turned on, uploads it to the cloud clipboard — where it stays long after BitosX
/// has cleared the clipboard itself. The formats are documented by Microsoft ("Cloud Clipboard and
/// Clipboard History Formats"); "Clipboard Viewer Ignore" is the older convention that third-party
/// clipboard managers honor. Pasting is unaffected.
/// </para>
/// <para>
/// These are requests, honored by the operating system and by well-behaved clipboard managers. An
/// application that reads the clipboard directly still sees the text. No equivalent is set on macOS
/// or Linux yet; see docs/SECURITY_MODEL.md.
/// </para>
/// </remarks>
public static class SensitiveClipboardData
{
    /// <summary>Presence of this format keeps the content out of clipboard history and sync entirely.</summary>
    public const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>A DWORD of 0 keeps the content out of the clipboard history.</summary>
    public const string ClipboardHistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>A DWORD of 0 keeps the content from being uploaded to the cloud clipboard.</summary>
    public const string CloudClipboardFormat = "CanUploadToCloudClipboard";

    /// <summary>Presence of this format asks third-party clipboard managers to ignore the content.</summary>
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    /// <summary>Creates the clipboard content for <paramref name="text"/> on the current platform.</summary>
    public static DataTransfer Create(string text) => Create(text, OperatingSystem.IsWindows());

    /// <summary>Creates the clipboard content for <paramref name="text"/>.</summary>
    /// <param name="text">The secret to place on the clipboard.</param>
    /// <param name="markAsSensitive">Whether to add the Windows marker formats.</param>
    public static DataTransfer Create(string text, bool markAsSensitive)
    {
        ArgumentNullException.ThrowIfNull(text);

        var item = new DataTransferItem();
        item.SetText(text);

        if (markAsSensitive)
        {
            item.Set(DataFormat.CreateBytesPlatformFormat(ExcludeFromMonitoringFormat), BitConverter.GetBytes(1));
            item.Set(DataFormat.CreateBytesPlatformFormat(ClipboardHistoryFormat), BitConverter.GetBytes(0));
            item.Set(DataFormat.CreateBytesPlatformFormat(CloudClipboardFormat), BitConverter.GetBytes(0));
            item.Set(DataFormat.CreateBytesPlatformFormat(ViewerIgnoreFormat), BitConverter.GetBytes(1));
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }
}
