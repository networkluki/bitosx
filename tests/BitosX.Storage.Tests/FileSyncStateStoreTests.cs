using System.Text;
using BitosX.Core.Storage;
using BitosX.Storage;

namespace BitosX.Storage.Tests;

/// <summary>The sync record on disk: round trip, atomic write, and everything that is not exactly ours reads as "never synced".</summary>
public sealed class FileSyncStateStoreTests : IDisposable
{
    private static readonly string Fingerprint = new('a', 64);
    private readonly string _dir;
    private readonly FileSyncStateStore _store;

    public FileSyncStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BitosX.SyncState", Guid.NewGuid().ToString("N"));
        _store = new FileSyncStateStore(Path.Combine(_dir, "sync-state.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoFile_IsNull()
        => Assert.Null(await _store.LoadAsync());

    [Fact]
    public async Task SaveThenLoad_RoundTrips_AndLeavesNoTemporaryFile()
    {
        var state = new SyncState(7, Fingerprint, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

        await _store.SaveAsync(state);

        Assert.Equal(state, await _store.LoadAsync());
        Assert.Single(Directory.GetFiles(_dir));
        string json = await File.ReadAllTextAsync(_store.FilePath);
        Assert.Contains("\"revision\": 7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_ReplacesThePreviousRecord()
    {
        await _store.SaveAsync(new SyncState(1, Fingerprint, DateTimeOffset.UtcNow));
        await _store.SaveAsync(new SyncState(2, new string('b', 64), DateTimeOffset.UtcNow));

        SyncState? loaded = await _store.LoadAsync();
        Assert.Equal(2, loaded!.Revision);
        Assert.Equal(new string('b', 64), loaded.LocalFingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"revision\":1,\"localFingerprint\":\"FP\",\"syncedAt\":\"2026-10-20T12:00:00Z\"}")]   // a version this build does not know
    [InlineData("{\"version\":1,\"revision\":0,\"localFingerprint\":\"FP\",\"syncedAt\":\"2026-10-20T12:00:00Z\"}")]   // revision 0 is never recorded
    [InlineData("{\"version\":1,\"revision\":1,\"localFingerprint\":\"abc\",\"syncedAt\":\"2026-10-20T12:00:00Z\"}")]  // not a SHA-256
    [InlineData("{\"version\":1,\"revision\":1,\"localFingerprint\":\"FP\"}")]                                          // no timestamp
    [InlineData("{\"version\":1,\"revision\":\"1\",\"localFingerprint\":\"FP\",\"syncedAt\":\"2026-10-20T12:00:00Z\"}")] // a number as a string
    public async Task AnythingThatIsNotOurRecord_ReadsAsNeverSynced(string content)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_store.FilePath, content.Replace("FP", Fingerprint, StringComparison.Ordinal), new UTF8Encoding(false));

        Assert.Null(await _store.LoadAsync());
    }

    [Fact]
    public async Task AFileFarTooLargeToBeOurs_ReadsAsNeverSynced()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_store.FilePath, "{\"version\":1,\"padding\":\"" + new string('x', 5000) + "\"}");

        Assert.Null(await _store.LoadAsync());
    }

    [Fact]
    public async Task Clear_RemovesTheRecord_AndIsFineWhenThereIsNone()
    {
        await _store.SaveAsync(new SyncState(1, Fingerprint, DateTimeOffset.UtcNow));

        await _store.ClearAsync();
        await _store.ClearAsync();

        Assert.False(File.Exists(_store.FilePath));
        Assert.Null(await _store.LoadAsync());
    }

    [Fact]
    public async Task AnImplausibleState_IsNotRecorded()
        => await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(new SyncState(0, Fingerprint, DateTimeOffset.UtcNow)));
}
