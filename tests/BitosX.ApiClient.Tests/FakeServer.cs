using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BitosX.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.ApiClient.Tests;

/// <summary>
/// A server that answers from a script, and remembers what it was asked. The client under test
/// talks to it through a real <see cref="HttpClient"/> with the same settings as in the application.
/// </summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public static readonly ApiServer Address = ApiServer.Parse("https://api.example.test");

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Answers, taken in order; the last one is repeated.</summary>
    public Queue<Func<RecordedRequest, HttpResponseMessage>> Answers { get; } = new();

    private Func<RecordedRequest, HttpResponseMessage>? _last;

    /// <summary>When set, no answer ever comes: not even the headers.</summary>
    public bool StallHeaders { get; set; }

    public void Reply(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? customize = null)
        => Answers.Enqueue(_ => Make(status, json, customize));

    public void Fail(Exception exception) => Answers.Enqueue(_ => throw exception);

    public static HttpResponseMessage Make(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? customize = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        customize?.Invoke(response);
        return response;
    }

    /// <summary>A client over this server, set up as <see cref="HttpBitosXApiClient.Create"/> sets one up; the deadlines may be shortened for a test.</summary>
    public HttpBitosXApiClient NewClient(
        TimeSpan? callTimeout = null,
        TimeSpan? envelopeTimeout = null,
        Microsoft.Extensions.Logging.ILogger<HttpBitosXApiClient>? logger = null)
    {
        var http = new HttpClient(this, disposeHandler: false)
        {
            BaseAddress = Address.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan, // as in the application: every call has its own deadline
            MaxResponseContentBufferSize = HttpBitosXApiClient.MaxEnvelopeResponseBytes,
        };
        HttpBitosXApiClient.ConfigureDefaultHeaders(http, "9.9.9");
        return new HttpBitosXApiClient(http, Address, logger ?? NullLogger<HttpBitosXApiClient>.Instance, callTimeout, envelopeTimeout);
    }

    /// <summary>A 200 whose headers arrive at once and whose body never does, as on a connection that died without a reset.</summary>
    public void ReplyWithABodyThatNeverArrives()
        => Answers.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() });

    private sealed class StalledContent : HttpContent
    {
        public StalledContent()
        {
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => Task.Delay(Timeout.Infinite);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("synchronous read");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization,
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString());
        Requests.Add(recorded);

        if (StallHeaders)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        Func<RecordedRequest, HttpResponseMessage> answer = Answers.Count > 0 ? Answers.Dequeue() : _last ?? throw new InvalidOperationException("No answer scripted.");
        _last = answer;
        return answer(recorded);
    }

    public static string Tokens(string access = "jwt.access", string refresh = "r3fr3sh", int accessSeconds = 900)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return "{\"accessToken\":\"" + access + "\",\"accessTokenExpiresAt\":\"" + now.AddSeconds(accessSeconds).ToString("o") + "\","
            + "\"refreshToken\":\"" + refresh + "\",\"refreshTokenExpiresAt\":\"" + now.AddDays(30).ToString("o") + "\",\"tokenType\":\"Bearer\"}";
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string Body,
    AuthenticationHeaderValue? Authorization,
    string UserAgent,
    string Accept);
