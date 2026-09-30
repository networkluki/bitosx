using BitosX.Storage;

namespace BitosX.App.Tests;

/// <summary>
/// Where the application keeps its files, and what the <c>--data-directory</c> option does. The
/// rule that matters: an option that is given but cannot be used never leads to the default vault.
/// </summary>
public sealed class AppLocationTests
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "BitosX.AppLocationTests", "data");

    [Fact]
    public void WithoutTheOption_TheLocationIsThePerUserOne()
    {
        AppLocation location = AppLocation.FromArguments([]);

        Assert.True(location.IsDefault);
        Assert.Null(location.DataDirectory);
        Assert.Equal(FileLocalVaultStorage.DefaultVaultFilePath, location.VaultFilePath);
        Assert.Equal(FileAppSettingsStore.DefaultPath, location.SettingsFilePath);
        Assert.Equal("BitosX", location.WindowTitle);
        Assert.Same(AppLocation.Default, location);
    }

    [Fact]
    public void OtherArguments_AreLeftAlone()
    {
        AppLocation location = AppLocation.FromArguments(["--something", "else", "-psn_0_12345"]);

        Assert.True(location.IsDefault);
    }

    [Fact]
    public void TheOption_NamesTheDirectoryOfTheVaultAndTheSettings()
    {
        AppLocation location = AppLocation.FromArguments(["--data-directory", Directory]);

        Assert.False(location.IsDefault);
        Assert.Equal(Directory, location.DataDirectory);
        Assert.Equal(Path.Combine(Directory, "vault.bitosx"), location.VaultFilePath);
        Assert.Equal(Path.Combine(Directory, "settings.json"), location.SettingsFilePath);
    }

    [Fact]
    public void TheOption_MayBeWrittenWithAnEqualsSign()
    {
        AppLocation location = AppLocation.FromArguments(["--data-directory=" + Directory]);

        Assert.Equal(Directory, location.DataDirectory);
    }

    [Fact]
    public void TheOption_MayStandAmongOtherArguments()
    {
        AppLocation location = AppLocation.FromArguments(["--first", "--data-directory", Directory, "--last"]);

        Assert.Equal(Directory, location.DataDirectory);
    }

    [Fact]
    public void ATrailingSeparator_MakesNoDifference()
    {
        AppLocation location = AppLocation.FromArguments(["--data-directory", Directory + Path.DirectorySeparatorChar]);

        Assert.Equal(Directory, location.DataDirectory);
        Assert.Equal(Path.Combine(Directory, "vault.bitosx"), location.VaultFilePath);
    }

    [Fact]
    public void TheWindowTitle_NamesTheDirectory_SoThatItIsVisibleWhichVaultIsOpen()
    {
        AppLocation location = AppLocation.In(Directory);

        Assert.Equal("BitosX — " + Directory, location.WindowTitle);
    }

    [Fact]
    public void TheDirectory_NeedNotExist_AndIsNotCreatedByNamingIt()
    {
        string missing = Path.Combine(Path.GetTempPath(), "BitosX.AppLocationTests", Guid.NewGuid().ToString("N"));

        AppLocation location = AppLocation.In(missing);

        Assert.Equal(missing, location.DataDirectory);
        Assert.False(System.IO.Directory.Exists(missing));
    }

    // ---- An option that cannot be used is an error, never the default vault ----

    private static readonly string[][] Unusable =
    [
        ["--data-directory"], // no directory
        ["--data-directory", ""],
        ["--data-directory", "   "],
        ["--data-directory="],
        ["--data-directory", "relative" + Path.DirectorySeparatorChar + "path"],
        ["--data-directory", "."],
        ["--data-directory", Directory, "--data-directory", Directory], // twice
        ["--data-directory=" + Directory, "--data-directory", Directory],
        ["--data-directories", Directory], // misspelled
        ["--data-dir", Directory],
        ["--datadirectory", Directory],
        ["--data", Directory],
        ["--data-directory:" + Directory],
        ["--DATA-DIRECTORY", Directory],
        ["--Data-Directory=" + Directory],
    ];

    public static IEnumerable<object[]> UnusableArguments => Unusable.Select(arguments => new object[] { arguments });

    [Theory]
    [MemberData(nameof(UnusableArguments))]
    public void AnOptionThatCannotBeUsed_IsRefused(string[] arguments)
    {
        AppLocationException refused = Assert.Throws<AppLocationException>(() => AppLocation.FromArguments(arguments));

        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
    }

    [Fact]
    public void AFile_IsNotADirectory()
    {
        string file = Path.Combine(Path.GetTempPath(), "BitosX.AppLocationTests." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            Assert.Throws<AppLocationException>(() => AppLocation.In(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheServices_UseTheLocationTheyAreGiven()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BitosX.AppLocationTests", Guid.NewGuid().ToString("N"));
        AppLocation location = AppLocation.In(directory);

        using Microsoft.Extensions.DependencyInjection.ServiceProvider services = ServiceConfiguration.Build(location);

        var storage = (FileLocalVaultStorage)services.GetService(typeof(FileLocalVaultStorage))!;
        Assert.Equal(location.VaultFilePath, storage.VaultFilePath);
        Assert.Same(location, services.GetService(typeof(AppLocation)));
        Assert.False(System.IO.Directory.Exists(directory)); // nothing is written by starting
    }
}
