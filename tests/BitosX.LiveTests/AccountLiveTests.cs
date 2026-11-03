using BitosX.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.LiveTests;

/// <summary>
/// The account, through the application's own client and session, against the real API: sign-in
/// with and without a second factor, the session list, refresh, the password change and deletion.
/// Every test makes its own accounts.
/// </summary>
public sealed class AccountLiveTests
{
    private static void AssertUtc(DateTimeOffset moment, string what)
        => Assert.True(moment.Offset == TimeSpan.Zero, $"{what} is not in UTC: {moment:O}");

    [LiveFact]
    public async Task AVerifiedAccount_SignsIn_WithThePasswordAlone_AndSeesItsProfile()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());

        await device.SignInAsync(email.ToUpperInvariant(), password); // addresses match whatever the case

        AccountProfile profile = await device.Account.GetProfileAsync();
        Assert.Equal(email, profile.Email);
        Assert.True(profile.EmailVerified);
        AssertUtc(profile.CreatedAt, "createdAt");
        Assert.InRange(profile.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(1));

        TwoFactorStatus? twoFactor = await device.Account.GetTwoFactorStatusAsync();
        Assert.NotNull(twoFactor);
        Assert.False(twoFactor.Enabled);

        Assert.True(await device.Account.SignOutAsync());
        Assert.False(device.Account.IsSignedIn);
    }

    [LiveFact]
    public async Task AWrongPassword_IsRefused_AsUnauthenticated()
    {
        (string email, _) = await LiveStack.NewAccountAsync();
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());

        ApiException refused = await Assert.ThrowsAsync<ApiException>(() => device.Account.SignInAsync(email, LiveStack.NewPassword()));

        Assert.Equal(ApiFailure.Unauthenticated, refused.Failure);
        Assert.False(device.Account.IsSignedIn);
    }

    [LiveFact]
    public async Task WithTwoFactor_SignInTakesACode_OrARecoveryCode()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        (Totp authenticator, IReadOnlyList<string> recovery) = await LiveStack.EnableTwoFactorAsync(email, password);
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());

        Assert.Equal(SignInOutcome.SecondFactorRequired, await device.Account.SignInAsync(email, password));
        Assert.False(device.Account.IsSignedIn);
        Assert.True(device.Account.IsAwaitingSecondFactor);

        // A refused code keeps the challenge: the next code completes it.
        ApiException refused = await Assert.ThrowsAsync<ApiException>(() => device.Account.CompleteSignInAsync(authenticator.WrongCode()));
        Assert.Equal(ApiFailure.Unauthenticated, refused.Failure);
        Assert.True(device.Account.IsAwaitingSecondFactor);

        await device.Account.CompleteSignInAsync(await authenticator.NextCodeAsync());
        Assert.True(device.Account.IsSignedIn);
        TwoFactorStatus? status = await device.Account.GetTwoFactorStatusAsync();
        Assert.True(status!.Enabled);
        AssertUtc(status.ConfirmedAt!.Value, "confirmedAt");
        Assert.Equal(10, status.RecoveryCodesRemaining);
        Assert.True(await device.Account.SignOutAsync());

        // A recovery code is accepted wherever a code is, once.
        Assert.Equal(SignInOutcome.SecondFactorRequired, await device.Account.SignInAsync(email, password));
        await device.Account.CompleteSignInAsync(recovery[0]);
        Assert.True(device.Account.IsSignedIn);
        Assert.Equal(9, (await device.Account.GetTwoFactorStatusAsync())!.RecoveryCodesRemaining);
        Assert.True(await device.Account.SignOutAsync());

        Assert.Equal(SignInOutcome.SecondFactorRequired, await device.Account.SignInAsync(email, password));
        ApiException reused = await Assert.ThrowsAsync<ApiException>(() => device.Account.CompleteSignInAsync(recovery[0]));
        Assert.Equal(ApiFailure.Unauthenticated, reused.Failure);
        Assert.False(device.Account.IsSignedIn);
    }

    [LiveFact]
    public async Task TheSessionList_HoldsAtMostTheServersCap_NewestFirst_WithThisSessionMarked()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());

        // More sessions than the server lists (200), opened through the application's client. One at a
        // time: the server hashes passwords in a bounded queue and answers 503 when it is full.
        const int Others = 204;
        for (int i = 0; i < Others; i++)
        {
            Assert.NotNull((await device.Api.LoginAsync(email, password)).Tokens);
        }

        await device.SignInAsync(email, password); // the newest, so it is listed

        IReadOnlyList<AccountSessionInfo> sessions = await device.Account.GetSessionsAsync();

        Assert.Equal(200, sessions.Count);
        Assert.Single(sessions, s => s.Current);
        Assert.True(sessions[0].Current);
        Assert.Equal(sessions.Count, sessions.Select(s => s.Id).Distinct().Count());
        Assert.Equal(sessions.OrderByDescending(s => s.CreatedAt).Select(s => s.Id), sessions.Select(s => s.Id));
        Assert.All(sessions, s =>
        {
            AssertUtc(s.CreatedAt, "createdAt");
            AssertUtc(s.LastSeenAt, "lastSeenAt");
            AssertUtc(s.ExpiresAt, "expiresAt");
            Assert.True(s.ExpiresAt > DateTimeOffset.UtcNow);
        });

        // Ending another one: it is gone from the list, this one stays.
        AccountSessionInfo another = sessions[1];
        await device.Account.RevokeSessionAsync(another.Id);
        Assert.True(device.Account.IsSignedIn);
        Assert.DoesNotContain(await device.Account.GetSessionsAsync(), s => s.Id == another.Id);

        await device.Account.SignOutEverywhereAsync();
        Assert.False(device.Account.IsSignedIn);
    }

    [LiveFact]
    public async Task ARefresh_RenewsTheTokens_AndARefreshTokenPresentedTwice_EndsTheSession()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        using var device = new Device();

        AccountTokens first = (await device.Api.LoginAsync(email, password)).Tokens!;
        AssertUtc(first.AccessTokenExpiresAt, "accessTokenExpiresAt");
        AssertUtc(first.RefreshTokenExpiresAt, "refreshTokenExpiresAt");

        AccountTokens second = await device.Api.RefreshAsync(first.RefreshToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(email, (await device.Api.GetProfileAsync(second.AccessToken)).Email);

        // The server takes a spent refresh token for a stolen one and ends the whole session.
        ApiException replay = await Assert.ThrowsAsync<ApiException>(() => device.Api.RefreshAsync(first.RefreshToken));
        Assert.Equal(ApiFailure.Unauthenticated, replay.Failure);
        ApiException ended = await Assert.ThrowsAsync<ApiException>(() => device.Api.GetProfileAsync(second.AccessToken));
        Assert.Equal(ApiFailure.Unauthenticated, ended.Failure);
    }

    [LiveFact]
    public async Task TheSession_RefreshesBeforeTheAccessTokenRunsOut_AndCarriesOn()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        var clock = new MovableClock();
        using var device = new Device(clock);
        await device.CreateVaultAsync(LiveStack.NewPassword());
        await device.SignInAsync(email, password);
        Guid session = (await device.Account.GetSessionsAsync()).Single(s => s.Current).Id;

        // As the session sees it, the access token is about to run out: it is refreshed before use.
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(email, (await device.Account.GetProfileAsync()).Email);
        clock.Advance(TimeSpan.FromMinutes(20));
        IReadOnlyList<AccountSessionInfo> sessions = await device.Account.GetSessionsAsync();

        Assert.True(device.Account.IsSignedIn);
        Assert.Equal(session, sessions.Single(s => s.Current).Id); // the same session, renewed
    }

    [LiveFact]
    public async Task ChangingThePassword_KeepsThisSession_EndsTheOthers_AndRetiresTheOldPassword()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        using var device = new Device();
        using var other = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());
        await device.SignInAsync(email, password);
        AccountTokens elsewhere = (await other.Api.LoginAsync(email, password)).Tokens!;
        string next = LiveStack.NewPassword();

        await device.Account.ChangePasswordAsync(password, next, null);

        Assert.True(device.Account.IsSignedIn);
        Assert.Equal(email, (await device.Account.GetProfileAsync()).Email);
        ApiException signedOut = await Assert.ThrowsAsync<ApiException>(() => other.Api.GetProfileAsync(elsewhere.AccessToken));
        Assert.Equal(ApiFailure.Unauthenticated, signedOut.Failure);
        ApiException old = await Assert.ThrowsAsync<ApiException>(() => other.Api.LoginAsync(email, password));
        Assert.Equal(ApiFailure.Unauthenticated, old.Failure);
        Assert.NotNull((await other.Api.LoginAsync(email, next)).Tokens);
    }

    [LiveFact]
    public async Task AWrongPasswordOrCode_IsForbidden_AndTheSessionStays()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        (Totp authenticator, IReadOnlyList<string> recovery) = await LiveStack.EnableTwoFactorAsync(email, password);
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());
        Assert.Equal(SignInOutcome.SecondFactorRequired, await device.Account.SignInAsync(email, password));
        await device.Account.CompleteSignInAsync(await authenticator.NextCodeAsync());

        ApiException wrongPassword = await Assert.ThrowsAsync<ApiException>(
            () => device.Account.ChangePasswordAsync(LiveStack.NewPassword(), LiveStack.NewPassword(), recovery[0]));
        ApiException noCode = await Assert.ThrowsAsync<ApiException>(
            () => device.Account.ChangePasswordAsync(password, LiveStack.NewPassword(), null));
        ApiException wrongCode = await Assert.ThrowsAsync<ApiException>(
            () => device.Account.DeleteAccountAsync(password, authenticator.WrongCode()));

        Assert.All([wrongPassword, noCode, wrongCode], e => Assert.Equal(ApiFailure.Forbidden, e.Failure));
        Assert.Equal(403, wrongPassword.StatusCode);
        Assert.True(device.Account.IsSignedIn);
        Assert.Equal(email, (await device.Account.GetProfileAsync()).Email);

        // The password is checked first, so the wrong one did not use up the recovery code.
        Assert.Equal(10, (await device.Account.GetTwoFactorStatusAsync())!.RecoveryCodesRemaining);
    }

    [LiveFact]
    public async Task DeletingTheAccount_WithACode_SignsOut_AndTheAccountIsGone()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        (Totp authenticator, _) = await LiveStack.EnableTwoFactorAsync(email, password);
        using var device = new Device();
        await device.CreateVaultAsync(LiveStack.NewPassword());
        Assert.Equal(SignInOutcome.SecondFactorRequired, await device.Account.SignInAsync(email, password));
        await device.Account.CompleteSignInAsync(await authenticator.NextCodeAsync());
        await device.AddLoginAsync("kept on this device");
        await device.Sync.SyncAsync();

        await device.Account.DeleteAccountAsync(password, await authenticator.NextCodeAsync());

        Assert.False(device.Account.IsSignedIn);
        ApiException gone = await Assert.ThrowsAsync<ApiException>(() => device.Account.SignInAsync(email, password));
        Assert.Equal(ApiFailure.Unauthenticated, gone.Failure);

        // The vault on this device is not the account's: it is still here, unlocked, with its item.
        Assert.Contains(device.Vault.Items, i => i.Name == "kept on this device");
    }

    [LiveFact]
    public async Task TheApplicationsClient_ReachesTheStack()
    {
        using var client = HttpBitosXApiClient.Create(ApiServer.Parse(LiveStack.Api.ToString()), "0.0.0-live", NullLogger<HttpBitosXApiClient>.Instance);
        Assert.True(await client.PingAsync());
    }
}

/// <summary>The wall clock, moved forward on request: for what the client decides by the time, never for the server.</summary>
internal sealed class MovableClock : TimeProvider
{
    private TimeSpan _offset;

    public void Advance(TimeSpan by) => _offset += by;

    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + _offset;
}
