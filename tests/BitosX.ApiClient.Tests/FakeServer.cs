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

    public HttpBitosXApiClient NewClient()
    {
        var http = new HttpClient(this, disposeHandler: false)
        {
            BaseAddress = Address.BaseAddress,
            Timeout = HttpBitosXApiClient.Timeout,
            MaxResponseContentBufferSize = HttpBitosXApiClient.MaxEnvelopeResponseBytes,
        };
        HttpBitosXApiClient.ConfigureDefaultHeaders(http, "9.9.9");
        return new HttpBitosXApiClient(http, Address, NullLogger<HttpBitosXApiClient>.Instance);
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
