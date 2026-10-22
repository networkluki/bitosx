using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Clipboard;
using BitosX.Platform.Files;

namespace BitosX.App.Tests;

/// <summary>Fake vault service: reports Unlocked; CRUD is unused by the restore-boundary tests.</summary>
internal sealed class FakeVaultService : IVaultService
{
    public VaultState State { get; set; } = VaultState.Unlocked;
    public int LockCount { get; private set; }

    public event EventHandler<VaultState>? StateChanged;

    public Argon2Parameters? KdfParameters { get; set; }
    public bool UsesLegacyPasswordEncoding { get; set; }
    public int StrengthenCount { get; private set; }

    public Task StrengthenKeyDerivationAsync(
        string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default)
    {
        StrengthenCount++;
        if (OperationThrow is not null)
        {
            return Task.FromException(OperationThrow);
        }

        KdfParameters = newParameters;
        UsesLegacyPasswordEncoding = false;
        return Task.CompletedTask;
    }

    public List<VaultItem> Stored { get; } = [];
    public IReadOnlyList<VaultItem> Items => Stored;

    /// <summary>When set, every item operation and the master password change throw this.</summary>
    public Exception? OperationThrow { get; set; }

    public Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

    /// <summary>When set, locking throws this.</summary>
    public Exception? LockThrows { get; set; }

    public void Lock()
    {
        if (LockThrows is not null)
        {
            throw LockThrows;
        }

        LockCount++;
        State = VaultState.Locked;
        StateChanged?.Invoke(this, State);
    }

    public Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public int ChangePasswordCount { get; private set; }
    public Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        ChangePasswordCount++;
        return OperationThrow is not null ? Task.FromException(OperationThrow) : Task.CompletedTask;
    }
    public int RotateCount { get; private set; }
    public Exception? RotateThrow { get; set; }
    public Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        RotateCount++;
        if (RotateThrow is not null)
        {
            return Task.FromException(RotateThrow);
        }

        return Task.CompletedTask;
    }
    public Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (OperationThrow is not null)
        {
            return Task.FromException(OperationThrow);
        }

        Stored.RemoveAll(i => i.Id == id);
        return Task.CompletedTask;
    }
    public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (OperationThrow is not null)
        {
            return Task.FromException<VaultItem>(OperationThrow);
        }

        int index = Stored.FindIndex(i => i.Id == id);
        Stored[index] = Stored[index] with { Favorite = !Stored[index].Favorite };
        return Task.FromResult(Stored[index]);
    }
}

/// <summary>Records calls to the destructive commit and validation, and can simulate failures.</summary>
internal sealed class FakeVaultBackupService : IVaultBackupService
{
    public int ExportCount { get; private set; }
    public int PrepareCount { get; private set; }
    public int CommitCount { get; private set; }
    public Exception? PrepareThrow { get; set; }
    public Exception? CommitThrow { get; set; }

    public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ExportCount++;
        return Task.CompletedTask;
    }

    public Task<BackupPreview> PrepareRestoreAsync(string backupPath, string backupPassword, CancellationToken cancellationToken = default)
    {
        PrepareCount++;
        if (PrepareThrow is not null)
        {
            throw PrepareThrow;
        }

        return Task.FromResult(SamplePreview());
    }

    public Task<BackupPreview> PrepareRestoreAsync(EncryptedVault envelope, string backupPassword, string sourceName, long sizeBytes, CancellationToken cancellationToken = default)
    {
        PrepareCount++;
        if (PrepareThrow is not null)
        {
            throw PrepareThrow;
        }

        return Task.FromResult(new BackupPreview(envelope, envelope.Version, 3, sourceName, sizeBytes));
    }

    public Task CommitRestoreAsync(BackupPreview preview, CancellationToken cancellationToken = default)
    {
        CommitCount++;
        if (CommitThrow is not null)
        {
            throw CommitThrow;
        }

        return Task.CompletedTask;
    }

    private static BackupPreview SamplePreview()
    {
        var envelope = new EncryptedVault(
            1, "argon2id", Argon2Parameters.ForTesting,
            new byte[16], "AES-256-GCM", new byte[12], new byte[48], new byte[12], new byte[62]);
        return new BackupPreview(envelope, 1, 2, "backup.bitosx-backup", 1234);
    }
}

internal sealed class FakeFilePicker : IFilePicker
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }

    public Task<string?> PickBackupSaveAsync(string suggestedFileName, CancellationToken cancellationToken = default) => Task.FromResult(SavePath);
    public Task<string?> PickBackupOpenAsync(CancellationToken cancellationToken = default) => Task.FromResult(OpenPath);
}

