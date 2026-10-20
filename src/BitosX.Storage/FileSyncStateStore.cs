using System.Text.Json;
using System.Text.Json.Serialization;
using BitosX.Core.Storage;

namespace BitosX.Storage;

/// <summary>
/// The sync record as a small JSON file next to the vault. Written atomically like the vault
/// itself; read with the same fail-closed stance — anything that is not exactly a record this
/// build writes counts as "never synced", which only makes the next sync more careful.
/// </summary>
public sealed class FileSyncStateStore : ISyncStateStore
{
    /// <summary>The file is a few hundred bytes; anything much larger is not ours.</summary>
    public const long MaxFileSizeBytes = 4 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 4,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly string _directory;

    public FileSyncStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The sync state path must include a directory.", nameof(path));
    }

    /// <summary>The resolved absolute path of the record.</summary>
    public string FilePath => _path;

    public async Task<SyncState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await StorageFiles.ReadBoundedAsync(_path, MaxFileSizeBytes, "sync state", cancellationToken).ConfigureAwait(false);
        }
        catch (Crypto.UnsupportedVaultFormatException)
        {
            return null; // too large to be ours
        }

        Record? record;
        try
        {
            record = JsonSerializer.Deserialize<Record>(StorageFiles.WithoutByteOrderMark(bytes), Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || record.Version != 1 || record.Revision is not { } revision || record.LocalFingerprint is null || record.SyncedAt is not { } synced)
        {
            return null;
        }

        var state = new SyncState(revision, record.LocalFingerprint, synced);
        return state.IsPlausible ? state : null;
    }

    public async Task SaveAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsPlausible)
        {
            throw new ArgumentException("The sync state is not one that can be recorded.", nameof(state));
        }

        var record = new Record { Version = 1, Revision = state.Revision, LocalFingerprint = state.LocalFingerprint, SyncedAt = state.SyncedAt };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        StorageFiles.CreatePrivateDirectory(_directory);
        await StorageFiles.WriteAtomicAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path); // an error here is reported, not swallowed: a stale record would mislead the next sync
        }

        return Task.CompletedTask;
    }

    private sealed class Record
    {
        public int? Version { get; init; }

        public long? Revision { get; init; }

        public string? LocalFingerprint { get; init; }

        public DateTimeOffset? SyncedAt { get; init; }
    }
}
