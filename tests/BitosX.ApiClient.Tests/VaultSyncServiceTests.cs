using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// The sync decision over a scripted account and an in-memory vault: when it uploads, when it stops,
/// what it records, and that nothing is ever changed on either side without the rule allowing it.
/// </summary>
public sealed class VaultSyncServiceTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    private static EncryptedVault Vault(byte marker)
        => new(1, "argon2id", Argon2Parameters.ForTesting, new byte[16], "AES-256-GCM", new byte[12], new byte[48], new byte[12], [marker, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

    private static byte[] Bytes(EncryptedVault vault) => EncryptedVaultSerializer.SerializeToUtf8Bytes(vault);

    private static string Fp(EncryptedVault vault) => HttpBitosXApiClient.Fingerprint(Bytes(vault));

    // The envelope holds byte arrays, so record equality is reference equality; compare the bytes they serialize to.
    private static void AssertVault(EncryptedVault expected, EncryptedVault? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Bytes(expected), Bytes(actual!));
    }

    // ---- sync ----

    [Fact]
    public async Task NothingOnTheServer_UploadsAsRevisionOne_AndRecordsIt()
    {
        _s.Storage.Stored = Vault(1);

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Uploaded, result.Outcome);
        Assert.Equal(1, result.Server!.Revision);
        Assert.Equal(0, _s.Account.LastUpload!.Value.BaseRevision);
        Assert.Equal(Bytes(Vault(1)), _s.Account.LastUpload.Value.Envelope);
        Assert.Equal(_s.Record(1, Fp(Vault(1)), _s.Clock.Now), _s.State.Saved);
        Assert.Equal(_s.Clock.Now, result.LastSyncedAt);
    }

    [Fact]
    public async Task TheSameVaultOnBothSides_IsUpToDate_AndNothingIsSent_EvenWhenNeverSyncedHere()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(1)));

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.Null(_s.Account.LastUpload);
        Assert.Equal(1, _s.State.Saved!.Revision); // this device now knows the revision it agrees with
    }

    [Fact]
    public async Task NeverSyncedHere_AndTheServerHoldsSomethingElse_IsAConflict_AndNothingMoves()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Equal(1, result.Server!.Revision);
        Assert.Null(_s.Account.LastUpload);
        Assert.Null(_s.State.Saved);
        Assert.Equal(Bytes(Vault(2)), _s.Account.StoredEnvelope);
    }

    [Fact]
    public async Task OnlyThisDeviceChanged_Uploads_AgainstTheRevisionItLastSaw()
    {
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Storage.Stored = Vault(2); // edited here since

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Uploaded, result.Outcome);
        Assert.Equal(1, _s.Account.LastUpload!.Value.BaseRevision);
        Assert.Equal(2, result.Server!.Revision);
        Assert.Equal(_s.Record(2, Fp(Vault(2)), _s.Clock.Now), _s.State.Saved);
    }

    [Fact]
    public async Task OnlyTheServerChanged_IsServerAhead_AndNothingMoves()
    {
        _s.Storage.Stored = Vault(1);
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Account.Store(Bytes(Vault(1)));
        _s.Account.Store(Bytes(Vault(2))); // another device uploaded revision 2

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.ServerAhead, result.Outcome);
        Assert.Equal(2, result.Server!.Revision);
        Assert.Null(_s.Account.LastUpload);
        Assert.Equal(1, _s.State.Saved!.Revision);
        AssertVault(Vault(1), _s.Storage.Stored);
    }

    [Fact]
    public async Task BothChanged_IsAConflict_AndNothingMoves()
    {
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Storage.Stored = Vault(2);
        _s.Account.Store(Bytes(Vault(1)));
        _s.Account.Store(Bytes(Vault(3)));

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Null(_s.Account.LastUpload);
        Assert.Equal(Bytes(Vault(3)), _s.Account.StoredEnvelope);
        Assert.Equal(1, _s.State.Saved!.Revision);
    }

    [Fact]
    public async Task AnUploadAnotherDeviceBeatsToIt_IsAConflict_AndTheRecordIsUnchanged()
    {
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Storage.Stored = Vault(2);
        _s.Account.RaceBeforeUpload = () => _s.Account.Store(Bytes(Vault(3)));

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.Server!.Revision);
        Assert.Equal(Bytes(Vault(3)), _s.Account.StoredEnvelope);
        Assert.Equal(1, _s.State.Saved!.Revision);
    }

    // ---- the record is about one server, one account, and one revision with its bytes ----

    [Fact]
    public async Task ARecordOfAnotherAccount_CountsAsNeverSynced_SoNothingIsOverwritten()
    {
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1)) with { AccountId = Guid.NewGuid() };
        _s.Storage.Stored = Vault(2); // a different vault than the server's

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Null(_s.Account.LastUpload);
        Assert.Equal(Bytes(Vault(1)), _s.Account.StoredEnvelope);
    }

    [Fact]
    public async Task ARecordOfAnotherServer_CountsAsNeverSynced()
    {
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1)) with { Server = "https://api.other.example/" };
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));
        _s.Account.Store(Bytes(Vault(1))); // revision 3, the same bytes as this device: up to date, and recorded for this server

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.Equal(FakeServer.Address.BaseAddress.ToString(), _s.State.Saved!.Server);
        Assert.Equal(3, _s.State.Saved.Revision);
    }

    [Fact]
    public async Task TheSameRevisionWithOtherBytes_IsNotUpToDate_OnAServerThatNumbersAnewAfterADelete()
    {
        // Two devices synced at revision 1. The other one removes the vault from the server, edits, and
        // syncs: a server that numbers anew stores its vault as revision 1 again.
        _s.Account.RestartsRevisionsAfterDelete = true;
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Account.DeleteFromOutside();
        _s.Account.Store(Bytes(Vault(2)));
        Assert.Equal(1, _s.Account.Revision);

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.ServerAhead, result.Outcome); // not "up to date"
        Assert.Null(_s.Account.LastUpload);
    }

    [Fact]
    public async Task TheSameRevisionWithOtherBytes_IsNeverSilentlyOverwritten()
    {
        _s.Account.RestartsRevisionsAfterDelete = true;
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Account.DeleteFromOutside();
        _s.Account.Store(Bytes(Vault(3))); // another device's vault, as revision 1 again
        _s.Storage.Stored = Vault(2); // and this device edited too

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Null(_s.Account.LastUpload);
        Assert.Equal(Bytes(Vault(3)), _s.Account.StoredEnvelope);
    }

    [Fact]
    public async Task AfterTheServerRemovedTheVault_RevisionsGoOn_AndThisDeviceUploadsAgainstNothing()
    {
        // The real server (0.11.0+): revisions never restart. After a delete, the next upload is the
        // previous revision + 1, uploaded against "nothing stored" (0).
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(1)));
        _s.Account.Store(Bytes(Vault(2)));
        _s.State.Saved = _s.Record(2, Fp(Vault(2)), _s.Clock.Now.AddHours(-1));
        _s.Account.DeleteFromOutside();

        SyncResult result = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Uploaded, result.Outcome);
        Assert.Equal(0, _s.Account.LastUpload!.Value.BaseRevision);
        Assert.Equal(3, result.Server!.Revision);
        Assert.Equal(3, _s.State.Saved!.Revision);
    }

    [Fact]
    public async Task DeleteThenSyncFromTheSameDevice_UploadsAsTheNextRevision()
    {
        _s.Storage.Stored = Vault(1);
        Assert.Equal(SyncOutcome.Uploaded, (await _s.Service.SyncAsync()).Outcome);
        await _s.Service.DeleteFromServerAsync();

        SyncResult again = await _s.Service.SyncAsync();

        Assert.Equal(SyncOutcome.Uploaded, again.Outcome);
        Assert.Equal(2, again.Server!.Revision);
        Assert.Equal(0, _s.Account.LastUpload!.Value.BaseRevision);
    }

    [Fact]
    public async Task ADownloadWhoseBytesAreWrittenDifferentlyHere_IsStillUpToDateAfterwards()
    {
        // The server's bytes are another client's serialization; this device writes its own. The record
        // keeps both fingerprints, so the next sync knows nothing changed on either side.
        byte[] serverBytes = Bytes(Vault(2));
        serverBytes = [.. System.Text.Encoding.UTF8.GetBytes(" "), .. serverBytes]; // same vault, other bytes
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(serverBytes);
        SyncDownload download = await _s.Service.PrepareDownloadAsync("pw");
        await _s.Service.CommitDownloadAsync(download);
        Assert.NotEqual(_s.State.Saved!.ServerFingerprint, _s.State.Saved.LocalFingerprint);

        _s.Vault.State = VaultState.Unlocked;
        Assert.Equal(SyncOutcome.UpToDate, (await _s.Service.SyncAsync()).Outcome);
        Assert.Null(_s.Account.LastUpload);
    }

    // ---- download ----

    [Fact]
    public async Task PrepareDownload_OpensTheServersVault_WithThePasswordGiven_AndChangesNothing()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));

        SyncDownload download = await _s.Service.PrepareDownloadAsync("the other master password");

        AssertVault(Vault(2), download.Preview.Envelope);
        Assert.Equal("server revision 1", download.Preview.FileName);
        Assert.Equal(Bytes(Vault(2)).Length, download.Preview.FileSizeBytes);
        Assert.Equal("the other master password", _s.Backup.LastPassword);
        AssertVault(Vault(1), _s.Storage.Stored);
        Assert.Null(_s.State.Saved);
    }

    [Fact]
    public async Task PrepareDownload_WhenTheServerHoldsNothing_Says()
    {
        _s.Storage.Stored = Vault(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.PrepareDownloadAsync("pw"));
    }

    [Fact]
    public async Task PrepareDownload_OfBytesThatAreNotAVault_FailsClosed_BeforeThePasswordIsUsed()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store("{\"version\":1,\"not\":\"a vault\"}"u8.ToArray());

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => _s.Service.PrepareDownloadAsync("pw"));
        Assert.Null(_s.Backup.LastPassword);
    }

    [Fact]
    public async Task PrepareDownload_WithTheWrongPassword_FailsClosed()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));
        _s.Backup.PrepareThrows = new VaultAuthenticationException();

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => _s.Service.PrepareDownloadAsync("pw"));
        AssertVault(Vault(1), _s.Storage.Stored);
    }

    [Fact]
    public async Task CommitDownload_ReplacesTheVault_ThenRecordsTheServersRevision()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));
        SyncDownload download = await _s.Service.PrepareDownloadAsync("pw");

        await _s.Service.CommitDownloadAsync(download);

        AssertVault(Vault(2), _s.Storage.Stored);
        Assert.Equal(1, _s.Backup.CommitCount);
        Assert.Equal(_s.Record(1, Fp(Vault(2)), _s.Clock.Now), _s.State.Saved);

        // ...so that the next sync, after unlocking, finds this device up to date.
        _s.Vault.State = VaultState.Unlocked;
        _s.Account.SignedIn = true;
        Assert.Equal(SyncOutcome.UpToDate, (await _s.Service.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task CommitDownload_ThatFailsToWrite_RecordsNothing()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(2)));
        SyncDownload download = await _s.Service.PrepareDownloadAsync("pw");
        _s.Backup.CommitThrows = new IOException("disk");

        await Assert.ThrowsAsync<IOException>(() => _s.Service.CommitDownloadAsync(download));

        AssertVault(Vault(1), _s.Storage.Stored);
        Assert.Null(_s.State.Saved);
    }

    // ---- the explicit ways out of a conflict ----

    [Fact]
    public async Task OverwriteServer_UploadsThisDevicesVault_OverWhateverTheServerHolds()
    {
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now.AddHours(-1));
        _s.Storage.Stored = Vault(2);
        _s.Account.Store(Bytes(Vault(1)));
        _s.Account.Store(Bytes(Vault(3)));
        Assert.Equal(SyncOutcome.Conflict, (await _s.Service.SyncAsync()).Outcome);

        SyncResult result = await _s.Service.OverwriteServerAsync();

        Assert.Equal(SyncOutcome.Uploaded, result.Outcome);
        Assert.Equal(2, _s.Account.LastUpload!.Value.BaseRevision); // still compare-and-set against what was just observed
        Assert.Equal(Bytes(Vault(2)), _s.Account.StoredEnvelope);
        Assert.Equal(3, _s.State.Saved!.Revision);
    }

    [Fact]
    public async Task OverwriteServer_StillLosesToADeviceThatUploadsMeanwhile()
    {
        _s.Storage.Stored = Vault(2);
        _s.Account.Store(Bytes(Vault(1)));
        _s.Account.RaceBeforeUpload = () => _s.Account.Store(Bytes(Vault(3)));

        SyncResult result = await _s.Service.OverwriteServerAsync();

        Assert.Equal(SyncOutcome.Conflict, result.Outcome);
        Assert.Equal(Bytes(Vault(3)), _s.Account.StoredEnvelope);
    }

    [Fact]
    public async Task DeleteFromServer_RemovesTheEnvelope_AndForgetsTheRecord_AndLeavesTheVault()
    {
        _s.Storage.Stored = Vault(1);
        _s.Account.Store(Bytes(Vault(1)));
        _s.State.Saved = _s.Record(1, Fp(Vault(1)), _s.Clock.Now);

        await _s.Service.DeleteFromServerAsync();

        Assert.Null(_s.Account.StoredEnvelope);
        Assert.Null(_s.State.Saved);
        AssertVault(Vault(1), _s.Storage.Stored);
    }

    // ---- preconditions ----

    [Fact]
    public async Task ALockedVault_OrASignedOutAccount_StopsEverything_BeforeAnyCall()
    {
        _s.Storage.Stored = Vault(1);
        _s.Vault.State = VaultState.Locked;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.SyncAsync());

        _s.Vault.State = VaultState.Unlocked;
        _s.Account.SignedIn = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.SyncAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.PrepareDownloadAsync("pw"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.OverwriteServerAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.DeleteFromServerAsync());

        Assert.Equal(0, _s.Account.Calls);
    }

    [Fact]
    public async Task TwoOperationsAtOnce_TheSecondIsRefused()
    {
        _s.Storage.Stored = Vault(1);
        var gate = new TaskCompletionSource();
        _s.Account.BeforeStatus = () => gate.Task;

        Task<SyncResult> first = _s.Service.SyncAsync();
        await Task.Yield();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.Service.SyncAsync());

        gate.SetResult();
        Assert.Equal(SyncOutcome.Uploaded, (await first).Outcome);
    }

    // ---- fakes ----

    private sealed class Setup : IDisposable
    {
        public Clock Clock { get; } = new();
        public ScriptedAccount Account { get; } = new();
        public FakeVault Vault { get; } = new();
        public MemoryStorage Storage { get; } = new();
        public FakeBackup Backup { get; }
        public MemoryState State { get; } = new();
        public VaultSyncService Service { get; }

        public Setup()
        {
            Backup = new FakeBackup(Storage, Vault);
            Service = new VaultSyncService(Account, Vault, Storage, Backup, State, Clock, NullLogger<VaultSyncService>.Instance);
        }

        public void Dispose() => Service.Dispose();

        /// <summary>A record of a sync with this server and account, at which the server held what this device held.</summary>
        public SyncState Record(long revision, string fingerprint, DateTimeOffset at)
            => new(FakeServer.Address.BaseAddress.ToString(), Account.AccountId, revision, fingerprint, fingerprint, at);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The account, with the server behind it emulated: one envelope, compare-and-set on its revision.</summary>
    private sealed class ScriptedAccount : IAccountSession
    {
        public bool SignedIn { get; set; } = true;
        public byte[]? StoredEnvelope { get; private set; }
        public long Revision { get; private set; }
        public (long BaseRevision, byte[] Envelope)? LastUpload { get; private set; }
        public int Calls { get; private set; }
        public Action? RaceBeforeUpload { get; set; }
        public Func<Task>? BeforeStatus { get; set; }

        /// <summary>The account the session is signed in to.</summary>
        public Guid AccountId { get; set; } = Guid.Parse("11111111-2222-4333-8444-555555555555");

        /// <summary>
        /// When set, the server numbers revisions anew after a delete, as servers before 0.11.0 did. The
        /// real server never does that any more (revisions only grow); the client must not depend on it.
        /// </summary>
        public bool RestartsRevisionsAfterDelete { get; set; }

        /// <summary>Puts an envelope on the server, as an upload by this or any other device does: the next revision.</summary>
        public void Store(byte[] envelope)
        {
            StoredEnvelope = envelope;
            Revision++;
        }

        private SyncStatus? Status() => StoredEnvelope is null ? null : new(Revision, HttpBitosXApiClient.Fingerprint(StoredEnvelope), StoredEnvelope.Length, DateTimeOffset.UtcNow);

        public bool IsAvailable => true;
        public string? ServerName => "api.example.test";
        public ApiServer? Server { get; set; } = FakeServer.Address;
        public bool IsSignedIn => SignedIn;
        public event EventHandler? Changed { add { } remove { } }

        public async Task<SyncStatus?> GetSyncStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (BeforeStatus is not null)
            {
                await BeforeStatus().ConfigureAwait(false);
            }

            return Status();
        }

        public Task<SyncEnvelope?> DownloadEnvelopeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Status() is { } s ? new SyncEnvelope(s, StoredEnvelope!) : null);
        }

        public Task<SyncStatus> UploadEnvelopeAsync(long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
        {
            Calls++;
            RaceBeforeUpload?.Invoke();
            RaceBeforeUpload = null;
            LastUpload = (baseRevision, envelope.ToArray());

            // As the server: 0 means "nothing stored" and is accepted only while nothing is; otherwise the
            // base must be the stored revision.
            long expected = StoredEnvelope is null ? 0 : Revision;
            if (baseRevision != expected)
            {
                throw new ApiException(ApiFailure.Conflict, 409);
            }

            Store(envelope.ToArray());
            return Task.FromResult(Status()!);
        }

        public Task DeleteEnvelopeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            StoredEnvelope = null;
            if (RestartsRevisionsAfterDelete)
            {
                Revision = 0;
            }

            return Task.CompletedTask;
        }

        /// <summary>Removes the vault from the server, as another device would.</summary>
        public void DeleteFromOutside()
        {
            StoredEnvelope = null;
            if (RestartsRevisionsAfterDelete)
            {
                Revision = 0;
            }
        }

        public bool IsAwaitingSecondFactor => false;
        public SecondFactorMethods SecondFactorMethods => SecondFactorMethods.None;
        public Task<SignInOutcome> SignInAsync(string email, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteSignInAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void CancelSignIn() => throw new NotSupportedException();
        public Task<AccountProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid> GetAccountIdAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(AccountId);
        }

        public Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutEverywhereAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EndSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorStatus?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PasskeyList?> GetPasskeysAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAccountAsync(string password, string? code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeVault : IVaultService
    {
        public VaultState State { get; set; } = VaultState.Unlocked;
        public event EventHandler<VaultState>? StateChanged;
        public IReadOnlyList<VaultItem> Items => [];
        public Argon2Parameters? KdfParameters => null;
        public bool UsesLegacyPasswordEncoding => false;

        public void Lock()
        {
            State = VaultState.Locked;
            StateChanged?.Invoke(this, State);
        }

        public Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StrengthenKeyDerivationAsync(string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoginItem>> AddLoginsAsync(Func<IReadOnlyList<VaultItem>, IReadOnlyList<LoginItem>> chooseItems, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<VaultItem>> VerifyMasterPasswordAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemoryStorage : ILocalVaultStorage
    {
        public EncryptedVault? Stored { get; set; }
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored is not null);
        public Task<EncryptedVault> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored ?? throw new FileNotFoundException("No vault."));

        public Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default)
        {
            Stored = vault;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Stored = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>The restore path, with the real one's observable effects: the vault replaced on commit, then locked.</summary>
    private sealed class FakeBackup(MemoryStorage storage, FakeVault vault) : IVaultBackupService
    {
        public string? LastPassword { get; private set; }
        public Exception? PrepareThrows { get; set; }
        public Exception? CommitThrows { get; set; }
        public int CommitCount { get; private set; }

        public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupPreview> PrepareRestoreAsync(string backupPath, string backupPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupPreview> PrepareRestoreAsync(EncryptedVault envelope, string backupPassword, string sourceName, long sizeBytes, CancellationToken cancellationToken = default)
        {
            LastPassword = backupPassword;
            if (PrepareThrows is not null)
            {
                throw PrepareThrows;
            }

            return Task.FromResult(new BackupPreview(envelope, envelope.Version, 2, sourceName, sizeBytes));
        }

        public async Task CommitRestoreAsync(BackupPreview preview, CancellationToken cancellationToken = default)
        {
            if (CommitThrows is not null)
            {
                throw CommitThrows;
            }

            CommitCount++;
            await storage.WriteAsync(preview.Envelope, cancellationToken).ConfigureAwait(false);
            vault.Lock();
        }
    }

    private sealed class MemoryState : ISyncStateStore
    {
        public SyncState? Saved { get; set; }
        public Task<SyncState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);

        public Task SaveAsync(SyncState state, CancellationToken cancellationToken = default)
        {
            Saved = state;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Saved = null;
            return Task.CompletedTask;
        }
    }
}
