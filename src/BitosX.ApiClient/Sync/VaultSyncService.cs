using BitosX.Core.Services;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.ApiClient.Sync;

/// <summary>The sync of <see cref="IVaultSyncService"/>, over the account session, the vault file and the sync record.</summary>
public sealed class VaultSyncService : IVaultSyncService, IDisposable
{
    private readonly IAccountSession _account;
    private readonly IVaultService _vault;
    private readonly ILocalVaultStorage _storage;
    private readonly IVaultBackupService _backup;
    private readonly ISyncStateStore _state;
    private readonly TimeProvider _clock;
    private readonly ILogger<VaultSyncService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public VaultSyncService(
        IAccountSession account,
        IVaultService vault,
        ILocalVaultStorage storage,
        IVaultBackupService backup,
        ISyncStateStore state,
        TimeProvider clock,
        ILogger<VaultSyncService> logger)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
        => OneAtATimeAsync(async ct =>
        {
            Require();
            (byte[] local, string localFingerprint) = await ReadLocalAsync(ct).ConfigureAwait(false);
            Binding binding = await BindAsync(ct).ConfigureAwait(false);
            SyncState? recorded = await LoadRecordAsync(binding, ct).ConfigureAwait(false);
            SyncStatus? server = await _account.GetSyncStatusAsync(ct).ConfigureAwait(false);

            if (server is null)
            {
                // Nothing on the server (yet, or any more): this device's vault is uploaded against
                // "nothing stored", which the server accepts only while that is still true.
                return await UploadAsync(binding, local, localFingerprint, baseRevision: 0, ct).ConfigureAwait(false);
            }

            bool sameAsServer = string.Equals(server.Fingerprint, localFingerprint, StringComparison.Ordinal);
            bool localUnchanged = recorded is not null && string.Equals(recorded.LocalFingerprint, localFingerprint, StringComparison.Ordinal);

            // The server is what this device last saw only if both the revision and the bytes match: a
            // revision number alone could have been handed out again after the vault was removed.
            bool serverUnchanged = recorded is not null
                && recorded.Revision == server.Revision
                && string.Equals(recorded.ServerFingerprint, server.Fingerprint, StringComparison.Ordinal);

            if (sameAsServer || (localUnchanged && serverUnchanged))
            {
                SyncState state = await RecordAsync(binding, server, localFingerprint, ct).ConfigureAwait(false);
                ApiLog.SyncChecked(_logger, nameof(SyncOutcome.UpToDate), server.Revision);
                return new SyncResult(SyncOutcome.UpToDate, server, state.SyncedAt);
            }

            if (recorded is null)
            {
                // Never synced here, and the server holds something else: no way to tell which is newer.
                ApiLog.SyncConflict(_logger, server.Revision, 0);
                return new SyncResult(SyncOutcome.Conflict, server, null);
            }

            if (serverUnchanged)
            {
                // Only this device moved on: the server takes it, provided nobody got there meanwhile.
                return await UploadAsync(binding, local, localFingerprint, server.Revision, ct).ConfigureAwait(false);
            }

            if (localUnchanged)
            {
                ApiLog.SyncChecked(_logger, nameof(SyncOutcome.ServerAhead), server.Revision);
                return new SyncResult(SyncOutcome.ServerAhead, server, recorded.SyncedAt);
            }

            ApiLog.SyncConflict(_logger, server.Revision, recorded.Revision);
            return new SyncResult(SyncOutcome.Conflict, server, recorded.SyncedAt);
        }, cancellationToken);