/// <summary>Records copies and clears; can simulate a clipboard that is busy.</summary>
internal sealed class FakeClipboardService : IClipboardService
{
    public TimeSpan ClearDelay => TimeSpan.FromSeconds(30);
    public bool HasPendingSecret { get; set; }
    public string? LastCopied { get; private set; }
    public int ClearCount { get; private set; }
    public Exception? CopyThrow { get; set; }

    public Task CopySecretAsync(string value, CancellationToken cancellationToken = default)
    {
        if (CopyThrow is not null)
        {
            return Task.FromException(CopyThrow);
        }

        LastCopied = value;
        HasPendingSecret = true;
        return Task.CompletedTask;
    }

    public Task ClearIfUnchangedAsync(CancellationToken cancellationToken = default)
    {
        ClearCount++;
        HasPendingSecret = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAppSettingsStore : IAppSettingsStore
{
    public AppSettings? Saved { get; private set; }
    public Exception? SaveThrow { get; set; }

    public AppSettings Load() => AppSettings.Default;

    public void Save(AppSettings settings)
    {
        if (SaveThrow is not null)
        {
            throw SaveThrow;
        }

        Saved = settings;
    }
}

/// <summary>Reports a fixed duration, or a device without enough memory.</summary>
internal sealed class FakeKeyDerivationBenchmark : IKeyDerivationBenchmark
{
    public TimeSpan Elapsed { get; set; } = TimeSpan.FromMilliseconds(210);
    public Exception? MeasureThrow { get; set; }
    public List<Argon2Parameters> Measured { get; } = [];

    public Task<TimeSpan> MeasureAsync(Argon2Parameters parameters, CancellationToken cancellationToken = default)
    {
        Measured.Add(parameters);
        return MeasureThrow is not null ? Task.FromException<TimeSpan>(MeasureThrow) : Task.FromResult(Elapsed);
    }
}

/// <summary>
/// The BitosX server as the test application sees it: one account, signed in with a known
/// password, with two sessions. Records what it is asked; never touches the network.
/// </summary>
internal sealed class ScriptedServer : BitosX.ApiClient.IBitosXApiClient
{
    public const string Email = "alice@example.com";
    public const string Password = "correct horse battery";

    private static readonly DateTimeOffset Created = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    public BitosX.ApiClient.ApiServer? Server { get; } = BitosX.ApiClient.ApiServer.Parse("https://api.example.test");

    public bool IsServerIntegrationEnabled => true;

    public int LoginCount { get; private set; }
    public int RefreshCount { get; private set; }
    public int LogoutCount { get; private set; }
    public int LogoutAllCount { get; private set; }
    public List<Guid> Revoked { get; } = [];

    /// <summary>When set, every call fails with this.</summary>
    public BitosX.ApiClient.ApiException? Throws { get; set; }

    public Guid CurrentSessionId { get; } = Guid.NewGuid();
    public Guid OtherSessionId { get; } = Guid.NewGuid();

    private void MaybeThrow()
    {
        if (Throws is not null)
        {
            throw Throws;
        }
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(Throws is null);

    /// <summary>When set, the password step answers with a challenge and this code completes it.</summary>
    public string? SecondFactorCode { get; set; }
    public int SecondFactorCount { get; private set; }

    public Task<BitosX.ApiClient.SignInResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        LoginCount++;
        if (!string.Equals(email, Email, StringComparison.OrdinalIgnoreCase) || !string.Equals(password, Password, StringComparison.Ordinal))
        {
            throw new BitosX.ApiClient.ApiException(BitosX.ApiClient.ApiFailure.Unauthenticated, 401);
        }

        if (SecondFactorCode is not null)
        {
            return Task.FromResult(BitosX.ApiClient.SignInResult.SecondFactor(new BitosX.ApiClient.SecondFactorChallenge(new string('M', 43), DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        return Task.FromResult(BitosX.ApiClient.SignInResult.SignedIn(Tokens()));
    }

    public Task<BitosX.ApiClient.AccountTokens> CompleteSecondFactorAsync(string mfaToken, string code, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        SecondFactorCount++;
        if (mfaToken != new string('M', 43) || SecondFactorCode is null || !string.Equals(code, SecondFactorCode, StringComparison.Ordinal))
        {
            throw new BitosX.ApiClient.ApiException(BitosX.ApiClient.ApiFailure.Unauthenticated, 401);
        }

        return Task.FromResult(Tokens());
    }

    public Task<BitosX.ApiClient.AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        RefreshCount++;
        return Task.FromResult(Tokens());
    }

    public Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        LogoutCount++;
        return Task.CompletedTask;
    }

    public Task LogoutAllAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        LogoutAllCount++;
        return Task.CompletedTask;
    }

    public Task<BitosX.ApiClient.AccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        return Task.FromResult(new BitosX.ApiClient.AccountProfile(Guid.Parse("11111111-2222-4333-8444-555555555555"), Email, true, Created));
    }

    public Task<IReadOnlyList<BitosX.ApiClient.AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var sessions = new List<BitosX.ApiClient.AccountSessionInfo>();
        if (!Revoked.Contains(CurrentSessionId))
        {
            sessions.Add(new(CurrentSessionId, now.AddMinutes(-5), now, now.AddDays(30), Current: true));
        }

        if (!Revoked.Contains(OtherSessionId))
        {
            sessions.Add(new(OtherSessionId, now.AddDays(-3), now.AddHours(-2), now.AddDays(27), Current: false));
        }

        return Task.FromResult<IReadOnlyList<BitosX.ApiClient.AccountSessionInfo>>(sessions);
    }

    public Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        Revoked.Add(sessionId);
        return Task.CompletedTask;
    }

