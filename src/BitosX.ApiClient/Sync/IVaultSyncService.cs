using BitosX.Core.Services;

namespace BitosX.ApiClient.Sync;

/// <summary>What a sync found, and what — if anything — it did about it.</summary>
public enum SyncOutcome
{
    /// <summary>The server holds what this device holds. Nothing was sent.</summary>
    UpToDate = 0,

    /// <summary>This device's vault had changed and the server had not: the vault was uploaded.</summary>
    Uploaded = 1,

    /// <summary>The server has a newer revision and this device's vault has not changed since it last synced. Nothing was changed; download to take the server's.</summary>
    ServerAhead = 2,

    /// <summary>
    /// Both changed — or this device has never synced and the server already holds a different vault.
    /// Nothing was changed. The user chooses: take the server's vault (download), or replace it with
    /// this device's (overwrite).
    /// </summary>
    Conflict = 3,
}

/// <summary>The result of a sync: the outcome, and what the server holds now (<see langword="null"/> when nothing).</summary>
public sealed record SyncResult(SyncOutcome Outcome, SyncStatus? Server, DateTimeOffset? LastSyncedAt);

/// <summary>A downloaded envelope that has been authenticated with the master password and validated, ready to replace the local vault.</summary>
public sealed record SyncDownload(BackupPreview Preview, SyncStatus Server);

/// <summary>
/// Encrypted sync of the vault with the BitosX server: the vault file, as it is on disk, goes up
/// and comes down as opaque bytes. The server never gets a key, and this service never decrypts
/// anything itself — a downloaded vault is opened on this device with the master password, through
/// the same validated restore path a backup file goes through, and it replaces the local vault only
/// after that, with a recovery copy kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>No merging.</b> Two vaults cannot be merged without decrypting both, and the server cannot
/// decrypt. When this device and the server have both changed since this device last synced, the
/// sync stops and says so; the user chooses which side wins, explicitly, every time.
/// </para>
/// <para>
/// <b>Fail closed.</b> Every operation here either completes or changes nothing. An upload the server
/// refuses because another device got there first changes nothing on either side. A download
/// replaces the local vault only after the master password has opened it and its contents have been
/// validated, and only after the user acknowledged the replacement.
/// </para>
/// <para>All operations require the vault to be unlocked and the account to be signed in.</para>
/// </remarks>
public interface IVaultSyncService
{
    /// <summary>
    /// Compares this device's vault with the server and uploads it when this device alone has
    /// changed. Never downloads and never overwrites a server revision this device has not seen.
    /// </summary>
    Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the server's vault and opens it with <paramref name="masterPassword"/> — the master
    /// password of <i>that</i> vault, which may differ from this device's. Nothing local changes.
    /// Throws when the server holds nothing (<see cref="InvalidOperationException"/>), when the
    /// password is wrong (<c>VaultAuthenticationException</c>), or when the envelope is not a valid
    /// vault.
    /// </summary>
    Task<SyncDownload> PrepareDownloadAsync(string masterPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the local vault with a prepared download: the current vault is kept as a recovery copy,
    /// the file is replaced atomically, the sync record is updated, and the vault locks so it is
    /// re-opened with the downloaded vault's master password.
    /// </summary>
    Task CommitDownloadAsync(SyncDownload download, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the server's vault with this device's, whatever the server holds. For resolving a
    /// <see cref="SyncOutcome.Conflict"/> in this device's favour; every other device then sees a
    /// conflict of its own. Still compare-and-set against the revision just observed, so a device
    /// that uploads between the look and the write is not silently overwritten.
    /// </summary>
    Task<SyncResult> OverwriteServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the vault from the server and forgets this device's sync record. The local vault is untouched.</summary>
    Task DeleteFromServerAsync(CancellationToken cancellationToken = default);
}
