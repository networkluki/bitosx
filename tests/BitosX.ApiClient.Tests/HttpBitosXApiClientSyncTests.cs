using System.Net;
using System.Security.Cryptography;
using System.Text;
using BitosX.ApiClient;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// The sync calls against a scripted server: what goes up, what is accepted coming down, and that
/// the server's own statements about the envelope (size, fingerprint, revision) are checked against
/// the bytes rather than believed.
/// </summary>
public sealed class HttpBitosXApiClientSyncTests
{
    private const string Access = "jwt.header.payload.signature";
    private static readonly byte[] Envelope = Encoding.UTF8.GetBytes("{\"version\":1,\"ciphertext\":\"AAAA\"}");
    private static readonly string Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Envelope));

    private static string StatusJson(long revision = 3, string? fingerprint = null, int? size = null)
        => "{\"revision\":" + revision + ",\"fingerprint\":\"" + (fingerprint ?? Fingerprint) + "\",\"sizeBytes\":" + (size ?? Envelope.Length) + ",\"updatedAt\":\"2026-10-20T12:00:00Z\"}";

    private static string EnvelopeJson(long revision = 3, string? fingerprint = null, int? size = null, string? envelope = null)
        => StatusJson(revision, fingerprint, size)[..^1] + ",\"envelope\":\"" + (envelope ?? Convert.ToBase64String(Envelope)) + "\"}";

    private static async Task<ApiException> FailsAsync(Func<Task> call) => await Assert.ThrowsAsync<ApiException>(call);

    [Fact]
    public async Task Status_IsRead_AndNothingIsNull()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, StatusJson());
        server.Reply(HttpStatusCode.NotFound);
        using HttpBitosXApiClient client = server.NewClient();

        SyncStatus? status = await client.GetSyncStatusAsync(Access, default);
        SyncStatus? none = await client.GetSyncStatusAsync(Access, default);

        Assert.NotNull(status);
        Assert.Equal(3, status!.Revision);
        Assert.Equal(Fingerprint, status.Fingerprint);
        Assert.Equal(Envelope.Length, status.SizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), status.UpdatedAt);
        Assert.Null(none);
        Assert.Equal(HttpMethod.Get, server.Requests[0].Method);
        Assert.Equal(new Uri("https://api.example.test/v1/sync/status"), server.Requests[0].Uri);
        Assert.Equal(Access, server.Requests[0].Authorization!.Parameter);
    }

    [Theory]
    [InlineData("{\"revision\":0,\"fingerprint\":\"FP\",\"sizeBytes\":10,\"updatedAt\":\"2026-10-20T12:00:00Z\"}")]       // revision 0 is never stored
    [InlineData("{\"revision\":1,\"fingerprint\":\"abc\",\"sizeBytes\":10,\"updatedAt\":\"2026-10-20T12:00:00Z\"}")]      // not a SHA-256
    [InlineData("{\"revision\":1,\"fingerprint\":\"FP\",\"sizeBytes\":0,\"updatedAt\":\"2026-10-20T12:00:00Z\"}")]        // empty envelope
    [InlineData("{\"revision\":1,\"fingerprint\":\"FP\",\"sizeBytes\":17000000,\"updatedAt\":\"2026-10-20T12:00:00Z\"}")] // above the limit
    [InlineData("{\"revision\":1,\"fingerprint\":\"FP\",\"sizeBytes\":10}")]                                               // no timestamp
    [InlineData("[]")]
    public async Task AMalformedStatus_IsUnexpected(string json)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, json.Replace("FP", Fingerprint, StringComparison.Ordinal));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetSyncStatusAsync(Access, default))).Failure);
    }

    [Fact]
    public async Task Download_ReturnsTheBytes_WhenTheyMatchWhatTheServerSaysAboutThem()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, EnvelopeJson());
        server.Reply(HttpStatusCode.NotFound);
        using HttpBitosXApiClient client = server.NewClient();

        SyncEnvelope? download = await client.DownloadEnvelopeAsync(Access, default);
        SyncEnvelope? none = await client.DownloadEnvelopeAsync(Access, default);

        Assert.NotNull(download);
        Assert.Equal(Envelope, download!.Envelope);
        Assert.Equal(3, download.Status.Revision);
        Assert.Null(none);
        Assert.Equal(new Uri("https://api.example.test/v1/sync/envelope"), server.Requests[0].Uri);
    }

    [Fact]
    public async Task Download_WithAFingerprintThatDoesNotMatchTheBytes_IsRefused()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, EnvelopeJson(fingerprint: new string('0', 64)));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.DownloadEnvelopeAsync(Access, default))).Failure);
    }

    [Fact]
    public async Task Download_WithASizeThatDoesNotMatchTheBytes_IsRefused()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, EnvelopeJson(size: Envelope.Length + 1));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.DownloadEnvelopeAsync(Access, default))).Failure);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    public async Task Download_ThatIsNotBase64_IsRefused(string envelope)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, EnvelopeJson(envelope: envelope));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.DownloadEnvelopeAsync(Access, default))).Failure);
    }

    [Fact]
    public async Task Download_MayBeFarLargerThanAnyOtherAnswer()
    {
        byte[] big = Encoding.UTF8.GetBytes("{\"version\":1,\"ciphertext\":\"" + new string('A', 300 * 1024) + "\"}");
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, EnvelopeJson(fingerprint: Convert.ToHexStringLower(SHA256.HashData(big)), size: big.Length, envelope: Convert.ToBase64String(big)));
        using HttpBitosXApiClient client = server.NewClient();

        SyncEnvelope? download = await client.DownloadEnvelopeAsync(Access, default);

        Assert.Equal(big, download!.Envelope);
        Assert.True(big.Length > HttpBitosXApiClient.MaxResponseBytes);
    }

    [Fact]
    public async Task OtherAnswers_KeepTheSmallCap()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, "{\"padding\":\"" + new string('x', HttpBitosXApiClient.MaxResponseBytes + 1) + "\"}");
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.GetSyncStatusAsync(Access, default))).Failure);
    }

    [Fact]
    public async Task Upload_PutsTheBaseRevisionAndTheBase64Envelope()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, StatusJson(revision: 3));
        using HttpBitosXApiClient client = server.NewClient();

        SyncStatus stored = await client.UploadEnvelopeAsync(Access, 2, Envelope, default);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(new Uri("https://api.example.test/v1/sync/envelope"), request.Uri);
        Assert.Equal("{\"baseRevision\":2,\"envelope\":\"" + Convert.ToBase64String(Envelope) + "\"}", request.Body);
        Assert.Equal(Access, request.Authorization!.Parameter);
        Assert.Equal(3, stored.Revision);
    }

    [Fact]
    public async Task Upload_AgainstAStaleRevision_IsAConflict()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.Conflict, "{\"type\":\"about:blank\",\"title\":\"Conflict\",\"currentRevision\":9,\"detail\":\"SECRET\"}");
        using HttpBitosXApiClient client = server.NewClient();

        ApiException ex = await FailsAsync(() => client.UploadEnvelopeAsync(Access, 2, Envelope, default));

        Assert.Equal(ApiFailure.Conflict, ex.Failure);
        Assert.Equal(409, ex.StatusCode);
        Assert.DoesNotContain("SECRET", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, null, 1)]       // size differs from what was sent
    [InlineData(2, null, null)]    // revision did not advance past the base
    [InlineData(3, "0000000000000000000000000000000000000000000000000000000000000000", null)] // fingerprint differs
    public async Task Upload_WhoseAnswerDoesNotDescribeWhatWasSent_IsUnexpected(long revision, string? fingerprint, int? size)
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.OK, StatusJson(revision, fingerprint, size));
        using HttpBitosXApiClient client = server.NewClient();

        Assert.Equal(ApiFailure.Unexpected, (await FailsAsync(() => client.UploadEnvelopeAsync(Access, 2, Envelope, default))).Failure);
    }

    [Fact]
    public async Task Upload_RefusesImpossibleArguments_BeforeAnyRoundTrip()
    {
        using var server = new FakeServer();
        using HttpBitosXApiClient client = server.NewClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UploadEnvelopeAsync(Access, -1, Envelope, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UploadEnvelopeAsync(Access, 0, Array.Empty<byte>(), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UploadEnvelopeAsync(Access, 0, new byte[HttpBitosXApiClient.MaxEnvelopeBytes + 1], default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadEnvelopeAsync(string.Empty, 0, Envelope, default));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Delete_IsADeleteWithTheToken_AndA204IsSuccess()
    {
        using var server = new FakeServer();
        server.Reply(HttpStatusCode.NoContent);
        using HttpBitosXApiClient client = server.NewClient();

        await client.DeleteEnvelopeAsync(Access, default);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(new Uri("https://api.example.test/v1/sync/envelope"), request.Uri);
        Assert.Equal(Access, request.Authorization!.Parameter);
    }

    [Fact]
    public void Fingerprint_IsTheLowercaseHexSha256OfTheBytes()
        => Assert.Equal(Fingerprint, HttpBitosXApiClient.Fingerprint(Envelope));
}