    // ---- sync: one stored envelope, compare-and-set on its revision, as the real server does ----

    public byte[]? StoredEnvelope { get; private set; }
    public long StoredRevision { get; private set; }
    public int UploadCount { get; private set; }
    public int DownloadCount { get; private set; }
    public int DeleteCount { get; private set; }

    /// <summary>Puts an envelope on the server from outside, as another device would.</summary>
    public void Store(byte[] envelope)
    {
        StoredEnvelope = envelope;
        StoredRevision++;
    }

    private BitosX.ApiClient.SyncStatus? Status()
        => StoredEnvelope is null
            ? null
            : new(StoredRevision, BitosX.ApiClient.HttpBitosXApiClient.Fingerprint(StoredEnvelope), StoredEnvelope.Length, DateTimeOffset.UtcNow);

    public Task<BitosX.ApiClient.SyncStatus?> GetSyncStatusAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        return Task.FromResult(Status());
    }

    public Task<BitosX.ApiClient.SyncEnvelope?> DownloadEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        DownloadCount++;
        return Task.FromResult(Status() is { } status ? new BitosX.ApiClient.SyncEnvelope(status, StoredEnvelope!) : null);
    }

    public Task<BitosX.ApiClient.SyncStatus> UploadEnvelopeAsync(string accessToken, long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        UploadCount++;
        if (baseRevision != StoredRevision)
        {
            throw new BitosX.ApiClient.ApiException(BitosX.ApiClient.ApiFailure.Conflict, 409);
        }

        Store(envelope.ToArray());
        return Task.FromResult(Status()!);
    }

    public Task DeleteEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        DeleteCount++;
        StoredEnvelope = null;
        return Task.CompletedTask;
    }

    private static BitosX.ApiClient.AccountTokens Tokens()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new("jwt.test", now.AddMinutes(15), "refresh.test", now.AddDays(30));
    }
}

/// <summary>A sync service the page tests script directly: what it answers, what it throws, what it was asked.</summary>
internal sealed class FakeVaultSyncService : BitosX.ApiClient.Sync.IVaultSyncService
{
    private static readonly BitosX.ApiClient.SyncStatus SampleStatus = new(3, new string('a', 64), 2048, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    public BitosX.ApiClient.Sync.SyncResult NextResult { get; set; } = new(BitosX.ApiClient.Sync.SyncOutcome.UpToDate, SampleStatus, DateTimeOffset.UtcNow);
    public Exception? Throws { get; set; }
    public Exception? CommitThrows { get; set; }

    public int SyncCount { get; private set; }
    public int OverwriteCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int CommitCount { get; private set; }
    public List<string> PasswordsTried { get; } = [];
    public BitosX.ApiClient.Sync.SyncDownload? Committed { get; private set; }

    private void MaybeThrow()
    {
        if (Throws is not null)
        {
            throw Throws;
        }
    }

    public Task<BitosX.ApiClient.Sync.SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        SyncCount++;
        return Task.FromResult(NextResult);
    }

