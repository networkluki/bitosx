using System.Text;
using BitosX.Storage;

namespace BitosX.Storage.Tests;

/// <summary>
/// What is specific to Windows in how the files are written: a file another process holds open
/// without delete sharing cannot be replaced, and virus scanners, indexers and backup tools do exactly
/// that to a freshly written file for a moment. These run on Windows; elsewhere they are reported as
/// skipped.
/// </summary>
public sealed class WindowsFilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public WindowsFilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(_path, "old");
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
    }

    /// <summary>Opens the file the way File.ReadAllText and most scanners do: read, sharing read only.</summary>
    private FileStream HoldOpen() => new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [WindowsFact]
    public async Task AFileHeldOpenForAMoment_IsReplacedOnceItIsLetGo()
    {
        Task release;
        using (var held = HoldOpen())
        {
            release = Task.Run(async () =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                held.Dispose();
            });

            StorageFiles.WriteAtomic(_path, Encoding.UTF8.GetBytes("new"));
        }

        await release;
        Assert.Equal("new", File.ReadAllText(_path));
        Assert.Single(Directory.EnumerateFiles(_tempDir));
    }

    [WindowsFact]
    public void AFileHeldOpenLongerThanTheRetries_IsLeftAsItWas_AndTheFailureIsReported()
    {
        using (HoldOpen())
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Exception failure = Assert.ThrowsAny<Exception>(() => StorageFiles.WriteAtomic(_path, Encoding.UTF8.GetBytes("new")));

            Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().FullName);
            Assert.True(started.Elapsed < StorageFiles.WindowsReplaceRetryBudget + TimeSpan.FromSeconds(1), $"gave up after {started.Elapsed}");
        }

        Assert.Equal("old", File.ReadAllText(_path));
        Assert.Single(Directory.EnumerateFiles(_tempDir)); // no temporary file left behind
    }
}