    public Task<SyncDownload> PrepareDownloadAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        return OneAtATimeAsync(async ct =>
        {
            Require();
            SyncEnvelope? download = await _account.DownloadEnvelopeAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The server holds no vault for this account.");

            // The bytes are what a device uploaded; they are trusted no further than a backup file is.
            // Deserialize fails closed on anything that is not a well-formed envelope.
            EncryptedVault envelope = EncryptedVaultSerializer.Deserialize(download.Envelope);
            BackupPreview preview = await _backup
                .PrepareRestoreAsync(envelope, masterPassword, $"server revision {download.Status.Revision}", download.Envelope.Length, ct)
                .ConfigureAwait(false);

            ApiLog.SyncDownloaded(_logger, download.Status.Revision, download.Envelope.Length);
            return new SyncDownload(preview, download.Status);
        }, cancellationToken);
    }

    public Task CommitDownloadAsync(SyncDownload download, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        return OneAtATimeAsync<object?>(async ct =>
        {
            Require();
            Binding binding = await BindAsync(ct).ConfigureAwait(false); // before the replace: nothing on the network between the two writes

            // Replace first, record second. A crash between the two leaves a vault on disk whose
            // fingerprint matches the server's, which the next sync recognises as up to date; the other
            // order could leave a record claiming the new vault while the old one is still on disk, and
            // the next sync would then offer to upload the old vault over the new one.
            await _backup.CommitRestoreAsync(download.Preview, ct).ConfigureAwait(false); // recovery copy, atomic replace, lock
            string fingerprint = HttpBitosXApiClient.Fingerprint(EncryptedVaultSerializer.SerializeToUtf8Bytes(download.Preview.Envelope));
            await RecordAsync(binding, download.Server, fingerprint, ct).ConfigureAwait(false);
            ApiLog.SyncRestored(_logger, download.Server.Revision);
            return null;
        }, cancellationToken);
    }

    public Task<SyncResult> OverwriteServerAsync(CancellationToken cancellationToken = default)
        => OneAtATimeAsync(async ct =>
        {
            Require();
            (byte[] local, string localFingerprint) = await ReadLocalAsync(ct).ConfigureAwait(false);
            Binding binding = await BindAsync(ct).ConfigureAwait(false);
            SyncStatus? server = await _account.GetSyncStatusAsync(ct).ConfigureAwait(false);
            return await UploadAsync(binding, local, localFingerprint, server?.Revision ?? 0, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task DeleteFromServerAsync(CancellationToken cancellationToken = default)
        => OneAtATimeAsync<object?>(async ct =>
        {
            Require();
            await _account.DeleteEnvelopeAsync(ct).ConfigureAwait(false);
            await _state.ClearAsync(ct).ConfigureAwait(false);
            ApiLog.SyncDeleted(_logger);
            return null;
        }, cancellationToken);

    /// <summary>Uploads against <paramref name="baseRevision"/>; a refusal is reported as a conflict and changes nothing.</summary>
    private async Task<SyncResult> UploadAsync(Binding binding, byte[] local, string localFingerprint, long baseRevision, CancellationToken cancellationToken)
    {
        SyncStatus stored;
        try
        {
            stored = await _account.UploadEnvelopeAsync(baseRevision, local, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.Conflict)
        {
            SyncStatus? now = await _account.GetSyncStatusAsync(cancellationToken).ConfigureAwait(false);
            ApiLog.SyncConflict(_logger, now?.Revision ?? 0, baseRevision);
            return new SyncResult(SyncOutcome.Conflict, now, null);
        }

        SyncState state = await RecordAsync(binding, stored, localFingerprint, cancellationToken).ConfigureAwait(false);
        ApiLog.SyncUploaded(_logger, stored.Revision, stored.SizeBytes);
        return new SyncResult(SyncOutcome.Uploaded, stored, state.SyncedAt);
    }

    /// <summary>The local vault as the bytes that would be uploaded, with their fingerprint.</summary>
    private async Task<(byte[] Bytes, string Fingerprint)> ReadLocalAsync(CancellationToken cancellationToken)
    {
        EncryptedVault envelope = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false); // fails closed on a malformed file
        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(envelope);
        if (bytes.Length > HttpBitosXApiClient.MaxEnvelopeBytes)
        {
            throw new InvalidOperationException("The vault is larger than the server accepts.");
        }

        return (bytes, HttpBitosXApiClient.Fingerprint(bytes));
    }

    private async Task<SyncState> RecordAsync(Binding binding, SyncStatus server, string localFingerprint, CancellationToken cancellationToken)
    {
        var state = new SyncState(binding.Server, binding.AccountId, server.Revision, server.Fingerprint, localFingerprint, _clock.GetUtcNow());
        await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>Which server and account this sync is with: a record is only ever read for, and written for, that pair.</summary>
    private readonly record struct Binding(string Server, Guid AccountId);

    private async Task<Binding> BindAsync(CancellationToken cancellationToken)
    {
        string server = _account.Server?.BaseAddress.ToString()
            ?? throw new InvalidOperationException("Server integration is disabled in this run of BitosX.");
        Guid accountId = await _account.GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        return new Binding(server, accountId);
    }

    /// <summary>The record, if it is about this server and account; a record about another one says nothing here (and is replaced by the next sync that completes).</summary>
    private async Task<SyncState?> LoadRecordAsync(Binding binding, CancellationToken cancellationToken)
    {
        SyncState? recorded = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        return recorded is not null && recorded.IsFor(binding.Server, binding.AccountId) ? recorded : null;
    }

    private void Require()
    {
        if (_vault.State != VaultState.Unlocked)
        {
            throw new InvalidOperationException("The vault must be unlocked to sync.");
        }

        if (!_account.IsSignedIn)
        {
            throw new InvalidOperationException("Sign in to your BitosX account to sync.");
        }
    }

    public void Dispose() => _oneAtATime.Dispose();

    private async Task<T> OneAtATimeAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!await _oneAtATime.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A sync is already in progress.");
        }

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
