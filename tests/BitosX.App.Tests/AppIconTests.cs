using Avalonia.Controls;
using Avalonia.Platform;
using BitosX.App.Services;
using BitosX.App.Views;

namespace BitosX.App.Tests;

/// <summary>
/// The application's own icon (drawn by tools/BitosX.IconGen) is the icon of every window it
/// opens, and the placeholder of the project template is gone.
/// </summary>
public sealed class AppIconTests
{
    private static readonly Uri BitosXIcon = new("avares://BitosX.App/Assets/bitosx.ico");

    [Fact]
    public Task TheMainWindow_AndTheStartupNotice_CarryTheBitosXIcon() => HeadlessUi.RunAsync(() =>
    {
        Assert.True(AssetLoader.Exists(BitosXIcon));
        Assert.False(AssetLoader.Exists(new Uri("avares://BitosX.App/Assets/avalonia-logo.ico")));

        var main = new MainWindow();
        Assert.NotNull(main.Icon);

        Window notice = StartupNotice.CreateWindow("BitosX", "BitosX is already running on this vault.");
        Assert.NotNull(notice.Icon);
        return Task.CompletedTask;
    });

    [Fact]
    public void TheIconFile_HoldsEverySizeWindowsAsksFor()
    {
        using Stream stream = OpenFromTheSourceTree();
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16()); // reserved
        Assert.Equal(1, reader.ReadUInt16()); // an icon, not a cursor
        int count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            byte width = reader.ReadByte();
            byte height = reader.ReadByte();
            Assert.Equal(width, height);
            sizes.Add(width == 0 ? 256 : width);
            reader.ReadBytes(14);
        }

        Assert.Superset(new HashSet<int> { 16, 24, 32, 48, 256 }, sizes.ToHashSet());
    }

    /// <summary>The icon file as it is in the source tree, which is what the program file on Windows embeds.</summary>
    private static FileStream OpenFromTheSourceTree()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "src", "BitosX.App", "Assets", "bitosx.ico");
            if (File.Exists(path))
            {
                return File.OpenRead(path);
            }
        }

        throw new FileNotFoundException("src/BitosX.App/Assets/bitosx.ico was not found above the test's directory.");
    }
}
