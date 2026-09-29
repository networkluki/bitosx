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

        Assert.Equal([_path], Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Load_OversizedFile_ReturnsDefaults_WithoutReadingIt()
    {
        // A settings file is a few hundred bytes; anything huge is not ours and must not be
        // loaded into memory at startup.
        using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(FileAppSettingsStore.MaxFileSizeBytes + 1);
        }

        var store = new FileAppSettingsStore(_path);

        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Fact]
    public void Load_FileWithByteOrderMark_IsAccepted()
    {
        var store = new FileAppSettingsStore(_path);
        store.Save(new AppSettings { LockOnMinimize = true, InactivityTimeout = TimeSpan.FromMinutes(3) });
        File.WriteAllBytes(_path, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(_path)]);

        AppSettings loaded = store.Load();

        Assert.True(loaded.LockOnMinimize);
        Assert.Equal(TimeSpan.FromMinutes(3), loaded.InactivityTimeout);
    }

    [Theory]
    [InlineData("{\"inactivityTimeoutSeconds\":0}")]
    [InlineData("{\"inactivityTimeoutSeconds\":-5}")]
    [InlineData("{\"inactivityTimeoutSeconds\":2147483647}")]
    public void Load_OutOfRangeTimeout_IsClampedIntoTheAcceptedRange(string json)
    {
        // The file is not authenticated, so whatever it says is clamped: auto-lock can be turned
        // off by editing it, but it cannot be given a timeout outside the supported range.
        File.WriteAllText(_path, json);

        AppSettings loaded = new FileAppSettingsStore(_path).Load();

        Assert.InRange(loaded.InactivityTimeout, AppSettings.MinInactivityTimeout, AppSettings.MaxInactivityTimeout);
    }

    [Fact]
    public void Save_OverAnExistingFile_ReplacesIt_AndLeavesNothingElse()
    {
        var store = new FileAppSettingsStore(_path);
        store.Save(AppSettings.Default);
        store.Save(new AppSettings { LockOnMinimize = true });

        Assert.True(store.Load().LockOnMinimize);
        Assert.Equal([_path], Directory.GetFiles(_tempDir));
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