    public Task<BitosX.ApiClient.Sync.SyncDownload> PrepareDownloadAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        PasswordsTried.Add(masterPassword);
        MaybeThrow();
        var envelope = new EncryptedVault(
            1, "argon2id", Argon2Parameters.ForTesting,
            new byte[16], "AES-256-GCM", new byte[12], new byte[48], new byte[12], new byte[62]);
        return Task.FromResult(new BitosX.ApiClient.Sync.SyncDownload(new BackupPreview(envelope, 1, 7, "server revision 3", 2048), SampleStatus));
    }

    public Task CommitDownloadAsync(BitosX.ApiClient.Sync.SyncDownload download, CancellationToken cancellationToken = default)
    {
        if (CommitThrows is not null)
        {
            throw CommitThrows;
        }

        CommitCount++;
        Committed = download;
        return Task.CompletedTask;
    }

    public Task<BitosX.ApiClient.Sync.SyncResult> OverwriteServerAsync(CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        OverwriteCount++;
        return Task.FromResult(new BitosX.ApiClient.Sync.SyncResult(BitosX.ApiClient.Sync.SyncOutcome.Uploaded, SampleStatus with { Revision = 4 }, DateTimeOffset.UtcNow));
    }

    public Task DeleteFromServerAsync(CancellationToken cancellationToken = default)
    {
        MaybeThrow();
        DeleteCount++;
        return Task.CompletedTask;
    }
}

/// <summary>An account session the page tests script directly, without a server or tokens.</summary>
internal sealed class FakeAccountSession : BitosX.ApiClient.IAccountSession
{
    public bool IsAvailable { get; set; } = true;

    public string? ServerName { get; set; } = "api.example.test";

    public bool IsSignedIn { get; private set; }

    public event EventHandler? Changed;

    public (string Email, string Password)? LastSignIn { get; private set; }
    public string? LastCode { get; private set; }

    /// <summary>When true the password step asks for a code; <see cref="AcceptedCode"/> completes it.</summary>
    public bool RequiresSecondFactor { get; set; }
    public string AcceptedCode { get; set; } = "123456";
    public bool IsAwaitingSecondFactor { get; private set; }

    public BitosX.ApiClient.ApiException? SignInThrows { get; set; }
    public BitosX.ApiClient.ApiException? ProfileThrows { get; set; }
    public BitosX.ApiClient.ApiException? SignOutThrows { get; set; }

    public int SignOutCount { get; private set; }
    public int SignOutEverywhereCount { get; private set; }
    public List<Guid> Revoked { get; } = [];

    public List<BitosX.ApiClient.AccountSessionInfo> Sessions { get; } = [];

    /// <summary>Signs in or out from outside the page, as the vault locking would.</summary>
    public void Set(bool signedIn)
    {
        IsSignedIn = signedIn;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<BitosX.ApiClient.SignInOutcome> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        LastSignIn = (email, password);
        IsAwaitingSecondFactor = false;
        if (SignInThrows is not null)
        {
            return Task.FromException<BitosX.ApiClient.SignInOutcome>(SignInThrows);
        }

        if (RequiresSecondFactor)
        {
            IsAwaitingSecondFactor = true;
            return Task.FromResult(BitosX.ApiClient.SignInOutcome.SecondFactorRequired);
        }

        Set(true);
        return Task.FromResult(BitosX.ApiClient.SignInOutcome.SignedIn);
    }

    public Task CompleteSignInAsync(string code, CancellationToken cancellationToken = default)
    {
        LastCode = code;
        if (!IsAwaitingSecondFactor || !string.Equals(code, AcceptedCode, StringComparison.Ordinal))
        {
            return Task.FromException(new BitosX.ApiClient.ApiException(BitosX.ApiClient.ApiFailure.Unauthenticated, 401));
        }

        IsAwaitingSecondFactor = false;
        Set(true);
        return Task.CompletedTask;
    }

    public void CancelSignIn() => IsAwaitingSecondFactor = false;

    public Task<BitosX.ApiClient.AccountProfile> GetProfileAsync(CancellationToken cancellationToken = default)
        => ProfileThrows is not null
            ? Task.FromException<BitosX.ApiClient.AccountProfile>(ProfileThrows)
            : Task.FromResult(new BitosX.ApiClient.AccountProfile(Guid.NewGuid(), "alice@example.com", true, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)));

    public Task<IReadOnlyList<BitosX.ApiClient.AccountSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BitosX.ApiClient.AccountSessionInfo>>([.. Sessions.Where(s => !Revoked.Contains(s.Id))]);

    public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Revoked.Add(sessionId);
        if (Sessions.Any(s => s.Id == sessionId && s.Current))
        {
            Set(false);
        }

        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOutCount++;
        Set(false);
        return SignOutThrows is not null ? Task.FromException(SignOutThrows) : Task.CompletedTask;
    }

    public Task<BitosX.ApiClient.SyncStatus?> GetSyncStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BitosX.ApiClient.SyncEnvelope?> DownloadEnvelopeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BitosX.ApiClient.SyncStatus> UploadEnvelopeAsync(long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvelopeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SignOutEverywhereAsync(CancellationToken cancellationToken = default)
    {
        SignOutEverywhereCount++;
        Set(false);
        return Task.CompletedTask;
    }
}
