namespace BitosX.Core.Storage;

/// <summary>
/// What this device knows about its last sync with the server: which server and account, the server
/// revision it last agreed with and that revision's fingerprint, and the fingerprint the local vault
/// file had at that moment. Public metadata about
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
/// <param name="Server">The server it was with: its origin, as <c>https://api.bitosx.se/</c>.</param>
/// <param name="AccountId">The account it was for. A record for another server or account means nothing for this one.</param>
/// <param name="Revision">The server revision this device last uploaded, downloaded or agreed with.</param>
/// <param name="ServerFingerprint">
/// The SHA-256 (lowercase hex) of the envelope the server held at that revision. Compared together with
/// <paramref name="Revision"/>: a server that removed the vault and was given another one may, if it
/// numbers revisions anew, hand out the same number for different bytes.
/// </param>
/// <param name="LocalFingerprint">The SHA-256 (lowercase hex) of the local vault envelope's bytes at that moment.</param>
/// <param name="SyncedAt">When.</param>
public sealed record SyncState(
    string Server,
    Guid AccountId,
    long Revision,
    string ServerFingerprint,
    string LocalFingerprint,
    DateTimeOffset SyncedAt)
{
    /// <summary>The longest server origin a record holds.</summary>
    public const int MaxServerLength = 256;

    /// <summary>Whether the record is one this build could have written.</summary>
    public bool IsPlausible
        => Revision >= 1
            && IsFingerprint(ServerFingerprint)
            && IsFingerprint(LocalFingerprint)
            && AccountId != Guid.Empty
            && !string.IsNullOrWhiteSpace(Server)
            && Server.Length <= MaxServerLength;

    /// <summary>Whether this record is about <paramref name="server"/> and <paramref name="accountId"/>.</summary>
    public bool IsFor(string server, Guid accountId)
        => string.Equals(Server, server, StringComparison.Ordinal) && AccountId == accountId;

    private static bool IsFingerprint(string? value)
        => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
