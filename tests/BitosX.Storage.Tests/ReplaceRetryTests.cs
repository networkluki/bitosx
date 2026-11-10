using BitosX.Storage;

namespace BitosX.Storage.Tests;

/// <summary>
/// The schedule on which replacing a file is tried again, on every platform: the waiting is handed in,
/// so the schedule is checked without the clock. (Only Windows uses it for real - see
/// <see cref="WindowsFilesTests"/> - but the arithmetic is the same everywhere, and a target that is a
/// directory refuses to be replaced on all of them.)
/// </summary>
public sealed class ReplaceRetryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly string _replacement;

    public ReplaceRetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
        _replacement = _path + ".replacement";
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

    private static double[] Milliseconds(List<TimeSpan> pauses) => [.. pauses.Select(pause => pause.TotalMilliseconds)];

    [Fact]
    public void ATargetThatRefusesToBeReplaced_IsTriedAgainOnAGrowingPause_UntilTheBudgetIsSpent()
    {
        Directory.CreateDirectory(_path); // a file cannot be renamed over a directory, anywhere
        var pauses = new List<TimeSpan>();

        Exception failure = Assert.ThrowsAny<Exception>(
            () => StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pauses.Add));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().FullName);

        // 10 ms, doubling up to 200 ms, and no further pause once the sum would pass the budget.
        Assert.Equal([10d, 20d, 40d, 80d, 160d, 200d, 200d, 200d, 200d, 200d], Milliseconds(pauses));
        TimeSpan waited = pauses.Aggregate(TimeSpan.Zero, (sum, pause) => sum + pause);
        Assert.True(waited <= StorageFiles.WindowsReplaceRetryBudget, $"waited {waited}");
        Assert.True(waited + pauses[^1] > StorageFiles.WindowsReplaceRetryBudget, "one more pause would have fitted");

        // Nothing was changed by any of the attempts.
        Assert.True(Directory.Exists(_path));
        Assert.Equal("new", File.ReadAllText(_replacement));
    }

    [Fact]
    public void ATargetThatGivesWay_IsReplacedOnceItDoes()
    {
        Directory.CreateDirectory(_path);
        var pauses = new List<TimeSpan>();

        StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pause =>
        {
            pauses.Add(pause);
            if (pauses.Count < 2)
            {
                return;
            }

            // What was in the way is gone. From here on wait for real: on Windows a scanner may be
            // looking at the file at this very moment, which is what the waiting is for.
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path);
            }

            Thread.Sleep(pause);
        });

        Assert.True(pauses.Count >= 2, $"{pauses.Count} pauses");
        Assert.Equal([10d, 20d], Milliseconds(pauses)[..2]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(2, pauses.Count); // nothing else can be in the way: the very next attempt goes through
        }

        Assert.Equal("new", File.ReadAllText(_path));
        Assert.False(File.Exists(_replacement));
    }

    [Fact]
    public void AnExistingFile_IsReplaced_WithoutWaitingWhereNothingElseOpensFiles()
    {
        File.WriteAllText(_path, "old");
        var pauses = new List<TimeSpan>();

        StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pause =>
        {
            pauses.Add(pause);
            Thread.Sleep(pause);
        });

        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(pauses); // on Windows a scanner may have met the first attempt
        }

        Assert.Equal("new", File.ReadAllText(_path));
        Assert.False(File.Exists(_replacement));
    }

    [Fact]
    public void AReplacementThatIsNotThere_IsNotWaitedFor()
    {
        File.WriteAllText(_path, "old");
        File.Delete(_replacement);
        var pauses = new List<TimeSpan>();

        Assert.ThrowsAny<IOException>(() => StorageFiles.ReplaceRetryingWhileHeld(_replacement, _path, pauses.Add));

        Assert.Empty(pauses);
        Assert.Equal("old", File.ReadAllText(_path));
    }
}
