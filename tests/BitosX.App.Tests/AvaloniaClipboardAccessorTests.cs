using Avalonia.Controls;
using Avalonia.Input;
using BitosX.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Tests for how a secret is handed to the clipboard, in particular what happens when a clipboard
/// does not accept the sensitivity marker. The clipboard interface itself cannot be implemented
/// outside the UI framework, so the decision is tested through
/// <see cref="SensitiveClipboardData.PlaceAsync"/>, and the accessor against the clipboard of a
/// headless application (see <see cref="HeadlessUi"/>).
/// </summary>
public sealed class AvaloniaClipboardAccessorTests
{
    private const string Secret = "P@ssw0rd-Strong-123";

    /// <summary>Records what is placed on it; can refuse anything that is more than plain text.</summary>
    private sealed class RecordingClipboard
    {
        public bool RefuseMarkers { get; set; }
        public bool RefuseEverything { get; set; }
        public List<int> AcceptedFormatCounts { get; } = [];
        public int Attempts { get; private set; }
        public string? Text { get; private set; }

        public Task PlaceAsync(DataTransfer transfer)
        {
            Attempts++;
            bool marked = transfer.Formats.Any(f => f != DataFormat.Text);
            if (RefuseEverything || (RefuseMarkers && marked))
            {
                throw new InvalidOperationException("The clipboard refused the data.");
            }

            AcceptedFormatCounts.Add(transfer.Formats.Count);
            Text = transfer.TryGetText();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ClipboardPlatform.Windows, 5)]
    [InlineData(ClipboardPlatform.MacOS, 2)]
    [InlineData(ClipboardPlatform.Linux, 2)]
    [InlineData(ClipboardPlatform.Other, 1)]
    public async Task ASecret_IsPlacedWithThePlatformsMarker(ClipboardPlatform platform, int formats)
    {
        var clipboard = new RecordingClipboard();

        bool marked = await SensitiveClipboardData.PlaceAsync(Secret, platform, clipboard.PlaceAsync);

        Assert.True(marked);
        Assert.Equal(Secret, clipboard.Text);
        Assert.Equal([formats], clipboard.AcceptedFormatCounts);
        Assert.Equal(1, clipboard.Attempts);
    }

    [Theory]
    [InlineData(ClipboardPlatform.MacOS)]
    [InlineData(ClipboardPlatform.Linux)]
    public async Task WhereTheMarkerIsUntested_ARefusal_StillCopiesTheText(ClipboardPlatform platform)
    {
        var clipboard = new RecordingClipboard { RefuseMarkers = true };

        bool marked = await SensitiveClipboardData.PlaceAsync(Secret, platform, clipboard.PlaceAsync);

        // Copying is what the user asked for. Without the marker it is no worse than before 0.6.0.
        Assert.False(marked);
        Assert.Equal(Secret, clipboard.Text);
        Assert.Equal([1], clipboard.AcceptedFormatCounts);
        Assert.Equal(2, clipboard.Attempts);
    }

    [Theory]
    [InlineData(ClipboardPlatform.Windows)]
    [InlineData(ClipboardPlatform.Other)]
    public async Task Elsewhere_ARefusal_IsAFailure_NeverAnUnmarkedCopy(ClipboardPlatform platform)
    {
        var clipboard = new RecordingClipboard { RefuseMarkers = platform == ClipboardPlatform.Windows, RefuseEverything = platform == ClipboardPlatform.Other };

        // On Windows an unmarked copy would put the secret into the clipboard history. The copy
        // fails instead, and the view model tells the user.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SensitiveClipboardData.PlaceAsync(Secret, platform, clipboard.PlaceAsync));

        Assert.Null(clipboard.Text);
        Assert.Empty(clipboard.AcceptedFormatCounts);
        Assert.Equal(1, clipboard.Attempts);
    }

    [Theory]
    [InlineData(ClipboardPlatform.MacOS)]
    [InlineData(ClipboardPlatform.Linux)]
    public async Task AClipboardThatRefusesEverything_IsReported(ClipboardPlatform platform)
    {
        var clipboard = new RecordingClipboard { RefuseEverything = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SensitiveClipboardData.PlaceAsync(Secret, platform, clipboard.PlaceAsync));

        Assert.Null(clipboard.Text);
        Assert.Equal(2, clipboard.Attempts);
    }

    [Theory]
    [InlineData(ClipboardPlatform.Windows, false)]
    [InlineData(ClipboardPlatform.Other, false)]
    [InlineData(ClipboardPlatform.MacOS, true)]
    [InlineData(ClipboardPlatform.Linux, true)]
    public void AnUnmarkedCopy_IsAllowedOnlyWhereTheMarkerIsUntested(ClipboardPlatform platform, bool allowed)
        => Assert.Equal(allowed, SensitiveClipboardData.MayCopyWithoutMarker(platform));

    // ---- The accessor, against the clipboard of a headless application ----

    [Theory]
    [InlineData(ClipboardPlatform.Windows)]
    [InlineData(ClipboardPlatform.MacOS)]
    [InlineData(ClipboardPlatform.Linux)]
    [InlineData(ClipboardPlatform.Other)]
    public Task Accessor_Copies_ReadsBack_AndClears(ClipboardPlatform platform) => HeadlessUi.RunAsync(async () =>
    {
        Window window = HeadlessUi.Show(new TextBlock());
        var accessor = new AvaloniaClipboardAccessor(
            () => window.Clipboard, platform, NullLogger<AvaloniaClipboardAccessor>.Instance);

        await accessor.SetSecretTextAsync(Secret);
        Assert.Equal(Secret, await accessor.GetTextAsync());

        await accessor.ClearAsync();
        Assert.True(string.IsNullOrEmpty(await accessor.GetTextAsync()));
    });

    [Fact]
    public Task Accessor_WithoutAClipboard_DoesNothing_AndDoesNotThrow() => HeadlessUi.RunAsync(async () =>
    {
        var accessor = new AvaloniaClipboardAccessor(
            () => null, ClipboardPlatform.Windows, NullLogger<AvaloniaClipboardAccessor>.Instance);

        await accessor.SetSecretTextAsync(Secret);
        await accessor.ClearAsync();

        Assert.Null(await accessor.GetTextAsync());
    });
}
