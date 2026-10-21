namespace BitosX.Core.Storage;

/// <summary>
/// What this device knows about its last sync with the server: the server revision it last agreed
/// with and the fingerprint the local vault file had at that moment. Public metadata about
/// ciphertext, kept next to the vault; it lets the next sync tell "this device changed" from
/// "the server changed" from "both did", which is what fail-closed conflict handling needs.
/// </summary>
public interface ISyncStateStore
{
    /// <summary>The recorded state, or <see langword="null"/> when this device has never synced (or the record is unreadable, which counts as never).</summary>
    Task<SyncState?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a completed sync. Atomic: an interrupted write leaves the previous record.</summary>
    Task SaveAsync(SyncState state, CancellationToken cancellationToken = default);

    /// <summary>Forgets the record, so the next sync starts as a device that never synced.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>One completed sync, as this device recorded it.</summary>
/// <param name="Revision">The server revision this device last uploaded or downloaded.</param>
/// <param name="LocalFingerprint">The SHA-256 (lowercase hex) of the local vault envelope's bytes at that moment.</param>
/// <param name="SyncedAt">When.</param>
public sealed record SyncState(long Revision, string LocalFingerprint, DateTimeOffset SyncedAt)
{
    /// <summary>Whether the record is one this build could have written.</summary>
    public bool IsPlausible
        => Revision >= 1
            && LocalFingerprint is { Length: 64 }
            && LocalFingerprint.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
