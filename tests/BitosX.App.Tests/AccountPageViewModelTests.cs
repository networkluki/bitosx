using BitosX.ApiClient;
using BitosX.App.ViewModels;

namespace BitosX.App.Tests;

/// <summary>
/// The Account page over a scripted session: what it checks before a round trip, what it says when
/// something fails, that the password never stays on the page, and how it follows the session.
/// </summary>
public sealed class AccountPageViewModelTests
{
    private static readonly Guid Current = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static FakeAccountSession NewSession()
    {
        var session = new FakeAccountSession();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Sessions.Add(new AccountSessionInfo(Current, now.AddMinutes(-5), now, now.AddDays(30), Current: true));
        session.Sessions.Add(new AccountSessionInfo(Other, now.AddDays(-3), now.AddHours(-2), now.AddDays(27), Current: false));
        return session;
    }

    private static async Task<AccountPageViewModel> SignedInAsync(FakeAccountSession session)
    {
        var page = new AccountPageViewModel(session, new FakeVaultSyncService())
        {
            Email = "alice@example.com",
            Password = "correct horse battery",
        };
        await page.SignInCommand.ExecuteAsync(null);
        return page;
    }

    [Fact]
    public void ThePage_OpensSignedOut_OnTheServerItIsGiven()
    {
        using var page = new AccountPageViewModel(NewSession(), new FakeVaultSyncService());

        Assert.True(page.IsAvailable);
        Assert.Equal("api.example.test", page.ServerName);
        Assert.False(page.IsSignedIn);
        Assert.True(page.IsSignedOut);
        Assert.False(page.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignIn_ShowsTheAccountAndItsSessions_AndForgetsThePassword()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        Assert.True(page.IsSignedIn);
        Assert.Equal(("alice@example.com", "correct horse battery"), session.LastSignIn);
        Assert.Equal("alice@example.com", page.ProfileEmail);
        Assert.Equal("Yes", page.ProfileVerified);
        Assert.NotEmpty(page.ProfileCreated);
        Assert.Equal(2, page.Sessions.Count);
        Assert.True(page.Sessions[0].Current);
        Assert.Equal("This device", page.Sessions[0].Title);
        Assert.Equal("Sign out", page.Sessions[0].ActionLabel);
        Assert.Equal("End session", page.Sessions[1].ActionLabel);
        Assert.Equal(string.Empty, page.Password);
        Assert.Equal("Signed in.", page.Status);
    }

    [Theory]
    [InlineData("", "correct horse battery", "Enter the email")]
    [InlineData("not-an-address", "correct horse battery", "Enter the email")]
    [InlineData("alice@example.com", "short", "12 to 128")]
    public async Task ObviousMistakes_AreCaughtBeforeAnyRoundTrip(string email, string password, string expectedStatus)
    {
        FakeAccountSession session = NewSession();
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService()) { Email = email, Password = password };

        if (page.SignInCommand.CanExecute(null))
        {
            await page.SignInCommand.ExecuteAsync(null);
            Assert.Contains(expectedStatus, page.Status, StringComparison.Ordinal);
        }

        Assert.Null(session.LastSignIn);
        Assert.False(page.IsSignedIn);
    }

    [Fact]
    public async Task AWrongPassword_SaysSoGenerically_AndClearsTheField()
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(ApiFailure.Unauthenticated, 401);
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService()) { Email = "alice@example.com", Password = "wrong wrong wrong" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Invalid email or password.", page.Status);
        Assert.Equal(string.Empty, page.Password);
        Assert.Equal("alice@example.com", page.Email); // the email is kept; only the password goes
        Assert.False(page.IsSignedIn);
    }

    [Theory]
    [InlineData(ApiFailure.Network, "could not be reached")]
    [InlineData(ApiFailure.Unavailable, "temporarily unavailable")]
    [InlineData(ApiFailure.Unexpected, "unexpected way")]
    [InlineData(ApiFailure.InvalidRequest, "did not accept")]
    public async Task OtherFailures_AreToldInTheirOwnWords(ApiFailure failure, string expected)
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(failure);
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Contains(expected, page.Status, StringComparison.Ordinal);
        Assert.False(page.IsSignedIn);
    }

    [Fact]
    public async Task ARateLimit_SaysHowLongToWait()
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(ApiFailure.RateLimited, 429, TimeSpan.FromSeconds(30));
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Contains("about 30 seconds", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndingAnotherSession_RemovesItFromTheList()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.RevokeSessionCommand.ExecuteAsync(page.Sessions[1]);

        Assert.Equal([Other], session.Revoked);
        Assert.Single(page.Sessions);
        Assert.True(page.IsSignedIn);
        Assert.Equal("Session ended.", page.Status);
    }

    [Fact]
    public async Task EndingThisSession_SignsOut()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.RevokeSessionCommand.ExecuteAsync(page.Sessions[0]);

        Assert.False(page.IsSignedIn);
        Assert.Empty(page.Sessions);
        Assert.Equal(string.Empty, page.ProfileEmail);
    }

    [Fact]
    public async Task SignOut_ReturnsToTheSignInForm()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, session.SignOutCount);
        Assert.False(page.IsSignedIn);
        Assert.Equal("Signed out.", page.Status);
        Assert.Empty(page.Sessions);
    }

    [Fact]
    public async Task SignOut_IsSignedOutHere_EvenWhenTheServerCannotBeTold()
    {
        FakeAccountSession session = NewSession();
        session.SignOutThrows = new ApiException(ApiFailure.Network);
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutCommand.ExecuteAsync(null);

        Assert.False(page.IsSignedIn);
        Assert.Contains("could not be reached", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutEverywhere_SaysWhatItDid()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutEverywhereCommand.ExecuteAsync(null);

        Assert.Equal(1, session.SignOutEverywhereCount);
        Assert.False(page.IsSignedIn);
        Assert.Contains("everywhere", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedOutFromElsewhere_ThePageFollows()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        session.Set(false); // the vault locked, or the server ended the session

        Assert.False(page.IsSignedIn);
        Assert.Empty(page.Sessions);
        Assert.Equal(string.Empty, page.ProfileEmail);
    }

    [Fact]
    public async Task ASessionThatEndsDuringALoad_LeavesThePageSignedOut()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);
        session.ProfileThrows = new ApiException(ApiFailure.Unauthenticated, 401);
        session.Set(false);

        await page.RefreshSessionsCommand.ExecuteAsync(null);

        Assert.False(page.IsSignedIn);
    }

    [Fact]
    public async Task RefreshAsync_LoadsWhenSignedIn_AndIsQuietWhenNot()
    {
        FakeAccountSession session = NewSession();
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService());

        await page.RefreshAsync();
        Assert.False(page.IsSignedIn);
        Assert.Null(page.Status);

        session.Set(true);
        await page.RefreshAsync();
        Assert.True(page.IsSignedIn);
        Assert.Equal(2, page.Sessions.Count);
    }

    [Fact]
    public void WithoutAServer_ThePageSaysSo_AndOffersNothing()
    {
        var session = new FakeAccountSession { IsAvailable = false, ServerName = null };
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService()) { Email = "alice@example.com", Password = "correct horse battery" };

        Assert.False(page.IsAvailable);
        Assert.Contains("--api-server none", page.UnavailableNote, StringComparison.Ordinal);
        Assert.False(page.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task AfterDispose_ThePageNoLongerFollowsTheSession()
    {
        FakeAccountSession session = NewSession();
        AccountPageViewModel page = await SignedInAsync(session);
        page.Dispose();

        session.Set(false);

        Assert.True(page.IsSignedIn); // frozen as it was; the page is gone
    }
}
