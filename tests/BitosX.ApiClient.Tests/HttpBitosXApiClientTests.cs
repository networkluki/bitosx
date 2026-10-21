using System.Net;
using System.Net.Http.Headers;
using BitosX.ApiClient;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// The HTTPS client against a scripted server: what it sends, what it accepts, and how every way
/// the server can answer badly becomes an <see cref="ApiException"/> that says nothing the server said.
/// </summary>
public sealed class HttpBitosXApiClientTests
{
    private const string Access = "jwt.header.payload.signature";

    private static async Task<ApiException> FailsAsync(Func<Task> call)
        => await Assert.ThrowsAsync<ApiException>(call);

    // ---- what is sent ----

    [Fact]
    public async Task Login_PostsEmailAndPasswordAsJson_WithNoAuthorization()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, FakeServer.Tokens());
        using HttpBitosXApiClient client = server.NewClient();

        AccountTokens tokens = await client.LoginAsync("alice@example.com", "correct horse battery", default);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://api.example.test/v1/auth/login"), request.Uri);
        Assert.Equal("{\"email\":\"alice@example.com\",\"password\":\"correct horse battery\"}", request.Body);
        Assert.Null(request.Authorization);
        Assert.StartsWith("BitosX/9.9.9", request.UserAgent, StringComparison.Ordinal);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("jwt.access", tokens.AccessToken);
        Assert.Equal("r3fr3sh", tokens.RefreshToken);
        Assert.True(tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(tokens.RefreshTokenExpiresAt > tokens.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task AuthenticatedCalls_CarryTheBearerToken_AndNothingElseSecret()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"accountId\":\"11111111-2222-4333-8444-555555555555\",\"email\":\"alice@example.com\",\"emailVerified\":true,\"createdAt\":\"2026-09-01T10:00:00Z\"}");
        server.Reply(HttpStatusCode.OK, "[]");
        server.Reply(HttpStatusCode.NoContent);
        server.Reply(HttpStatusCode.NoContent);
        server.Reply(HttpStatusCode.NoContent);
        using HttpBitosXApiClient client = server.NewClient();

        AccountProfile profile = await client.GetProfileAsync(Access, default);
        IReadOnlyList<AccountSessionInfo> sessions = await client.GetSessionsAsync(Access, default);
        await client.RevokeSessionAsync(Access, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), default);
        await client.LogoutAsync(Access, default);
        await client.LogoutAllAsync(Access, default);

        Assert.Equal("alice@example.com", profile.Email);
        Assert.True(profile.EmailVerified);
        Assert.Empty(sessions);
        Assert.Equal(5, server.Requests.Count);
        Assert.All(server.Requests, r =>
        {
            Assert.Equal("Bearer", r.Authorization!.Scheme);
            Assert.Equal(Access, r.Authorization.Parameter);
        });
        Assert.Equal(new Uri("https://api.example.test/v1/account/me"), server.Requests[0].Uri);
        Assert.Equal(new Uri("https://api.example.test/v1/account/sessions"), server.Requests[1].Uri);
        Assert.Equal(HttpMethod.Delete, server.Requests[2].Method);
        Assert.Equal(new Uri("https://api.example.test/v1/account/sessions/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), server.Requests[2].Uri);
        Assert.Equal(new Uri("https://api.example.test/v1/auth/logout"), server.Requests[3].Uri);
        Assert.Equal(new Uri("https://api.example.test/v1/auth/logout-all"), server.Requests[4].Uri);
        Assert.All(server.Requests.Skip(2), r => Assert.Equal(string.Empty, r.Body));
    }

    [Fact]
    public async Task Refresh_PostsOnlyTheRefreshToken()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, FakeServer.Tokens(access: "jwt.next", refresh: "n3xt"));
        using HttpBitosXApiClient client = server.NewClient();

        AccountTokens tokens = await client.RefreshAsync("r3fr3sh", default);

        Assert.Equal("{\"refreshToken\":\"r3fr3sh\"}", Assert.Single(server.Requests).Body);
        Assert.Equal("jwt.next", tokens.AccessToken);
        Assert.Equal("n3xt", tokens.RefreshToken);
    }

    [Fact]
    public async Task Sessions_AreParsed_AndTheCurrentOneMarked()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK,
            "[{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"createdAt\":\"2026-10-20T08:00:00Z\",\"lastSeenAt\":\"2026-10-20T08:30:00Z\",\"expiresAt\":\"2026-11-19T08:30:00Z\",\"current\":true},"
            + "{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-ffffffffffff\",\"createdAt\":\"2026-10-01T08:00:00Z\",\"lastSeenAt\":\"2026-10-18T12:00:00Z\",\"expiresAt\":\"2026-11-17T12:00:00Z\",\"current\":false}]");
        using HttpBitosXApiClient client = server.NewClient();

        IReadOnlyList<AccountSessionInfo> sessions = await client.GetSessionsAsync(Access, default);

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].Current);
        Assert.False(sessions[1].Current);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), sessions[0].LastSeenAt);
    }

    [Fact]
    public async Task Ping_IsTrueOnlyForAHealthyAnswer()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"service\":\"BitosX API\",\"status\":\"healthy\"}");
        server.Reply(HttpStatusCode.ServiceUnavailable);
        server.Fail(new HttpRequestException("refused"));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.True(await client.PingAsync());
        Assert.False(await client.PingAsync());
        Assert.False(await client.PingAsync());
    }

    // ---- how the server's answers are judged ----

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ApiFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ApiFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.Unauthorized, ApiFailure.Unauthenticated)]
    [InlineData(HttpStatusCode.NotFound, ApiFailure.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiFailure.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, ApiFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ApiFailure.Unexpected)]
    [InlineData(HttpStatusCode.Found, ApiFailure.Unexpected)]
    [InlineData(HttpStatusCode.Forbidden, ApiFailure.Unexpected)]
    public async Task Status_DecidesTheFailure_AndTheBodyIsNeverQuoted(HttpStatusCode status, ApiFailure expected)
    {
        using var server = new FakeServer();
        server.Reply(status, "{\"type\":\"about:blank\",\"title\":\"Nope\",\"detail\":\"SECRET-DETAIL-FROM-SERVER\"}",
            r => r.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json"));
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await FailsAsync(() => client.LoginAsync("a@b.c", "x", default));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.DoesNotContain("SECRET", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimited_CarriesRetryAfter_WhenTheServerSaysSo()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.TooManyRequests, customize: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        server.Reply(HttpStatusCode.TooManyRequests);
        using HttpBitosXApiClient client = server.NewClient();

        ApiException withHint = await FailsAsync(() => client.LoginAsync("a@b.c", "x", default));
        ApiException without = await FailsAsync(() => client.LoginAsync("a@b.c", "x", default));

        Assert.Equal(TimeSpan.FromSeconds(30), withHint.RetryAfter);
        Assert.Null(without.RetryAfter);
    }

    [Fact]
    public async Task NetworkTrouble_IsNetwork_NotACredentialFailure()
    {
        using var server = new FakeServer();
        server.Fail(new HttpRequestException("connection refused"));
        server.Fail(new TaskCanceledException("timeout", new TimeoutException()));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Network, (await FailsAsync(() => client.LoginAsync("a@b.c", "x", default))).Failure);
        Assert.Equal(ApiFailure.Network, (await FailsAsync(() => client.LoginAsync("a@b.c", "x", default))).Failure);
    }

    [Fact]
    public async Task TheCallersOwnCancellation_IsNotTurnedIntoAFailure()
    {
        using var server = new FakeServer();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        server.Fail(new OperationCanceledException(cancelled.Token));
        using HttpBitosXApiClient client = server.NewClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoginAsync("a@b.c", "x", cancelled.Token));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"accessToken\":\"a\"}")]                                                                   // fields missing
    [InlineData("{\"accessToken\":\"\",\"accessTokenExpiresAt\":\"2030-01-01T00:00:00Z\",\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":\"2030-02-01T00:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"a\",\"accessTokenExpiresAt\":\"yesterday\",\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":\"2030-02-01T00:00:00Z\"}")]
    [InlineData("{\"accessToken\":\"a\",\"accessTokenExpiresAt\":\"2030-01-01T00:00:00Z\",\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":\"2030-02-01T00:00:00Z\",\"tokenType\":\"Basic\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task AMalformedTokenAnswer_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.LoginAsync("a@b.c", "x", default))).Failure);
    }

    [Fact]
    public async Task ANonJsonContentType_IsUnexpected_EvenWithStatus200()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, FakeServer.Tokens(), r => r.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/html"));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.LoginAsync("a@b.c", "x", default))).Failure);
    }

    [Fact]
    public async Task ABodyBeyondTheCap_IsUnexpected()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"padding\":\"" + new string('x', HttpBitosXApiClient.MaxResponseBytes + 1) + "\"}");
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetProfileAsync(Access, default))).Failure);
    }

    [Theory]
    [InlineData("{\"accountId\":\"not-a-guid\",\"email\":\"a@b.c\",\"emailVerified\":true,\"createdAt\":\"2026-09-01T10:00:00Z\"}")]
    [InlineData("{\"accountId\":\"11111111-2222-4333-8444-555555555555\",\"email\":\"\",\"emailVerified\":true,\"createdAt\":\"2026-09-01T10:00:00Z\"}")]
    [InlineData("{\"accountId\":\"11111111-2222-4333-8444-555555555555\",\"email\":\"a@b.c\",\"emailVerified\":\"yes\",\"createdAt\":\"2026-09-01T10:00:00Z\"}")]
    [InlineData("{\"accountId\":\"11111111-2222-4333-8444-555555555555\",\"email\":\"a@b.c\",\"emailVerified\":true}")]
    public async Task AMalformedProfile_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetProfileAsync(Access, default))).Failure);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"createdAt\":\"2026-10-20T08:00:00Z\"}]")]
    [InlineData("[null]")]
    public async Task AMalformedSessionList_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json);
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetSessionsAsync(Access, default))).Failure);
    }

    [Fact]
    public async Task UnknownFields_AreIgnored_SoANewerServerStillWorks()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, FakeServer.Tokens().Replace("}", ",\"somethingNew\":123}", StringComparison.Ordinal));
        using HttpBitosXApiClient client = server.NewClient();

        AccountTokens tokens = await client.LoginAsync("a@b.c", "x", default);

        Assert.Equal("jwt.access", tokens.AccessToken);
    }

    [Fact]
    public async Task AfterDispose_NothingIsSent()
    {
        using var server = new FakeServer();
        HttpBitosXApiClient client = server.NewClient();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.LoginAsync("a@b.c", "x", default));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void TheApplicationClient_IsSetUpTheSafeWay()
    {
        using HttpBitosXApiClient client = HttpBitosXApiClient.Create(
            FakeServer.Address, "1.2.3", Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpBitosXApiClient>.Instance);

        Assert.True(client.IsServerIntegrationEnabled);
        Assert.Same(FakeServer.Address, client.Server);
    }

    [Fact]
    public async Task TheLocalOnlyClient_MakesNoCalls()
    {
        var client = new LocalOnlyApiClient();

        Assert.False(client.IsServerIntegrationEnabled);
        Assert.Null(client.Server);
        Assert.False(await client.PingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync("a@b.c", "x", default));
    }

    [Fact]
    public async Task ABodyThatIsNotUtf8_IsUnexpected()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, customize: r =>
        {
            r.Content = new ByteArrayContent([0xC3, 0x28, 0x7B, 0x7D]);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        });
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetProfileAsync(Access, default))).Failure);
    }
}
