using BitosX.Core.Settings;
using BitosX.Storage;

namespace BitosX.Storage.Tests;

public sealed class FileAppSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public FileAppSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
    }

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaults()
    {
        var store = new FileAppSettingsStore(_path);
        AppSettings loaded = store.Load();

        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new FileAppSettingsStore(_path);
        var settings = new AppSettings
        {
            AutoLockOnInactivity = false,
            InactivityTimeout = TimeSpan.FromMinutes(15),
            LockOnMinimize = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.False(loaded.AutoLockOnInactivity);
        Assert.Equal(TimeSpan.FromMinutes(15), loaded.InactivityTimeout);
        Assert.True(loaded.LockOnMinimize);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(_path, "this is not json");
        var store = new FileAppSettingsStore(_path);

        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Fact]
    public void Save_ClampsOutOfRangeTimeout()
    {
        var store = new FileAppSettingsStore(_path);
        store.Save(new AppSettings { InactivityTimeout = TimeSpan.FromSeconds(1) }); // below the floor

        AppSettings loaded = store.Load();
        Assert.Equal(AppSettings.MinInactivityTimeout, loaded.InactivityTimeout);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var store = new FileAppSettingsStore(_path);
        store.Save(AppSettings.Default);

        Assert.False(File.Exists(_path + ".tmp"));
        Assert.True(File.Exists(_path));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
