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
        var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener())
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
        using var page = new AccountPageViewModel(NewSession(), new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener());

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
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = email, Password = password };

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
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "wrong wrong wrong" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Invalid email or password.", page.Status);
        Assert.Equal(string.Empty, page.Password);
        Assert.Equal("alice@example.com", page.Email); // the email is kept; only the password goes
        Assert.False(page.IsSignedIn);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("1234567")]
    [InlineData("12345678")]
    [InlineData("abcde-fghjk")]
    [InlineData("ABCDE FGHJK")]
    [InlineData("abcdefghjk")]
    public void CodesOfEveryLengthTheServerUses_AndRecoveryCodes_LookLikeCodes(string code)
        => Assert.True(AccountPageViewModel.LooksLikeACode(code));

    [Fact]
    public async Task A503OnThePasswordStep_DoesNotBlameTwoFactor()
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(ApiFailure.Unavailable, 503);
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Contains("temporarily unavailable", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("wo-factor", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A503OnTheCodeStep_IsAboutTwoFactor()
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };
        await page.SignInCommand.ExecuteAsync(null);

        Assert.Contains("Two-factor sign-in is temporarily unavailable", AccountPageViewModel.Describe(new ApiException(ApiFailure.Unavailable, 503), "code"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A404OnSignIn_SaysSigningInIsNotOffered()
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(ApiFailure.NotFound, 404);
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Signing in is not available on this server.", page.Status);
    }

    [Fact]
    public async Task AnExpiredCodeStep_SaysToStartOver_RatherThanToTryTheNextCode()
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };
        await page.SignInCommand.ExecuteAsync(null);
        session.CancelSignIn(); // the challenge died meanwhile (expired, or too many codes)

        page.Code = "123456";
        await page.SubmitCodeCommand.ExecuteAsync(null);

        Assert.False(page.IsAwaitingCode);
        Assert.Contains("expired", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("next code", page.Status, StringComparison.Ordinal);
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
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.Contains(expected, page.Status, StringComparison.Ordinal);
        Assert.False(page.IsSignedIn);
    }

    // ---- second factor ----

    [Fact]
    public async Task ASecondFactor_SwitchesToTheCodeStep_AndTheCodeSignsIn()
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

        await page.SignInCommand.ExecuteAsync(null);

        Assert.False(page.IsSignedIn);
        Assert.True(page.IsAwaitingCode);
        Assert.False(page.IsEnteringCredentials);
        Assert.Equal(string.Empty, page.Password); // the password never waits on the page
        Assert.Contains("authenticator app", page.Status, StringComparison.Ordinal);
        Assert.False(page.SignInCommand.CanExecute(null));
        Assert.False(page.SubmitCodeCommand.CanExecute(null)); // nothing typed yet

        page.Code = " 123456 ";
        Assert.True(page.SubmitCodeCommand.CanExecute(null));
        await page.SubmitCodeCommand.ExecuteAsync(null);

        Assert.Equal("123456", session.LastCode);
        Assert.True(page.IsSignedIn);
        Assert.False(page.IsAwaitingCode);
        Assert.Equal(string.Empty, page.Code);
        Assert.Equal("Signed in.", page.Status);
        Assert.Equal("alice@example.com", page.ProfileEmail);
    }

    [Fact]
    public async Task AWrongCode_SaysSo_StaysOnTheCodeStep_AndClearsTheCode()
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };
        await page.SignInCommand.ExecuteAsync(null);

        page.Code = "000000";
        await page.SubmitCodeCommand.ExecuteAsync(null);

        Assert.Contains("not accepted", page.Status, StringComparison.Ordinal);
        Assert.True(page.IsAwaitingCode);
        Assert.False(page.IsSignedIn);
        Assert.Equal(string.Empty, page.Code);

        // A recovery code is accepted in its written form.
        session.AcceptedCode = "abcde-fghjk";
        page.Code = "abcde-fghjk";
        await page.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(page.IsSignedIn);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345a")]
    [InlineData("abcde-fghj")]
    [InlineData("abcde_fghjk")]
    [InlineData("123456789")]
    [InlineData("abcde-fghjl")]   // 'l' is not in the recovery alphabet
    [InlineData("abcdef-ghjk")]   // the separator is after the fifth character
    public async Task ACodeThatCannotBeRight_IsCaughtBeforeAnyRoundTrip(string code)
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };
        await page.SignInCommand.ExecuteAsync(null);

        page.Code = code;
        await page.SubmitCodeCommand.ExecuteAsync(null);

        Assert.Null(session.LastCode);
        Assert.Contains("8-digit code", page.Status, StringComparison.Ordinal);
        Assert.True(page.IsAwaitingCode);
    }

    [Fact]
    public async Task StartOver_DropsTheChallenge_AndReturnsToThePassword()
    {
        FakeAccountSession session = NewSession();
        session.RequiresSecondFactor = true;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };
        await page.SignInCommand.ExecuteAsync(null);
        page.Code = "123";

        Assert.True(page.CancelCodeCommand.CanExecute(null));
        page.CancelCodeCommand.Execute(null);

        Assert.False(page.IsAwaitingCode);
        Assert.True(page.IsEnteringCredentials);
        Assert.False(session.IsAwaitingSecondFactor);
        Assert.Equal(string.Empty, page.Code);
        Assert.Equal("alice@example.com", page.Email);
        Assert.Null(page.Status);
    }

    [Fact]
    public async Task ARateLimit_SaysHowLongToWait()
    {
        FakeAccountSession session = NewSession();
        session.SignInThrows = new ApiException(ApiFailure.RateLimited, 429, TimeSpan.FromSeconds(30));
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

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
    public async Task SignOut_ThatTheServerDidNotHear_SaysSo()
    {
        FakeAccountSession session = NewSession();
        session.SignOutTellsServer = false;
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutCommand.ExecuteAsync(null);

        Assert.False(page.IsSignedIn);
        Assert.Contains("Signed out on this device", page.Status, StringComparison.Ordinal);
        Assert.Contains("did not confirm", page.Status, StringComparison.Ordinal);
        Assert.Contains("may still list it", page.Status, StringComparison.Ordinal);

        // It was not necessarily out of reach: a server that answers 503 or 429 was not told either.
        Assert.DoesNotContain("could not be reached", page.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiFailure.Unavailable)]
    [InlineData(ApiFailure.RateLimited)]
    [InlineData(ApiFailure.Forbidden)]
    public async Task SignOutEverywhere_ThatTheServerRefused_SaysNoSessionEnded_AndStaysSignedIn(ApiFailure failure)
    {
        FakeAccountSession session = NewSession();
        session.SignOutEverywhereThrows = new ApiException(failure);
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutEverywhereCommand.ExecuteAsync(null);

        Assert.True(page.IsSignedIn);
        Assert.Contains("No session was ended", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Every device has to sign in again", page.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiFailure.Network)]
    [InlineData(ApiFailure.Unexpected)]
    public async Task SignOutEverywhere_WithoutAnAnswer_ClaimsNeitherWay_AndStaysSignedIn(ApiFailure failure)
    {
        // The request may have reached the server and ended every session before the answer was lost.
        FakeAccountSession session = NewSession();
        session.SignOutEverywhereThrows = new ApiException(failure);
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutEverywhereCommand.ExecuteAsync(null);

        Assert.True(page.IsSignedIn);
        Assert.Contains("did not confirm", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("No session was ended", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Every device has to sign in again", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutEverywhere_WhoseSessionEndedOnTheWay_SaysTheOthersMayStillBeSignedIn()
    {
        FakeAccountSession session = NewSession();
        session.SignOutEverywhereThrows = new ApiException(ApiFailure.Unauthenticated, 401);
        session.SignOutEverywhereEndsSession = true;
        using AccountPageViewModel page = await SignedInAsync(session);

        await page.SignOutEverywhereCommand.ExecuteAsync(null);

        Assert.False(page.IsSignedIn);
        Assert.Contains("did not confirm", page.Status, StringComparison.Ordinal);
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
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener());

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
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener()) { Email = "alice@example.com", Password = "correct horse battery" };

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

    // ---- two-factor state ----

    [Theory]
    [InlineData(true, "On since", true)]
    [InlineData(false, "Off.", false)]
    public async Task TheTwoFactorState_IsShown_AndDecidesWhetherACodeIsAskedFor(bool enabled, string expected, bool asksForCode)
    {
        FakeAccountSession session = NewSession();
        session.TwoFactor = new TwoFactorStatus(enabled, enabled ? DateTimeOffset.UtcNow.AddDays(-3) : null, enabled ? 9 : 0, false);
        using AccountPageViewModel page = await SignedInAsync(session);

        Assert.Equal(enabled ? TwoFactorState.On : TwoFactorState.Off, page.TwoFactor);
        Assert.Contains(expected, page.TwoFactorSummary, StringComparison.Ordinal);
        Assert.Equal(asksForCode, page.AsksForCode);
    }

    [Fact]
    public async Task TwoFactorNotOfferedByTheServer_IsSaidSo()
    {
        FakeAccountSession session = NewSession();
        session.TwoFactor = null;
        using AccountPageViewModel page = await SignedInAsync(session);

        Assert.Equal(TwoFactorState.NotAvailable, page.TwoFactor);
        Assert.Contains("Not available", page.TwoFactorSummary, StringComparison.Ordinal);
        Assert.False(page.AsksForCode);
    }

    [Fact]
    public async Task ATwoFactorStateThatCannotBeRead_DoesNotFailThePage_AndTheCodeIsStillOffered()
    {
        FakeAccountSession session = NewSession();
        session.TwoFactorThrows = new ApiException(ApiFailure.Network);
        using AccountPageViewModel page = await SignedInAsync(session);

        Assert.Equal("Signed in.", page.Status);
        Assert.Equal(TwoFactorState.Unknown, page.TwoFactor);
        Assert.True(page.AsksForCode);
    }

    // ---- change the account password ----

    private static async Task<AccountPageViewModel> ReadyToChangeAsync(FakeAccountSession session, string next = "a brand new account pw", string? repeat = null)
    {
        AccountPageViewModel page = await SignedInAsync(session);
        page.CurrentAccountPassword = "correct horse battery";
        page.NewAccountPassword = next;
        page.RepeatAccountPassword = repeat ?? next;
        return page;
    }

    [Fact]
    public async Task ChangePassword_NeedsAllThreeFields()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        Assert.False(page.ChangePasswordCommand.CanExecute(null));
        page.CurrentAccountPassword = "x";
        page.NewAccountPassword = "y";
        Assert.False(page.ChangePasswordCommand.CanExecute(null));
        page.RepeatAccountPassword = "y";
        Assert.True(page.ChangePasswordCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("too short", null, "12 to 128")]
    [InlineData("a brand new account pw", "a different repetition", "do not match")]
    public async Task ChangePassword_CatchesObviousMistakes_BeforeAnyRoundTrip(string next, string? repeat, string expected)
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await ReadyToChangeAsync(session, next, repeat);

        await page.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Null(session.LastPasswordChange);
        Assert.Contains(expected, page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, page.CurrentAccountPassword);
        Assert.Equal(string.Empty, page.NewAccountPassword);
    }

    [Fact]
    public async Task ChangePassword_SaysOtherDevicesWereSignedOut_ThisOneStays_AndTheMasterPasswordIsUnchanged()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await ReadyToChangeAsync(session);

        await page.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Equal(("correct horse battery", "a brand new account pw", (string?)null), session.LastPasswordChange);
        Assert.True(page.IsSignedIn);
        Assert.Contains("Every other device and browser was signed out", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.Contains("this device stays signed in", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.Contains("vault master password is unchanged", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, page.CurrentAccountPassword);
        Assert.Equal(string.Empty, page.NewAccountPassword);
        Assert.Equal(string.Empty, page.RepeatAccountPassword);
    }

    [Fact]
    public async Task ChangePassword_WithTwoFactorOn_AsksForTheCode_AndSendsIt()
    {
        FakeAccountSession session = NewSession();
        session.TwoFactor = new TwoFactorStatus(true, null, 8, false);
        using AccountPageViewModel page = await ReadyToChangeAsync(session);

        await page.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Null(session.LastPasswordChange);
        Assert.Contains("two-factor", page.ChangePasswordStatus, StringComparison.Ordinal);

        page.CurrentAccountPassword = "correct horse battery";
        page.NewAccountPassword = page.RepeatAccountPassword = "a brand new account pw";
        page.ChangePasswordCode = " 12345678 ";
        await page.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Equal("12345678", session.LastPasswordChange!.Value.Code);
        Assert.Equal(string.Empty, page.ChangePasswordCode);
    }

    [Fact]
    public async Task ChangePassword_ThatTheServerRefuses_KeepsTheSession_AndSaysNothingChanged()
    {
        FakeAccountSession session = NewSession();
        session.ChangePasswordThrows = new ApiException(ApiFailure.Forbidden, 403);
        using AccountPageViewModel page = await ReadyToChangeAsync(session);

        await page.ChangePasswordCommand.ExecuteAsync(null);

        Assert.True(page.IsSignedIn);
        Assert.Contains("not accepted", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.Contains("still signed in", page.ChangePasswordStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiFailure.Network)]
    [InlineData(ApiFailure.Unexpected)]
    public async Task ChangePassword_WithoutAnAnswer_SaysItMayOrMayNotHaveChanged(ApiFailure failure)
    {
        FakeAccountSession session = NewSession();
        session.ChangePasswordThrows = new ApiException(failure);
        using AccountPageViewModel page = await ReadyToChangeAsync(session);

        await page.ChangePasswordCommand.ExecuteAsync(null);

        Assert.True(page.IsSignedIn);
        Assert.Contains("may or may not have been changed", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", page.ChangePasswordStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("The account password was changed", page.ChangePasswordStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePassword_ThatTheServersPolicyRefuses_SaysThePolicy()
    {
        FakeAccountSession session = NewSession();
        session.ChangePasswordThrows = new ApiException(ApiFailure.InvalidRequest, 400);
        using AccountPageViewModel page = await ReadyToChangeAsync(session);

        await page.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Contains("12 to 128", page.ChangePasswordStatus, StringComparison.Ordinal);
    }

    // ---- delete the account ----

    [Fact]
    public async Task DeleteAccount_NeedsThePasswordAndTheAcknowledgement()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);

        page.DeletePassword = "correct horse battery";
        Assert.False(page.DeleteAccountCommand.CanExecute(null));
        page.DeleteAcknowledged = true;
        Assert.True(page.DeleteAccountCommand.CanExecute(null));
        page.DeletePassword = string.Empty;
        Assert.False(page.DeleteAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteAccount_SignsOut_AndSaysTheVaultOnThisDeviceIsUntouched()
    {
        FakeAccountSession session = NewSession();
        using AccountPageViewModel page = await SignedInAsync(session);
        page.DeletePassword = "correct horse battery";
        page.DeleteAcknowledged = true;

        await page.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Equal(("correct horse battery", (string?)null), session.LastDeleteAccount);
        Assert.False(page.IsSignedIn);
        Assert.Contains("account was deleted", page.Status, StringComparison.Ordinal);
        Assert.Contains("vault on this device was not touched", page.Status, StringComparison.Ordinal);
        Assert.Equal(string.Empty, page.DeletePassword);
        Assert.False(page.DeleteAcknowledged);
    }

    [Fact]
    public async Task DeleteAccount_ThatTheServerRefuses_KeepsEverything()
    {
        FakeAccountSession session = NewSession();
        session.DeleteAccountThrows = new ApiException(ApiFailure.Forbidden, 403);
        using AccountPageViewModel page = await SignedInAsync(session);
        page.DeletePassword = "wrong account password";
        page.DeleteAcknowledged = true;

        await page.DeleteAccountCommand.ExecuteAsync(null);

        Assert.True(page.IsSignedIn);
        Assert.Contains("was not deleted", page.DeleteStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, page.DeletePassword);
    }

    [Theory]
    [InlineData(ApiFailure.Network)]
    [InlineData(ApiFailure.Unexpected)]
    public async Task DeleteAccount_WithoutAnAnswer_SaysItMayOrMayNotHaveBeenDeleted(ApiFailure failure)
    {
        FakeAccountSession session = NewSession();
        session.DeleteAccountThrows = new ApiException(failure);
        using AccountPageViewModel page = await SignedInAsync(session);
        page.DeletePassword = "correct horse battery";
        page.DeleteAcknowledged = true;

        await page.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Contains("may or may not have been deleted", page.DeleteStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("was not deleted", page.DeleteStatus, StringComparison.Ordinal);
        Assert.Contains("vault on this device was not touched", page.DeleteStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void AForbiddenAnswer_ToACallThatSentNoPassword_DoesNotBlameThePassword()
    {
        string said = AccountPageViewModel.Describe(new ApiException(ApiFailure.Forbidden, 403), "sign out");

        Assert.Equal("The server refused the request.", said);
    }

    // ---- forgotten password ----

    [Fact]
    public async Task ForgotPassword_OpensTheProductionResetPage()
    {
        FakeAccountSession session = NewSession();
        session.Server = ApiServer.Production;
        var links = new FakeLinkOpener();
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, links);

        Assert.True(page.IsProductionServer);
        await page.ForgotPasswordCommand.ExecuteAsync(null);

        Assert.Equal([new Uri("https://vault.bitosx.se/forgot-password")], links.Opened);
        Assert.Contains("vault master password is not affected", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_IsNotOffered_ForAnotherServer()
    {
        FakeAccountSession session = NewSession(); // https://api.example.test
        var links = new FakeLinkOpener();
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, links);

        Assert.False(page.IsProductionServer);
        await page.ForgotPasswordCommand.ExecuteAsync(null);

        Assert.Empty(links.Opened);
        Assert.DoesNotContain("vault.bitosx.se", page.TwoFactorNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_WhenTheBrowserCannotBeOpened_SaysWhereToGo()
    {
        FakeAccountSession session = NewSession();
        session.Server = ApiServer.Production;
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), InlineUiDispatcher.Instance, new FakeLinkOpener { Succeeds = false });

        await page.ForgotPasswordCommand.ExecuteAsync(null);

        Assert.Contains("vault.bitosx.se/forgot-password", page.Status, StringComparison.Ordinal);
    }
}
