using Avalonia.Input;
using BitosX.App.Services;

namespace BitosX.App.Tests;

/// <summary>
/// Tests for the content BitosX places on the clipboard when a secret is copied (0.5.1). These
/// check what is handed to the platform; that Windows then keeps it out of the clipboard history
/// was verified against the real clipboard and is recorded in docs/VERIFICATION.md.
/// </summary>
public sealed class SensitiveClipboardDataTests
{
    private const string Secret = "P@ssw0rd-Strong-123";

    private static byte[]? Raw(DataTransfer transfer, string platformFormat)
        => Assert.Single(transfer.Items).TryGetRaw(DataFormat.CreateBytesPlatformFormat(platformFormat)) as byte[];

    [Fact]
    public void Create_CarriesTheTextSoItCanBePasted()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, markAsSensitive: true);

        Assert.Equal(Secret, transfer.TryGetText());
    }

    [Fact]
    public void Create_Sensitive_AsksWindowsToKeepItOutOfHistoryAndCloudSync()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, markAsSensitive: true);

        // Documented by Microsoft: a DWORD of 0 means "do not include".
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Raw(transfer, "CanIncludeInClipboardHistory"));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Raw(transfer, "CanUploadToCloudClipboard"));

        // For these two, being present is what matters.
        Assert.NotNull(Raw(transfer, "ExcludeClipboardContentFromMonitorProcessing"));
        Assert.NotNull(Raw(transfer, "Clipboard Viewer Ignore"));
    }

    [Fact]
    public void Create_Sensitive_AddsNothingButTheTextAndTheMarkers()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, markAsSensitive: true);

        Assert.Equal(5, transfer.Formats.Count);
        Assert.Contains(DataFormat.Text, transfer.Formats);
    }

    [Fact]
    public void Create_NotSensitive_IsPlainText()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, markAsSensitive: false);

        Assert.Equal([DataFormat.Text], transfer.Formats);
        Assert.Equal(Secret, transfer.TryGetText());
    }

    [Fact]
    public void Create_OnThisPlatform_MarksAsSensitiveExactlyOnWindows()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret);

        Assert.Equal(OperatingSystem.IsWindows() ? 5 : 1, transfer.Formats.Count);
    }

    [Fact]
    public void Create_TheMarkersNeverContainTheSecret()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, markAsSensitive: true);

        foreach (DataFormat format in transfer.Formats.Where(f => f != DataFormat.Text))
        {
            byte[] raw = Assert.IsType<byte[]>(Assert.Single(transfer.Items).TryGetRaw(format));
            Assert.Equal(4, raw.Length);
        }
    }
}
