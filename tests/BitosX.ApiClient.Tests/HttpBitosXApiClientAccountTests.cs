using System.Diagnostics;
using System.Net;
using BitosX.ApiClient;
using Microsoft.Extensions.Logging;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// The account endpoints added with the password change and two-factor status, and the transport
/// guarantees they rely on: a deadline that covers the body, 403 as its own failure, the session list's
/// own cap, timestamps without an offset read as UTC, and no id or secret in the log.
/// </summary>
public sealed class HttpBitosXApiClientAccountTests
{
    private const string Access = "jwt.header.payload.signature";

    // ---- change password / delete account / two-factor status ----

    [Fact]
    public async Task ChangePassword_PostsBothPasswords_AndTheCodeOnlyWhenGiven()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.NoContent);
        server.Reply(HttpStatusCode.NoContent);
        using HttpBitosXApiClient client = server.NewClient();

        await client.ChangePasswordAsync(Access, "old account password", "new account password", null, default);
        await client.ChangePasswordAsync(Access, "old account password", "new account password", "123456", default);

        Assert.All(server.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal(new Uri("https://api.example.test/v1/account/password"), r.Uri);
            Assert.Equal(Access, r.Authorization!.Parameter);
        });
        Assert.Equal("{\"currentPassword\":\"old account password\",\"newPassword\":\"new account password\"}", server.Requests[0].Body);
        Assert.Equal("{\"currentPassword\":\"old account password\",\"newPassword\":\"new account password\",\"code\":\"123456\"}", server.Requests[1].Body);
    }

    [Fact]
    public async Task DeleteAccount_PostsThePassword_AndTheCodeOnlyWhenGiven()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.NoContent);
        server.Reply(HttpStatusCode.NoContent);
        using HttpBitosXApiClient client = server.NewClient();

        await client.DeleteAccountAsync(Access, "account password", "", default);
        await client.DeleteAccountAsync(Access, "account password", "abcde-fghjk", default);

        Assert.Equal(new Uri("https://api.example.test/v1/account/delete"), server.Requests[0].Uri);
        Assert.Equal("{\"password\":\"account password\"}", server.Requests[0].Body);
        Assert.Equal("{\"password\":\"account password\",\"code\":\"abcde-fghjk\"}", server.Requests[1].Body);
    }

    [Fact]
    public async Task A403_IsForbidden_NotASignOut_AndQuotesNothing()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.Forbidden, "{\"detail\":\"Password confirmation failed. SECRET\"}");
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(
            () => client.ChangePasswordAsync(Access, "wrong", "new account password", null, default));

        Assert.Equal(ApiFailure.Forbidden, ex.Failure);
        Assert.Equal(403, ex.StatusCode);
        Assert.DoesNotContain("SECRET", ex.Message, StringComparison.Ordinal);
        Assert.Equal("The server refused the request.", ex.Message); // what was refused is for the caller to say
    }

    [Fact]
    public async Task TwoFactorStatus_IsRead()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"enabled\":true,\"confirmedAt\":\"2026-10-01T08:00:00Z\",\"recoveryCodesRemaining\":7,\"pendingEnrollment\":false}");
        using HttpBitosXApiClient client = server.NewClient();

        TwoFactorStatus? status = await client.GetTwoFactorStatusAsync(Access, default);

        Assert.Equal(new TwoFactorStatus(true, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), 7, false), status);
        Assert.Equal(new Uri("https://api.example.test/v1/account/totp"), Assert.Single(server.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task TwoFactorStatus_IsNull_WhenTheServerDoesNotOfferIt(HttpStatusCode status)
    {
        using var server = new FakeServer();
        server.Reply(status);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Null(await client.GetTwoFactorStatusAsync(Access, default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":true,\"recoveryCodesRemaining\":-1,\"pendingEnrollment\":false}")]
    [InlineData("{\"enabled\":\"yes\",\"recoveryCodesRemaining\":1,\"pendingEnrollment\":false}")]
    public async Task AMalformedTwoFactorStatus_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json);
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetTwoFactorStatusAsync(Access, default));
        Assert.Equal(ApiFailure.Unexpected, ex.Failure);
    }

    // ---- passkeys (read-only) ----

    [Fact]
    public async Task Passkeys_AreListed_WithoutAnyKeyMaterial()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK,
            "{\"passkeys\":[{\"id\":\"6f1c2e4a-1b2c-4d3e-8f90-123456789abc\",\"name\":\"Laptop\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"lastUsedAt\":\"2026-11-01T18:15:00Z\",\"backedUp\":true},"
            + "{\"id\":\"0e9d8c7b-6a5f-4e3d-9c2b-1a0f9e8d7c6b\",\"name\":\"Security key\",\"createdAt\":\"2026-10-31T09:00:00Z\",\"backedUp\":false,\"somethingNew\":1}],\"max\":10}");
        using HttpBitosXApiClient client = server.NewClient();

        PasskeyList? list = await client.GetPasskeysAsync(Access, default);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.example.test/v1/account/passkeys"), request.Uri);
        Assert.Equal(Access, request.Authorization!.Parameter);
        Assert.NotNull(list);
        Assert.Equal(10, list.Max);
        Assert.Equal(2, list.Passkeys.Count);
        Assert.Equal(
            new PasskeyInfo("6f1c2e4a-1b2c-4d3e-8f90-123456789abc", "Laptop", new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 18, 15, 0, TimeSpan.Zero), true),
            list.Passkeys[0]);
        Assert.Null(list.Passkeys[1].LastUsedAt); // never used yet
        Assert.False(list.Passkeys[1].BackedUp);
    }

    [Fact]
    public async Task NoPasskeys_IsAnEmptyList()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"passkeys\":[],\"max\":10}");
        using HttpBitosXApiClient client = server.NewClient();

        PasskeyList? list = await client.GetPasskeysAsync(Access, default);

        Assert.NotNull(list);
        Assert.Empty(list.Passkeys);
    }

    [Fact]
    public async Task Passkeys_AreNull_WhenTheServerDoesNotOfferThem()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.NotFound);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Null(await client.GetPasskeysAsync(Access, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiFailure.Unavailable)] // a server that has passkeys but cannot answer now: not "none"
    [InlineData(HttpStatusCode.Unauthorized, ApiFailure.Unauthenticated)]
    [InlineData(HttpStatusCode.Forbidden, ApiFailure.Forbidden)]
    public async Task OtherFailuresOfThePasskeyList_AreFailures(HttpStatusCode status, ApiFailure expected)
    {
        using var server = new FakeServer();
        server.Reply(status);
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPasskeysAsync(Access, default));
        Assert.Equal(expected, ex.Failure);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"passkeys\":[]}")]                                                                                                   // no max
    [InlineData("{\"max\":10}")]                                                                                                        // no list
    [InlineData("{\"passkeys\":[],\"max\":-1}")]
    [InlineData("{\"passkeys\":[null],\"max\":10}")]
    [InlineData("{\"passkeys\":[{\"id\":\"a\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":true}],\"max\":10}")]               // no name
    [InlineData("{\"passkeys\":[{\"id\":\"a\",\"name\":\"\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":true}],\"max\":10}")]   // empty name
    [InlineData("{\"passkeys\":[{\"name\":\"Laptop\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":true}],\"max\":10}")]          // no id
    [InlineData("{\"passkeys\":[{\"id\":\"a\",\"name\":\"Laptop\",\"backedUp\":true}],\"max\":10}")]                                  // no createdAt
    [InlineData("{\"passkeys\":[{\"id\":\"a\",\"name\":\"Laptop\",\"createdAt\":\"2026-10-30T09:00:00Z\"}],\"max\":10}")]            // no backedUp
    [InlineData("{\"passkeys\":[{\"id\":\"a\",\"name\":\"Laptop\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":\"yes\"}],\"max\":10}")]
    [InlineData("[]")]
    public async Task AMalformedPasskeyList_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json);
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPasskeysAsync(Access, default));
        Assert.Equal(ApiFailure.Unexpected, ex.Failure);
    }

    [Fact]
    public async Task ThePasskeyList_HasTheOrdinaryCap_AndALimitOnItsLength()
    {
        string one = "{\"id\":\"a\",\"name\":\"Laptop\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":true}";
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"passkeys\":[],\"max\":10,\"padding\":\"" + new string('p', HttpBitosXApiClient.MaxResponseBytes) + "\"}");
        server.Reply(HttpStatusCode.OK, "{\"passkeys\":[" + string.Join(",", Enumerable.Repeat(one, 101)) + "],\"max\":10}");
        server.Reply(HttpStatusCode.OK, "{\"passkeys\":[{\"id\":\"a\",\"name\":\"" + new string('n', 129) + "\",\"createdAt\":\"2026-10-30T09:00:00Z\",\"backedUp\":true}],\"max\":10}");
        using HttpBitosXApiClient client = server.NewClient();

        for (int i = 0; i < 3; i++)
        {
            ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPasskeysAsync(Access, default));
            Assert.Equal(ApiFailure.Unexpected, ex.Failure);
        }
    }

    [Fact]
    public async Task ThePasskeyList_HasTheOrdinaryDeadline()
    {
        using var server = new FakeServer();
        server.ReplyWithABodyThatNeverArrives();
        using HttpBitosXApiClient client = server.NewClient(callTimeout: TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPasskeysAsync(Access, default));

        Assert.Equal(ApiFailure.Network, ex.Failure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the deadline covers the body");
    }

    // ---- timestamps ----

    [Fact]
    public async Task ATimestampWithoutAnOffset_IsReadAsUtc_AndOneWithAnOffsetKeepsIt()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK,
            "[{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"createdAt\":\"2026-10-20T08:00:00\",\"lastSeenAt\":\"2026-10-20T10:30:00+02:00\",\"expiresAt\":\"2026-11-19T08:30:00.1234567Z\",\"current\":true}]");
        using HttpBitosXApiClient client = server.NewClient();

        AccountSessionInfo session = Assert.Single(await client.GetSessionsAsync(Access, default));

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), session.CreatedAt);
        Assert.Equal(TimeSpan.Zero, session.CreatedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), session.LastSeenAt.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 8, 30, 0, TimeSpan.Zero).AddTicks(1234567), session.ExpiresAt);
    }

    [Fact]
    public async Task ATimestampThatIsNotIso8601_IsUnexpected()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK,
            "[{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"createdAt\":\"20/10/2026\",\"lastSeenAt\":\"2026-10-20T10:30:00Z\",\"expiresAt\":\"2026-11-19T08:30:00Z\",\"current\":true}]");
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetSessionsAsync(Access, default));
        Assert.Equal(ApiFailure.Unexpected, ex.Failure);
    }

    // ---- the session list's cap ----

    [Fact]
    public async Task TheSessionList_ReadsTheTwoHundredSessionsTheServerMayList_BeyondTheOrdinaryCap()
    {
        string one = "{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"createdAt\":\"2026-10-20T08:00:00.1234567Z\",\"lastSeenAt\":\"2026-10-20T08:30:00.1234567Z\",\"expiresAt\":\"2026-11-19T08:30:00.1234567Z\",\"current\":false,\"padding\":\"" + new string('p', 200) + "\"}";
        string list = "[" + string.Join(",", Enumerable.Repeat(one, 200)) + "]";
        Assert.True(list.Length > HttpBitosXApiClient.MaxResponseBytes);

        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, list);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(200, (await client.GetSessionsAsync(Access, default)).Count);
    }

    [Fact]
    public async Task TheSessionList_StillHasACap()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "[" + new string(' ', HttpBitosXApiClient.MaxSessionsResponseBytes) + "]");
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetSessionsAsync(Access, default));
        Assert.Equal(ApiFailure.Unexpected, ex.Failure);
    }

    // ---- deadlines ----

    [Fact]
    public async Task ABodyThatStopsArriving_EndsAsANetworkFailure_AtTheDeadline()
    {
        using var server = new FakeServer();
        server.ReplyWithABodyThatNeverArrives();
        using HttpBitosXApiClient client = server.NewClient(callTimeout: TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetProfileAsync(Access, default));

        Assert.Equal(ApiFailure.Network, ex.Failure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the deadline covers the body");
    }

    [Fact]
    public async Task HeadersThatNeverArrive_EndAsANetworkFailure_AtTheDeadline()
    {
        using var server = new FakeServer { StallHeaders = true };
        server.Reply(HttpStatusCode.OK, "{}");
        using HttpBitosXApiClient client = server.NewClient(callTimeout: TimeSpan.FromMilliseconds(300));

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.RefreshAsync("r3fr3sh", default));
        Assert.Equal(ApiFailure.Network, ex.Failure);
    }

    [Fact]
    public async Task AnEnvelopeTransfer_HasTheLongerDeadline()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, null, r =>
        {
            // Answers only after the ordinary deadline has passed: the envelope's own deadline is longer.
            Thread.Sleep(600);
            r.Content = new StringContent("{\"revision\":1,\"fingerprint\":\"" + HttpBitosXApiClient.Fingerprint([1, 2, 3]) + "\",\"sizeBytes\":3,\"updatedAt\":\"2026-10-20T08:00:00Z\",\"envelope\":\"AQID\"}", System.Text.Encoding.UTF8, "application/json");
        });
        using HttpBitosXApiClient client = server.NewClient(callTimeout: TimeSpan.FromMilliseconds(200), envelopeTimeout: TimeSpan.FromSeconds(30));

        SyncEnvelope? envelope = await client.DownloadEnvelopeAsync(Access, default);

        Assert.Equal(new byte[] { 1, 2, 3 }, envelope!.Envelope);
    }

    [Fact]
    public async Task TheCallersCancellation_IsStillTheCallers()
    {
        using var server = new FakeServer();
        server.ReplyWithABodyThatNeverArrives();
        using HttpBitosXApiClient client = server.NewClient(callTimeout: TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetProfileAsync(Access, cancel.Token));
    }

    // ---- what is logged ----

    [Fact]
    public async Task AFailedRevoke_LogsTheEndpointTemplate_NeverTheSessionId()
    {
        var log = new RecordingLogger();
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.NotFound);
        using HttpBitosXApiClient client = server.NewClient(logger: log);
        var id = Guid.Parse("3f25aaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        await Assert.ThrowsAsync<ApiException>(() => client.RevokeSessionAsync(Access, id, default));

        string line = Assert.Single(log.Lines);
        Assert.Contains("v1/account/sessions/{id}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("3f25", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Access, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedPasswordChange_LogsNoPassword()
    {
        var log = new RecordingLogger();
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.Forbidden);
        using HttpBitosXApiClient client = server.NewClient(logger: log);

        await Assert.ThrowsAsync<ApiException>(() => client.ChangePasswordAsync(Access, "old-secret-pw", "new-secret-pw", "123456", default));

        string line = Assert.Single(log.Lines);
        Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", line, StringComparison.Ordinal);
    }

    // ---- secrets in ToString ----

    [Fact]
    public void Tokens_AndSignInResults_NeverPrintTheirSecrets()
    {
        var tokens = new AccountTokens("ACCESS-SECRET", DateTimeOffset.UtcNow, "REFRESH-SECRET", DateTimeOffset.UtcNow);
        var challenge = new SecondFactorChallenge("MFA-SECRET", DateTimeOffset.UtcNow);

        foreach (string printed in new[] { tokens.ToString(), SignInResult.SignedIn(tokens).ToString(), SignInResult.SecondFactor(challenge).ToString() })
        {
            Assert.DoesNotContain("SECRET", printed, StringComparison.Ordinal);
            Assert.Contains("redacted", printed, StringComparison.Ordinal);
        }
    }

    private sealed class RecordingLogger : ILogger<HttpBitosXApiClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + " " + string.Join(";", (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(kv => kv.Key + "=" + kv.Value) ?? []));
    }
}
