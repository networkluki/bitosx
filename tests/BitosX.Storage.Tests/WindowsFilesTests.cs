using BitosX.Storage;

namespace BitosX.Storage.Tests;

/// <summary>
/// What is specific to Windows in how a file is replaced: it cannot be renamed over while another
/// program has it open, and virus scanners, indexers and backup tools open freshly written files for a
/// moment. These run on Windows; elsewhere they are reported as skipped.
/// <para>The waiting between attempts is handed in by the tests, so nothing here depends on the clock
/// or on how busy the machine is. (The first version of these tests let go of the file from a timer,
/// and failed on a loaded build machine when the timer ran late.)</para>
/// </summary>
public sealed class WindowsFilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly string _replacement;

    public WindowsFilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
        _replacement = _path + ".replacement";
        File.WriteAllText(_path, "old");
        File.WriteAllText(_replacement, "new");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opens the file the way File.ReadAllText and most scanners do: read, sharing read only.</summary>
    private FileStream HoldOpen() => new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static double[] Milliseconds(List<TimeSpan> pauses) => [.. pauses.Select(pause => pause.TotalMilliseconds)];

    [WindowsFact]
    public void AFileHeldOpenForAMoment_IsReplacedOnceItIsLetGo()
    {
        var pauses = new List<TimeSpan>();
        FileStream held = HoldOpen();
        try
        {
            StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pause =>
            {
                pauses.Add(pause);
                if (pauses.Count < 3)
                {
                    return; // the handle below is still open, whatever the clock says
                }

                // The other program is done with the file: let go here, not from a timer. From then
                // on wait for real, as the application does - a scanner may look at the file just now.
                held.Dispose();
                Thread.Sleep(pause);
            });
        }
        finally
        {
            held.Dispose();
        }

        // The attempts that met the open handle each asked for the next, longer pause.
        Assert.True(pauses.Count >= 3, $"{pauses.Count} pauses");
        Assert.Equal([10d, 20d, 40d], Milliseconds(pauses)[..3]);
        Assert.Equal("new", File.ReadAllText(_path));
        Assert.False(File.Exists(_replacement));
    }

    [WindowsFact]
    public void AFileHeldOpenLongerThanTheRetries_IsLeftAsItWas_AndTheFailureIsReported()
    {
        var pauses = new List<TimeSpan>();
        Exception failure;
        using (HoldOpen())
        {
            failure = Assert.ThrowsAny<Exception>(
                () => StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pauses.Add));
        }

        Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().FullName);

        // The pauses grow to 200 ms and stop before they would add up to more than the budget.
        Assert.Equal([10d, 20d, 40d, 80d, 160d, 200d, 200d, 200d, 200d, 200d], Milliseconds(pauses));
        Assert.True(pauses.Aggregate(TimeSpan.Zero, (sum, pause) => sum + pause) <= StorageFiles.WindowsReplaceRetryBudget);

        // Every failed attempt left both files what and where they were.
        Assert.Equal("old", File.ReadAllText(_path));
        Assert.Equal("new", File.ReadAllText(_replacement));
    }

    [WindowsFact]
    public void AWriteThatCannotReplaceTheFile_LeavesItAsItWas_AndNothingElseBehind()
    {
        // Through the real write, with the real waiting: about 1.3 s, and no assumption about how long.
        using (HoldOpen())
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => StorageFiles.WriteAtomic(_path, "newer"u8));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().FullName);
        }

        Assert.Equal("old", File.ReadAllText(_path));
        Assert.Equal(
            ["settings.json", "settings.json.replacement"],
            Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [WindowsFact]
    public void AFileThatIsNotThereYet_IsSimplyMovedIntoPlace()
    {
        File.Delete(_path);

        StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, static pause => Thread.Sleep(pause));

        Assert.Equal("new", File.ReadAllText(_path));
        Assert.False(File.Exists(_replacement));
    }
}
