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
    public async Task A401OnACall_RefreshesOnce_RetriesOnce_AndThenEndsTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");
        s.Api.ProfileThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.Equal(ApiFailure.Unauthenticated, ex.Failure);
        Assert.Equal(1, s.Api.RefreshCount);
        Assert.Equal(["jwt.1", "jwt.2"], s.Api.AccessTokensSeen);
        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes);
    }

    [Fact]
    public async Task A401ThatARefreshCures_IsRetried_AndTheSessionLives()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");
        s.Api.ProfileThrowsOnce = new ApiException(ApiFailure.Unauthenticated, 401);

        AccountProfile profile = await s.Session.GetProfileAsync();

        Assert.Equal("alice@example.com", profile.Email);
        Assert.Equal(1, s.Api.RefreshCount);
        Assert.Equal(["jwt.1", "jwt.2"], s.Api.AccessTokensSeen);
        Assert.True(s.Session.IsSignedIn);
        Assert.Equal(1, s.Changes);
    }

    [Fact]
    public async Task A401WhoseRefreshIsRefused_EndsTheSession_WithoutARetry()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.RefreshThrows = new ApiException(ApiFailure.Unauthenticated, 401);
        s.Api.ProfileThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.Equal(["jwt.1"], s.Api.AccessTokensSeen);
        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task A403_IsNotASignOut()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.ChangePasswordThrows = new ApiException(ApiFailure.Forbidden, 403);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => s.Session.ChangePasswordAsync("wrong", "new account password", null));

        Assert.Equal(ApiFailure.Forbidden, ex.Failure);
        Assert.True(s.Session.IsSignedIn);
        Assert.Equal(0, s.Api.RefreshCount);
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
    public async Task SignOut_ForgetsEvenWhenTheServerCannotBeTold_AndSaysSo()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.LogoutThrows = new ApiException(ApiFailure.Network);

        Assert.False(await s.Session.SignOutAsync());

        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task SignOut_ThatTheServerConfirms_SaysSo()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        Assert.True(await s.Session.SignOutAsync());
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
    public async Task SignOutEverywhere_WithAnExpiredAccessToken_RefreshesFirst()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");

        await s.Session.SignOutEverywhereAsync();

        Assert.Equal(["jwt.2"], s.Api.AccessTokensSeen);
        Assert.Equal(1, s.Api.LogoutAllCount);
    }

    [Theory]
    [InlineData(ApiFailure.Network)]
    [InlineData(ApiFailure.Unavailable)]
    [InlineData(ApiFailure.RateLimited)]
    public async Task SignOutEverywhere_ThatCannotRefresh_FailsLoudly_AndKeepsTheSessionForARetry(ApiFailure failure)
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshThrows = new ApiException(failure);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => s.Session.SignOutEverywhereAsync());

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(0, s.Api.LogoutAllCount);
        Assert.True(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task SignOutEverywhere_ThatTheServerDoesNotHear_FailsLoudly_AndKeepsTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.LogoutThrows = new ApiException(ApiFailure.Network);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.SignOutEverywhereAsync());

        Assert.True(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task SignOutEverywhere_WhoseRefreshIsRefused_FailsLoudly_AndIsSignedOutHere()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshThrows = new ApiException(ApiFailure.Unauthenticated, 401);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.SignOutEverywhereAsync());

        Assert.Equal(0, s.Api.LogoutAllCount);
        Assert.False(s.Session.IsSignedIn);
    }

    // ---- ending this session from the list, and the account itself ----

    [Fact]
    public async Task EndingThisSessionFromTheList_SignsOutHere()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        var current = Guid.NewGuid();
        s.Api.SessionsResult = [new AccountSessionInfo(current, Start, Start, Start.AddDays(30), Current: true), new AccountSessionInfo(Guid.NewGuid(), Start, Start, Start.AddDays(30), Current: false)];
        await s.Session.GetSessionsAsync();

        await s.Session.RevokeSessionAsync(s.Api.SessionsResult[1].Id);
        Assert.True(s.Session.IsSignedIn);

        await s.Session.RevokeSessionAsync(current);
        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes);
    }

    [Fact]
    public async Task ChangePassword_KeepsThisSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        await s.Session.ChangePasswordAsync("old account password", "new account password", "123456");

        Assert.Equal(("old account password", "new account password", "123456"), s.Api.LastPasswordChange);
        Assert.True(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task DeleteAccount_SignsOutHere_OnlyWhenTheServerDeletedIt()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.DeleteAccountThrows = new ApiException(ApiFailure.Forbidden, 403);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.DeleteAccountAsync("wrong", null));
        Assert.True(s.Session.IsSignedIn);

        s.Api.DeleteAccountThrows = null;
        await s.Session.DeleteAccountAsync("account password", "abcde-fghjk");
        Assert.Equal(("account password", "abcde-fghjk"), s.Api.LastDeleteAccount);
        Assert.False(s.Session.IsSignedIn);
    }

    [Fact]
    public async Task TheAccountId_IsAskedForOncePerSession()
    {
        using var s = new Setup();
        await s.SignedInAsync();

        Guid first = await s.Session.GetAccountIdAsync();
        Guid again = await s.Session.GetAccountIdAsync();

        Assert.Equal(first, again);
        Assert.Equal(1, s.Api.ProfileCount);

        await s.Session.SignOutAsync();
        await s.SignedInAsync();
        await s.Session.GetAccountIdAsync();
        Assert.Equal(2, s.Api.ProfileCount);
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
    public async Task LockingTheVault_AfterTheAccessTokenExpired_RefreshesToTellTheServer()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 900);
        s.Clock.Now = s.Clock.Now.AddMinutes(20); // an inactivity lock always comes after the access token expired
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");

        s.Vault.Lock();

        Assert.False(s.Session.IsSignedIn);
        await s.Api.WaitForLogoutsAsync(1);
        Assert.Equal("r.1", s.Api.LastRefreshToken);
        Assert.Equal(["jwt.2"], s.Api.AccessTokensSeen);
    }

    [Fact]
    public async Task Closing_TellsTheServer_AndWaitsForIt()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        s.Api.RefreshResult = s.Tokens(access: "jwt.2", refresh: "r.2");

        await s.Session.EndSessionAsync();

        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(1, s.Api.LogoutCount);
        Assert.Equal(["jwt.2"], s.Api.AccessTokensSeen);
    }

    [Fact]
    public async Task Closing_NeverThrows_EvenWhenTheServerIsDown()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.LogoutThrows = new ApiException(ApiFailure.Network);

        await s.Session.EndSessionAsync();

        Assert.False(s.Session.IsSignedIn);
    }

    // ---- what completes after the session ended does not bring it back ----

    [Fact]
    public async Task ARefreshThatCompletesAfterTheVaultLocked_DoesNotResurrectTheSession()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        var answer = new TaskCompletionSource<AccountTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Api.RefreshGate = answer.Task;

        Task<AccountProfile> call = s.Session.GetProfileAsync();
        await s.Api.WaitForRefreshStartedAsync();
        s.Vault.Lock();
        answer.SetResult(s.Tokens(access: "jwt.late", refresh: "r.late"));

        await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(2, s.Changes); // signed in, then the lock; nothing after
        await s.Api.WaitUntilLoggedOutAsync("jwt.late"); // the late pair's session is ended on the server
        Assert.DoesNotContain("jwt.late", s.Api.ProfileTokensSeen);
    }

    [Fact]
    public async Task ASignInThatCompletesAfterTheVaultLocked_DoesNotSignIn()
    {
        using var s = new Setup();
        var answer = new TaskCompletionSource<SignInResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Api.LoginGate = answer.Task;

        Task<SignInOutcome> signIn = s.Session.SignInAsync("alice@example.com", "correct horse battery");
        s.Vault.Lock();
        answer.SetResult(SignInResult.SignedIn(s.Tokens(access: "jwt.late", refresh: "r.late")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
        Assert.False(s.Session.IsSignedIn);
        Assert.Equal(0, s.Changes);
        await s.Api.WaitUntilLoggedOutAsync("jwt.late");
    }

    [Fact]
    public async Task ASecondFactorStepThatCompletesAfterTheVaultLocked_DoesNotSignIn()
    {
        using var s = new Setup();
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5));
        await s.Session.SignInAsync("alice@example.com", "correct horse battery");
        var answer = new TaskCompletionSource<AccountTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Api.SecondFactorGate = answer.Task;

        Task complete = s.Session.CompleteSignInAsync("123456");
        s.Vault.Lock();
        answer.SetResult(s.Tokens(access: "jwt.late", refresh: "r.late"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete);
        Assert.False(s.Session.IsSignedIn);
        Assert.False(s.Session.IsAwaitingSecondFactor);
        await s.Api.WaitUntilLoggedOutAsync("jwt.late");
    }

    [Fact]
    public async Task ASignInThatCompletesAfterASignOutAndANewSignIn_DoesNotReplaceTheNewSession()
    {
        using var s = new Setup();
        await s.SignedInAsync(accessSeconds: 0);
        var answer = new TaskCompletionSource<AccountTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Api.RefreshGate = answer.Task;
        Task<AccountProfile> stale = s.Session.GetProfileAsync();
        await s.Api.WaitForRefreshStartedAsync();

        s.Api.LoginResult = s.Tokens(access: "jwt.new", refresh: "r.new");
        await s.Session.SignInAsync("alice@example.com", "correct horse battery"); // the old session is dropped, a new one held
        answer.SetResult(s.Tokens(access: "jwt.old-late", refresh: "r.old-late"));
        await Assert.ThrowsAnyAsync<Exception>(() => stale);

        Assert.True(s.Session.IsSignedIn);
        s.Api.RefreshGate = null;
        await s.Session.GetSessionsAsync();
        Assert.Equal("jwt.new", s.Api.AccessTokensSeen[^1]);
    }

    [Fact]
    public async Task AnExpiredRefreshToken_EndsTheSessionLikeEveryOtherSignOut()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Clock.Now = s.Clock.Now.AddDays(31);

        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync());

        Assert.Equal(2, s.Changes); // Changed is raised, so every page follows
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

    // ---- second factor ----

    [Fact]
    public async Task ASecondFactorChallenge_HoldsNoSession_UntilTheCodeCompletesIt()
    {
        using var s = new Setup();
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5));
        s.Api.SecondFactorResult = s.Tokens();

        SignInOutcome outcome = await s.Session.SignInAsync("alice@example.com", "correct horse battery");

        Assert.Equal(SignInOutcome.SecondFactorRequired, outcome);
        Assert.False(s.Session.IsSignedIn);
        Assert.True(s.Session.IsAwaitingSecondFactor);
        Assert.Equal(0, s.Changes);
        await Assert.ThrowsAsync<ApiException>(() => s.Session.GetProfileAsync()); // nothing authenticated works yet

        await s.Session.CompleteSignInAsync("123456");

        Assert.Equal((new string('A', 43), "123456"), s.Api.LastSecondFactor);
        Assert.True(s.Session.IsSignedIn);
        Assert.False(s.Session.IsAwaitingSecondFactor);
        Assert.Equal(1, s.Changes);
        await s.Session.GetProfileAsync();
        Assert.Equal(["jwt.1"], s.Api.AccessTokensSeen);
    }

    [Fact]
    public async Task ARefusedCode_KeepsTheChallenge_ForAnotherTry()
    {
        using var s = new Setup();
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5));
        s.Api.SecondFactorThrows = new ApiException(ApiFailure.Unauthenticated, 401);
        await s.Session.SignInAsync("alice@example.com", "correct horse battery");

        await Assert.ThrowsAsync<ApiException>(() => s.Session.CompleteSignInAsync("000000"));

        Assert.True(s.Session.IsAwaitingSecondFactor);
        Assert.False(s.Session.IsSignedIn);
        s.Api.SecondFactorThrows = null;
        s.Api.SecondFactorResult = s.Tokens();
        await s.Session.CompleteSignInAsync("654321");
        Assert.True(s.Session.IsSignedIn);
        Assert.Equal(2, s.Api.SecondFactorCount);
    }

    [Fact]
    public async Task AnExpiredOrCancelledChallenge_MeansStartingOver()
    {
        using var s = new Setup();
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5));
        await s.Session.SignInAsync("alice@example.com", "correct horse battery");

        s.Clock.Now = s.Clock.Now.AddMinutes(6);
        Assert.False(s.Session.IsAwaitingSecondFactor);
        ApiException expired = await Assert.ThrowsAsync<ApiException>(() => s.Session.CompleteSignInAsync("123456"));
        Assert.Equal(ApiFailure.Unauthenticated, expired.Failure);
        Assert.Equal(0, s.Api.SecondFactorCount); // nothing was sent for a dead challenge

        s.Clock.Now = Start;
        await s.Session.SignInAsync("alice@example.com", "correct horse battery");
        Assert.True(s.Session.IsAwaitingSecondFactor);
        s.Session.CancelSignIn();
        Assert.False(s.Session.IsAwaitingSecondFactor);
        await Assert.ThrowsAsync<ApiException>(() => s.Session.CompleteSignInAsync("123456"));
    }

    [Fact]
    public async Task TheChallengesMethods_AreKnownWhileItIsPending_AndNoneAfterwards()
    {
        using var s = new Setup();
        Assert.Equal(SecondFactorMethods.None, s.Session.SecondFactorMethods);
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5), SecondFactorMethods.Code | SecondFactorMethods.Passkey);
        s.Api.SecondFactorResult = s.Tokens();

        await s.Session.SignInAsync("alice@example.com", "correct horse battery");
        Assert.Equal(SecondFactorMethods.Code | SecondFactorMethods.Passkey, s.Session.SecondFactorMethods);

        // Passkeys or not, this client completes the step with a code.
        await s.Session.CompleteSignInAsync("123456");
        Assert.True(s.Session.IsSignedIn);
        Assert.Equal(SecondFactorMethods.None, s.Session.SecondFactorMethods);

        await s.Session.SignInAsync("alice@example.com", "correct horse battery");
        s.Clock.Now = s.Clock.Now.AddMinutes(6);
        Assert.Equal(SecondFactorMethods.None, s.Session.SecondFactorMethods); // an expired challenge offers nothing
    }

    [Fact]
    public async Task ThePasskeyList_IsAskedWithThisSessionsToken()
    {
        using var s = new Setup();
        await s.SignedInAsync();
        s.Api.Passkeys = new PasskeyList([new PasskeyInfo("pk-1", "Laptop", Start, null, true)], 10);

        PasskeyList? list = await s.Session.GetPasskeysAsync();

        Assert.Equal("Laptop", Assert.Single(list!.Passkeys).Name);
        Assert.Equal(["jwt.1"], s.Api.AccessTokensSeen);

        s.Api.Passkeys = null; // a server without passkeys
        Assert.Null(await s.Session.GetPasskeysAsync());
    }

    [Fact]
    public async Task LockingTheVault_DropsAPendingChallengeToo()
    {
        using var s = new Setup();
        s.Api.LoginChallenge = new SecondFactorChallenge(new string('A', 43), s.Clock.Now.AddMinutes(5));
        await s.Session.SignInAsync("alice@example.com", "correct horse battery");
        Assert.True(s.Session.IsAwaitingSecondFactor);

        s.Vault.Lock();

        Assert.False(s.Session.IsAwaitingSecondFactor);
        Assert.Equal(0, s.Changes); // there was no session to lose
    }

    private sealed class ScriptedApi : IBitosXApiClient, IDisposable
    {
        private readonly SemaphoreSlim _logouts = new(0);

        public void Dispose()
        {
            _logouts.Dispose();
            _refreshStarted.Dispose();
        }

        public bool IsServerIntegrationEnabled => true;

        public ApiServer? Server => FakeServer.Address;

        public AccountTokens? LoginResult { get; set; }
        public SecondFactorChallenge? LoginChallenge { get; set; }
        public Exception? LoginThrows { get; set; }
        public (string Email, string Password)? LastLogin { get; private set; }

        public AccountTokens? SecondFactorResult { get; set; }
        public Exception? SecondFactorThrows { get; set; }
        public (string MfaToken, string Code)? LastSecondFactor { get; private set; }
        public int SecondFactorCount { get; private set; }

        public AccountTokens? RefreshResult { get; set; }
        public Exception? RefreshThrows { get; set; }
        public TimeSpan RefreshDelay { get; set; }
        public int RefreshCount { get; private set; }
        public string? LastRefreshToken { get; private set; }

        public Exception? ProfileThrows { get; set; }
        public Exception? ProfileThrowsOnce { get; set; }
        public int ProfileCount { get; private set; }
        public List<string> ProfileTokensSeen { get; } = [];
        public Exception? LogoutThrows { get; set; }
        public List<string> LoggedOut { get; } = [];
        public IReadOnlyList<AccountSessionInfo> SessionsResult { get; set; } = [];
        public Task<AccountTokens>? RefreshGate { get; set; }
        public Task<SignInResult>? LoginGate { get; set; }
        public Task<AccountTokens>? SecondFactorGate { get; set; }
        public Exception? ChangePasswordThrows { get; set; }
        public (string Current, string New, string? Code)? LastPasswordChange { get; private set; }
        public Exception? DeleteAccountThrows { get; set; }
        public (string Password, string? Code)? LastDeleteAccount { get; private set; }
        private readonly SemaphoreSlim _refreshStarted = new(0);
        public int LogoutCount { get; private set; }
        public int LogoutAllCount { get; private set; }

        public List<string> AccessTokensSeen { get; } = [];

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SignInResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            LastLogin = (email, password);
            if (LoginThrows is not null)
            {
                return Task.FromException<SignInResult>(LoginThrows);
            }

            if (LoginGate is { } gate)
            {
                return gate;
            }

            return Task.FromResult(LoginChallenge is { } c ? SignInResult.SecondFactor(c) : SignInResult.SignedIn(LoginResult!));
        }

        public Task<AccountTokens> CompleteSecondFactorAsync(string mfaToken, string code, CancellationToken cancellationToken = default)
        {
            SecondFactorCount++;
            LastSecondFactor = (mfaToken, code);
            if (SecondFactorGate is { } gate)
            {
                return gate;
            }

            return SecondFactorThrows is not null ? Task.FromException<AccountTokens>(SecondFactorThrows) : Task.FromResult(SecondFactorResult!);
        }

        public async Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            LastRefreshToken = refreshToken;
            _refreshStarted.Release();
            if (RefreshGate is { } gate)
            {
                return await gate.ConfigureAwait(false);
            }

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
            lock (LoggedOut)
            {
                LoggedOut.Add(accessToken);
            }

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
            ProfileTokensSeen.Add(accessToken);
            ProfileCount++;
            if (ProfileThrowsOnce is { } once)
            {
                ProfileThrowsOnce = null;
                return Task.FromException<AccountProfile>(once);
            }

            return ProfileThrows is not null
                ? Task.FromException<AccountProfile>(ProfileThrows)
                : Task.FromResult(new AccountProfile(Guid.NewGuid(), "alice@example.com", true, Start));
        }

        public Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.FromResult(SessionsResult);
        }

        public Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.CompletedTask;
        }

        public Task ChangePasswordAsync(string accessToken, string currentPassword, string newPassword, string? code, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            LastPasswordChange = (currentPassword, newPassword, code);
            return ChangePasswordThrows is not null ? Task.FromException(ChangePasswordThrows) : Task.CompletedTask;
        }

        public Task DeleteAccountAsync(string accessToken, string password, string? code, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            LastDeleteAccount = (password, code);
            return DeleteAccountThrows is not null ? Task.FromException(DeleteAccountThrows) : Task.CompletedTask;
        }

        public Task<TwoFactorStatus?> GetTwoFactorStatusAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.FromResult<TwoFactorStatus?>(new TwoFactorStatus(false, null, 0, false));
        }

        /// <summary>What the passkey list answers; <see langword="null"/>: the server does not offer passkeys.</summary>
        public PasskeyList? Passkeys { get; set; }

        public Task<PasskeyList?> GetPasskeysAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return Task.FromResult(Passkeys);
        }

        public async Task WaitForRefreshStartedAsync()
            => Assert.True(await _refreshStarted.WaitAsync(TimeSpan.FromSeconds(5)), "no refresh started");

        public async Task WaitUntilLoggedOutAsync(string accessToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (LoggedOut)
                {
                    if (LoggedOut.Contains(accessToken))
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            Assert.Fail("the server was not told to end that session");
        }

        public SyncStatus? SyncStatusResult { get; set; }
        public Exception? SyncThrows { get; set; }
        public (long BaseRevision, byte[] Envelope)? LastUpload { get; private set; }

        public Task<SyncStatus?> GetSyncStatusAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return SyncThrows is not null ? Task.FromException<SyncStatus?>(SyncThrows) : Task.FromResult(SyncStatusResult);
        }

        public Task<SyncEnvelope?> DownloadEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return SyncThrows is not null
                ? Task.FromException<SyncEnvelope?>(SyncThrows)
                : Task.FromResult(SyncStatusResult is { } s ? new SyncEnvelope(s, new byte[s.SizeBytes]) : null);
        }

        public Task<SyncStatus> UploadEnvelopeAsync(string accessToken, long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            LastUpload = (baseRevision, envelope.ToArray());
            return SyncThrows is not null
                ? Task.FromException<SyncStatus>(SyncThrows)
                : Task.FromResult(new SyncStatus(baseRevision + 1, HttpBitosXApiClient.Fingerprint(envelope.Span), envelope.Length, DateTimeOffset.UtcNow));
        }

        public Task DeleteEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            AccessTokensSeen.Add(accessToken);
            return SyncThrows is not null ? Task.FromException(SyncThrows) : Task.CompletedTask;
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
