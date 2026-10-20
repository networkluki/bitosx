using BitosX.ApiClient;
using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// The in-memory account session: tokens are held only while signed in, refreshed exactly once
/// however many callers need them, and dropped on sign-out, on a 401, and when the vault locks.
/// </summary>
public sealed class AccountSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Setup : IDisposable
    {
        public Clock Clock { get; } = new();

        public ScriptedApi Api { get; } = new();

        public FakeVault Vault { get; } = new();

        public AccountSession Session { get; }

        public int Changes { get; private set; }

        public Setup()
        {
            Session = new AccountSession(Api, Vault, Clock, NullLogger<AccountSession>.Instance);
            Session.Changed += (_, _) => Changes++;
        }

        public void Dispose()
        {
            Session.Dispose();
            Api.Dispose();
            Vault.Dispose();
        }

        public AccountTokens Tokens(int accessSeconds = 900, string access = "jwt.1", string refresh = "r.1")
            => new(access, Clock.Now.AddSeconds(accessSeconds), refresh, Clock.Now.AddDays(30));

        public async Task SignedInAsync(int accessSeconds = 900)
        {
            Api.LoginResult = Tokens(accessSeconds);
            await Session.SignInAsync("alice@example.com", "correct horse battery");
        }

    }

    [Fact]
    public async Task SignIn_HoldsTheTokens_AndSaysSo()
    {
        using var s = new Setup();

        Assert.False(s.Session.IsSignedIn);
        await s.SignedInAsync();

        Assert.True(s.Session.IsSignedIn);
        Assert.Equal(1, s.Changes);
        Assert.Equal(("alice@example.com", "correct horse battery"), s.Api.LastLogin);
        Assert.True(s.Session.IsAvailable);
        Assert.Equal("api.example.test", s.Session.ServerName);
    }

    [Fact]
    public async Task AFailedSignIn_LeavesNothingBehind()
    {
        using var s = new Setup();
        s.Api.LoginThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.SignInAsync("alice@example.com", "wrong"));

        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(0, s.Changes);
    }

    [Fact]
    public async Task AuthenticatedCalls_UseTheAccessToken()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        await s.Session.GetProfileAsync();
        await s.Session.GetSessionsAsync();
        await s.Session.RevokeSessionAsync(Guid.NewGuid());

        Assert.Equal(["jwt.1", "jwt.1", "jwt.1"], s.Api.AccessTokensSeen);
        Assert.Equal(0, s.Api.RefreshCount);
    }

    [Fact]
    public async Task AnExpiringAccessToken_IsRefreshedFirst()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 900);
        s.Clock.Now = s.Clock.Now.AddSeconds(900 - 10); // inside the margin
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");

        await s.Session.GetProfileAsync();

        Assert.Equal(1, s.Api.RefreshCount);
        Assert.Equal("r.1", s.Api.LastRefreshToken);
        Assert.Equal(["jwt.2"], s.Api.AccessTokensSeen);
    }

    [Fact]
    public async Task ManyCallersAtOnce_CauseExactlyOneRefresh()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");
        s.Api.RefreshDelay = TimeSpan.FromMilliseconds(100);

        await Task.WhenAll(
            s.Session.GetProfileAsync(),
            s.Session.GetSessionsAsync(),
            s.Session.GetProfileAsync(),
            s.Session.GetSessionsAsync());

        // The server ends the session when a refresh token is presented twice, so this is the test that matters.
        Assert.Equal(1, s.Api.RefreshCount);
        Assert.Equal(4, s.Api.AccessTokensSeen.Count);
        Assert.All(s.Api.AccessTokensSeen, t => Assert.Equal("jwt.2", t));
    }

    [Fact]
    public async Task ARefusedRefresh_EndsTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes);
    }

    [Fact]
    public async Task A401OnACall_EndsTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.ProfileThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.Equal(ApiFailure.Unauthenticated, ex.Failure);
        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task AnotherFailure_KeepsTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.ProfileThrows = new ApiException(ApiFailure.Network);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.True(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task SignOut_TellsTheServer_AndForgets()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        await s.Session.SignOutAsync();

        Assert.Equal(1, s.Api.LogoutCount);
        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes);
    }

    [Fact]
    public async Task SignOut_ForgetsEvenWhenTheServerCannotBeTold()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.LogoutThrows = new ApiException(ApiFailure.Network);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.SignOutAsync());

        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task SignOutEverywhere_UsesTheOtherEndpoint()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        await s.Session.SignOutEverywhereAsync();

        Assert.Equal(1, s.Api.LogoutAllCount);
        Assert.Equal(0, s.Api.LogoutCount);
        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task LockingTheVault_SignsOut_AndTellsTheServerBestEffort()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        s.Vault.Lock();

        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes);
        await s.Api.WaitForLogoutsAsync(1);
        Assert.Equal(1, s.Api.LogoutCount);
    }

    [Fact]
    public async Task LockingTheVault_SignsOut_EvenIfTheServerIsDown()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.LogoutThrows = new ApiException(ApiFailure.Network);

        s.Vault.Lock();

        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public void LockingTheVault_WhileSignedOut_IsNothing()
    {
        using var s = new Setup();

        s.Vault.Lock();

        Assert.Equal(0, s.Changes);
        Assert.Equal(0, s.Api.LogoutCount);
    }

    [Fact]
    public async Task AnExpiredRefreshToken_MeansSignedOut()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        s.Clock.Now = s.Clock.Now.AddDays(31);

        Assert.False(s.Session.IsSignedIn);
        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());
        Assert.Equal(0, s.Api.RefreshCount);
    }

    [Fact]
    public async Task WithoutAServer_NothingCanBeDone()
    {
        using var vault = new FakeVault();
        using var session = new AccountSession(new LocalOnlyApiClient(), vault, TimeProvider.System, NullLogger<AccountSession>.Instance);

        Assert.False(session.IsAvailable);
        Assert.Null(session.ServerName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SignInAsync("a@b.c", "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetProfileAsync());
    }

    [Fact]
    public async Task AfterDispose_TheVaultIsNoLongerWatched()
    {
        var s = new Setup();
        await s.SignedInAsync();
        s.Session.Dispose();

        s.Vault.Lock();

        Assert.Equal(0, s.Api.LogoutCount);
    }

    // ---- fakes ----

    private sealed class ScriptedApi : IBitosXApiClient, IDisposable
    {
        private readonly SemaphoreSlim _logouts = new(0);

        public void Dispose() => _logouts.Dispose();

        public bool IsServerIntegrationEnabled => true;

        public ApiServer? Server => FakeServer.Address;

        public AccountTokens? LoginResult { get; set; }
        public Exception? LoginThrows { get; set; }
        public (string Email, string Password)? LastLogin { get; private set; }

        public AccountTokens? RefreshResult { get; set; }
        public Exception? RefreshThrows { get; set; }
        public TimeSpan RefreshDelay { get; set; }
        public int RefreshCount { get; private set; }
        public string? LastRefreshToken { get; private set; }

        public Exception? ProfileThrows { get; set; }
        public Exception? LogoutThrows { get; set; }
        public int LogoutCount { get; private set; }
        public int LogoutAllCount { get; private set; }

        public List<string> AccessTokensSeen { get; } = [];

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<AccountTokens> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            LastLogin = (email, password);
            return LoginThrows is not null ? Task.FromException<AccountTokens>(LoginThrows) : Task.FromResult(LoginResult!);
        }

        public async Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            LastRefreshToken = refreshToken;
            if (RefreshDelay > TimeSpan.Zero)
            {
                await Task.Delay(RefreshDelay, cancellationToken).ConfigureAwait(false);
            }

            if (RefreshThrows is not null)
            {
                throw RefreshThrows;
            }

            return RefreshResult!;
        }

        public Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            LogoutCount++;
            _logouts.Release();
            return LogoutThrows is not null ? Task.FromException(LogoutThrows) : Task.CompletedTask;
        }

        public Task LogoutAllAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            LogoutAllCount++;
            return LogoutThrows is not null ? Task.FromException(LogoutThrows) : Task.CompletedTask;
        }

        public Task<AccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return ProfileThrows is not null
                ? Task.FromException<AccountProfile>(ProfileThrows)
                : Task.FromResult(new AccountProfile(Guid.NewGuid(), "alice@example.com", true, Start));
        }

        public Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.FromResult<IReadOnlyList<AccountSessionInfo>>([]);
        }

        public Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.CompletedTask;
        }

        public async Task WaitForLogoutsAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.True(await _logouts.WaitAsync(TimeSpan.FromSeconds(5)), "the server was not told in time");
            }
        }
    }

    /// <summary>A vault that can only be locked; that is all the session asks of it.</summary>
    private sealed class FakeVault : IVaultService, IDisposable
    {
        public VaultState State { get; private set; } = VaultState.Unlocked;

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
        public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
