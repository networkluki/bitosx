using System.Text;
using Avalonia.Input;
using BitosX.App.Services;

namespace BitosX.App.Tests;

/// <summary>
/// Tests for the content BitosX places on the clipboard when a secret is copied. These check what
/// is handed to the platform. That Windows then keeps it out of the clipboard history was verified
/// against the real clipboard and is recorded in docs/VERIFICATION.md; for macOS and Linux nothing
/// beyond what is tested here has been verified.
/// </summary>
public sealed class SensitiveClipboardDataTests
{
    private const string Secret = "P@ssw0rd-Strong-123";

    public static TheoryData<ClipboardPlatform> AllPlatforms =>
        [ClipboardPlatform.Other, ClipboardPlatform.Windows, ClipboardPlatform.MacOS, ClipboardPlatform.Linux];

    private static byte[]? Raw(DataTransfer transfer, string platformFormat)
        => Assert.Single(transfer.Items).TryGetRaw(DataFormat.CreateBytesPlatformFormat(platformFormat)) as byte[];

    private static string[] MarkerNames(DataTransfer transfer)
        => [.. transfer.Formats.Where(f => f != DataFormat.Text).Select(f => f.Identifier).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void Create_AlwaysCarriesTheTextSoItCanBePasted(ClipboardPlatform platform)
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, platform);

        Assert.Equal(Secret, transfer.TryGetText());
        Assert.Contains(DataFormat.Text, transfer.Formats);
    }

    [Fact]
    public void Windows_AsksToBeKeptOutOfHistoryAndCloudSync()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, ClipboardPlatform.Windows);

        // Documented by Microsoft: a DWORD of 0 means "do not include".
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Raw(transfer, "CanIncludeInClipboardHistory"));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Raw(transfer, "CanUploadToCloudClipboard"));

        // For these two, being present is what matters.
        Assert.NotNull(Raw(transfer, "ExcludeClipboardContentFromMonitorProcessing"));
        Assert.NotNull(Raw(transfer, "Clipboard Viewer Ignore"));
        Assert.Equal(4, MarkerNames(transfer).Length);
    }

    [Fact]
    public void MacOS_MarksTheContentAsConcealed()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, ClipboardPlatform.MacOS);

        // nspasteboard.org: the presence of the type is what marks the content.
        Assert.Equal(["org.nspasteboard.ConcealedType"], MarkerNames(transfer));
        Assert.NotEmpty(Raw(transfer, "org.nspasteboard.ConcealedType")!);
    }

    [Fact]
    public void Linux_CarriesThePasswordManagerHint()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, ClipboardPlatform.Linux);

        Assert.Equal(["x-kde-passwordManagerHint"], MarkerNames(transfer));
        Assert.Equal("secret", Encoding.ASCII.GetString(Raw(transfer, "x-kde-passwordManagerHint")!));
    }

    [Fact]
    public void Other_IsPlainText()
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, ClipboardPlatform.Other);

        Assert.Equal([DataFormat.Text], transfer.Formats);
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void TheMarkers_NeverContainTheSecret(ClipboardPlatform platform)
    {
        DataTransfer transfer = SensitiveClipboardData.Create(Secret, platform);

        foreach (DataFormat format in transfer.Formats.Where(f => f != DataFormat.Text))
        {
            byte[] raw = Assert.IsType<byte[]>(Assert.Single(transfer.Items).TryGetRaw(format));
            Assert.InRange(raw.Length, 1, 8);
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, Encoding.Unicode.GetString(raw), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CurrentPlatform_IsTheOneTheTestsRunOn()
    {
        ClipboardPlatform expected =
            OperatingSystem.IsWindows() ? ClipboardPlatform.Windows
            : OperatingSystem.IsMacOS() ? ClipboardPlatform.MacOS
            : OperatingSystem.IsLinux() ? ClipboardPlatform.Linux
            : SensitiveClipboardData.CurrentPlatform;

        Assert.Equal(expected, SensitiveClipboardData.CurrentPlatform);
        Assert.Equal(
            SensitiveClipboardData.Create(Secret, expected).Formats.Count,
            SensitiveClipboardData.Create(Secret).Formats.Count);
    }
}
