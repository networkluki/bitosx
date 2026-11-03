using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BitosX.ApiClient;

/// <summary>
/// The BitosX server over HTTPS. One <see cref="HttpClient"/>, no cookies, no redirects, a hard
/// deadline per call (headers and body), a cap on what is read back, and a fixed set of endpoints. Every response is treated as
/// untrusted input: the status decides the outcome, a body is accepted only when it is JSON of the
/// expected shape, and nothing the server says is ever shown or logged.
/// </summary>
public sealed class HttpBitosXApiClient : IBitosXApiClient, IDisposable
{
    /// <summary>
    /// How long an ordinary call may take in total: connect, send, headers and the whole body. Applied
    /// per call with a deadline of its own, because <see cref="HttpClient.Timeout"/> stops counting at
    /// the headers when the body is read as a stream.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an envelope transfer (<c>GET</c>/<c>PUT /v1/sync/envelope</c>, up to 16 MiB as Base64) may take in total.</summary>
    public static readonly TimeSpan EnvelopeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The most that is read back from the server for one call. Every real answer is far smaller — except an envelope download, which has <see cref="MaxEnvelopeResponseBytes"/>, and the session list, which has <see cref="MaxSessionsResponseBytes"/>.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>The most that is read back for the session list: the server lists at most 200 sessions, about 190 bytes each.</summary>
    public const int MaxSessionsResponseBytes = 256 * 1024;

    /// <summary>The largest encrypted envelope that is sent or accepted: the vault file's own limit (<c>FileLocalVaultStorage.MaxFileSizeBytes</c>), which the server mirrors.</summary>
    public const int MaxEnvelopeBytes = 16 * 1024 * 1024;

    /// <summary>The most that is read back for an envelope download: the Base64 of the largest envelope plus its metadata.</summary>
    public const int MaxEnvelopeResponseBytes = (MaxEnvelopeBytes + 2) / 3 * 4 + 4096;

    private const int FingerprintHexLength = 64;

    private const int MaxTokenLength = 8 * 1024;
    private const int MaxRefreshTokenLength = 256;
    private const int MaxEmailLength = 320;
    private const int MaxSessions = 1000;
    private const int MaxSecondFactorMethods = 32;

    /// <summary>The most passkeys a list may hold to be believed (the server allows 10 per account).</summary>
    private const int MaxPasskeys = 100;

    /// <summary>The longest passkey name taken: the server allows 64 characters, which may be twice as many UTF-16 units.</summary>
    private const int MaxPasskeyNameLength = 128;

    private const int MaxPasskeyIdLength = 128;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 8,
    };

    private static readonly JsonSerializerOptions ReadJson = WithUtcTimestamps(Json);

    private readonly HttpClient _http;
    private readonly ILogger<HttpBitosXApiClient> _logger;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _envelopeTimeout;
    private bool _disposed;

    /// <summary>
    /// A client over <paramref name="http"/>, which must already have its base address and buffer
    /// limit set (see <see cref="Create"/>). For tests, which hand in a client over a handler of their
    /// own, and may shorten the deadlines.
    /// </summary>
    public HttpBitosXApiClient(
        HttpClient http,
        ApiServer server,
        ILogger<HttpBitosXApiClient> logger,
        TimeSpan? callTimeout = null,
        TimeSpan? envelopeTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        Server = server ?? throw new ArgumentNullException(nameof(server));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _callTimeout = callTimeout ?? Timeout;
        _envelopeTimeout = envelopeTimeout ?? EnvelopeTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_callTimeout, TimeSpan.Zero, nameof(callTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_envelopeTimeout, TimeSpan.Zero, nameof(envelopeTimeout));
    }

    private static JsonSerializerOptions WithUtcTimestamps(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        copy.Converters.Add(new UtcTimestampConverter());
        return copy;
    }

    /// <summary>A client for <paramref name="server"/> with the transport settings this class relies on.</summary>
    /// <param name="server">The server to talk to.</param>
    /// <param name="productVersion">The application version, for the <c>User-Agent</c> header the server records as the session's client.</param>
    /// <param name="logger">Receives non-sensitive events.</param>
    public static HttpBitosXApiClient Create(ApiServer server, string productVersion, ILogger<HttpBitosXApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,      // a redirect could carry a credential elsewhere; the API never redirects
            UseCookies = false,             // the API sets no cookies; bearer tokens are sent explicitly
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 32, // KiB
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = server.BaseAddress,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // every call has its own deadline, body included (SendCoreAsync)
            MaxResponseContentBufferSize = MaxEnvelopeResponseBytes, // the real cap is per call, in SendAsync
        };
        ConfigureDefaultHeaders(http, productVersion);
        return new HttpBitosXApiClient(http, server, logger);
    }

    /// <summary>The headers every call carries. Public so a test client is set up the same way.</summary>
    public static void ConfigureDefaultHeaders(HttpClient http, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);

        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.Clear();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BitosX", productVersion));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({PlatformName()})"));
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return OperatingSystem.IsLinux() ? "Linux" : "Other";
    }

    public bool IsServerIntegrationEnabled => true;

    public ApiServer? Server { get; }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HealthDto? health = await SendAsync<HealthDto>(HttpMethod.Get, "health", null, null, cancellationToken).ConfigureAwait(false);
            return string.Equals(health?.Status, "healthy", StringComparison.Ordinal);
        }
        catch (ApiException)
        {
            return false;
        }
    }

    public async Task<SignInResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        const string path = "v1/auth/login";
        (int status, LoginDto? dto) = await SendCoreAsync<LoginDto>(
            HttpMethod.Post, path, new { email, password }, null, Call.Default(path), cancellationToken).ConfigureAwait(false);

        // 202: the password was accepted and a second factor is required. The status decides; a 202 that
        // carries session tokens, or a 200 that carries a challenge, is a server this client does not trust.
        if (status == 202)
        {
            if (dto is null
                || dto.AccessToken is not null
                || dto.RefreshToken is not null
                || !IsMfaToken(dto.MfaToken)
                || dto.MfaTokenExpiresAt is not { } expires
                || (dto.Method is not null && !string.Equals(dto.Method, "totp", StringComparison.Ordinal))
                || ToMethods(dto.Methods) is not { } methods
                || (methods & SecondFactorMethods.Code) == SecondFactorMethods.None)
            {
                // A challenge this client cannot complete with a code is one it does not understand.
                throw Unexpected(path);
            }

            return SignInResult.SecondFactor(new SecondFactorChallenge(dto.MfaToken, expires, methods));
        }

        if (dto?.MfaToken is not null)
        {
            throw Unexpected(path);
        }

        return SignInResult.SignedIn(ToTokens(dto));
    }

    public async Task<AccountTokens> CompleteSecondFactorAsync(string mfaToken, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mfaToken);
        ArgumentException.ThrowIfNullOrEmpty(code);

        TokenDto? tokens = await SendAsync<TokenDto>(
            HttpMethod.Post, "v1/auth/login/totp", new { mfaToken, code }, null, cancellationToken).ConfigureAwait(false);
        return ToTokens(tokens);
    }

    /// <summary>
    /// The second-factor methods of a challenge, read tolerantly: no list is what a server before passkeys
    /// sends and means the code step (TOTP or a recovery code); names this client does not know, and
    /// empty entries, are ignored. <see langword="null"/> only for a list too long to be one.
    /// </summary>
    private static SecondFactorMethods? ToMethods(string?[]? names)
    {
        if (names is null)
        {
            return SecondFactorMethods.Code;
        }

        if (names.Length > MaxSecondFactorMethods)
        {
            return null;
        }

        SecondFactorMethods methods = SecondFactorMethods.None;
        foreach (string? name in names)
        {
            methods |= name switch
            {
                "totp" => SecondFactorMethods.Totp,
                "recovery" => SecondFactorMethods.Recovery,
                "passkey" => SecondFactorMethods.Passkey,
                _ => SecondFactorMethods.None,
            };
        }

        return methods;
    }

    /// <summary>The second-step token is 43 unpadded Base64URL characters (256 bits), as the refresh token is.</summary>
    private static bool IsMfaToken([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length != 43)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        TokenDto? tokens = await SendAsync<TokenDto>(
            HttpMethod.Post, "v1/auth/refresh", new { refreshToken }, null, cancellationToken).ConfigureAwait(false);
        return ToTokens(tokens);
    }

    public Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return SendAsync<NoBody>(HttpMethod.Post, "v1/auth/logout", null, accessToken, cancellationToken);
    }

    public Task LogoutAllAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return SendAsync<NoBody>(HttpMethod.Post, "v1/auth/logout-all", null, accessToken, cancellationToken);
    }

    public async Task<AccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        ProfileDto? me = await SendAsync<ProfileDto>(HttpMethod.Get, "v1/account/me", null, accessToken, cancellationToken).ConfigureAwait(false);
        if (me is null
            || me.AccountId is not { } id
            || !IsText(me.Email, MaxEmailLength)
            || me.EmailVerified is not { } verified
            || me.CreatedAt is not { } created)
        {
            throw Unexpected("v1/account/me");
        }

        return new AccountProfile(id, me.Email, verified, created);
    }

    public async Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        SessionDto[]? sessions = await SendAsync<SessionDto[]>(
            HttpMethod.Get, "v1/account/sessions", null, accessToken, cancellationToken, Call.Default("v1/account/sessions") with { MaxResponseBytes = MaxSessionsResponseBytes }).ConfigureAwait(false);
        if (sessions is null || sessions.Length > MaxSessions)
        {
            throw Unexpected("v1/account/sessions");
        }

        var result = new List<AccountSessionInfo>(sessions.Length);
        foreach (SessionDto s in sessions)
        {
            if (s is null
                || s.Id is not { } id
                || s.CreatedAt is not { } created
                || s.LastSeenAt is not { } seen
                || s.ExpiresAt is not { } expires
                || s.Current is not { } current)
            {
                throw Unexpected("v1/account/sessions");
            }

            result.Add(new AccountSessionInfo(id, created, seen, expires, current));
        }

        return result;
    }

    public Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        // The id is in the path but never in the log: the endpoint is logged as its template.
        return SendAsync<NoBody>(
            HttpMethod.Delete, "v1/account/sessions/" + sessionId.ToString("D"), null, accessToken, cancellationToken, Call.Default("v1/account/sessions/{id}"));
    }

    public Task ChangePasswordAsync(string accessToken, string currentPassword, string newPassword, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // A null code is left out of the body (the account has no second factor).
        return SendAsync<NoBody>(
            HttpMethod.Post, "v1/account/password", new { currentPassword, newPassword, code = NullIfEmpty(code) }, accessToken, cancellationToken);
    }

    public Task DeleteAccountAsync(string accessToken, string password, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(password);
        return SendAsync<NoBody>(
            HttpMethod.Post, "v1/account/delete", new { password, code = NullIfEmpty(code) }, accessToken, cancellationToken);
    }

    public async Task<TwoFactorStatus?> GetTwoFactorStatusAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        const string path = "v1/account/totp";
        TotpStatusDto? dto;
        try
        {
            dto = await SendAsync<TotpStatusDto>(HttpMethod.Get, path, null, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure is ApiFailure.Unavailable or ApiFailure.NotFound)
        {
            return null; // the server does not offer two-factor (capability off, or an older server)
        }

        if (dto is null
            || dto.Enabled is not { } enabled
            || dto.RecoveryCodesRemaining is not { } remaining
            || remaining is < 0 or > 100
            || dto.PendingEnrollment is not { } pending)
        {
            throw Unexpected(path);
        }

        return new TwoFactorStatus(enabled, dto.ConfirmedAt, remaining, pending);
    }

    public async Task<PasskeyList?> GetPasskeysAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        const string path = "v1/account/passkeys";
        PasskeyListDto? dto;
        try
        {
            dto = await SendAsync<PasskeyListDto>(HttpMethod.Get, path, null, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure is ApiFailure.NotFound)
        {
            return null; // the server does not offer passkeys (not configured, or a server before them)
        }

        if (dto is null
            || dto.Passkeys is not { } passkeys
            || passkeys.Length > MaxPasskeys
            || dto.Max is not { } max
            || max is < 0 or > MaxPasskeys)
        {
            throw Unexpected(path);
        }

        var result = new List<PasskeyInfo>(passkeys.Length);
        foreach (PasskeyDto p in passkeys)
        {
            if (p is null
                || !IsText(p.Id, MaxPasskeyIdLength)
                || !IsText(p.Name, MaxPasskeyNameLength)
                || p.CreatedAt is not { } created
                || p.BackedUp is not { } backedUp)
            {
                throw Unexpected(path);
            }

            result.Add(new PasskeyInfo(p.Id, p.Name, created, p.LastUsedAt, backedUp));
        }

        return new PasskeyList(result, max);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // ---- encrypted sync ----

    public async Task<SyncStatus?> GetSyncStatusAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        SyncStatusDto? status;
        try
        {
            status = await SendAsync<SyncStatusDto>(HttpMethod.Get, "v1/sync/status", null, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.NotFound)
        {
            return null; // the server holds nothing for this account
        }

        return ToStatus(status, "v1/sync/status");
    }

    public async Task<SyncEnvelope?> DownloadEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        SyncEnvelopeDto? dto;
        try
        {
            dto = await SendAsync<SyncEnvelopeDto>(
                HttpMethod.Get, "v1/sync/envelope", null, accessToken, cancellationToken, Call.Envelope("v1/sync/envelope", MaxEnvelopeResponseBytes)).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.NotFound)
        {
            return null;
        }

        const string path = "v1/sync/envelope";
        SyncStatus status = ToStatus(dto, path);
        if (dto!.Envelope is null || dto.Envelope.Length > MaxEnvelopeResponseBytes)
        {
            throw Unexpected(path);
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(dto.Envelope);
        }
        catch (FormatException ex)
        {
            throw Fail(path, ApiFailure.Unexpected, 200, null, ex);
        }

        // The server's own statements about the bytes must hold for the bytes it sent. A mismatch is
        // a server (or path) that cannot be trusted, not something to repair here.
        if (envelope.Length == 0
            || envelope.Length > MaxEnvelopeBytes
            || envelope.Length != status.SizeBytes
            || !string.Equals(Fingerprint(envelope), status.Fingerprint, StringComparison.Ordinal))
        {
            throw Unexpected(path);
        }

        return new SyncEnvelope(status, envelope);
    }

    public async Task<SyncStatus> UploadEnvelopeAsync(string accessToken, long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentOutOfRangeException.ThrowIfNegative(baseRevision);
        if (envelope.Length == 0 || envelope.Length > MaxEnvelopeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "The envelope must be between 1 byte and 16 MiB.");
        }

        const string path = "v1/sync/envelope";
        SyncStatusDto? stored = await SendAsync<SyncStatusDto>(
            HttpMethod.Put, path, new { baseRevision, envelope = Convert.ToBase64String(envelope.Span) }, accessToken, cancellationToken, Call.Envelope(path, MaxResponseBytes)).ConfigureAwait(false);
        SyncStatus status = ToStatus(stored, path);

        // What the server says it stored must be what was sent.
        if (status.SizeBytes != envelope.Length
            || status.Revision <= baseRevision
            || !string.Equals(Fingerprint(envelope.Span), status.Fingerprint, StringComparison.Ordinal))
        {
            throw Unexpected(path);
        }

        return status;
    }

    public Task DeleteEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return SendAsync<NoBody>(HttpMethod.Delete, "v1/sync/envelope", null, accessToken, cancellationToken);
    }

    /// <summary>The SHA-256 of an envelope as the server states it: lowercase hex of the exact bytes.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> envelope)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(envelope));

    private SyncStatus ToStatus(SyncStatusDto? dto, string path)
    {
        if (dto is null
            || dto.Revision is not { } revision
            || revision < 1
            || dto.SizeBytes is not { } size
            || size < 1
            || size > MaxEnvelopeBytes
            || dto.UpdatedAt is not { } updated
            || !IsLowerHex(dto.Fingerprint, FingerprintHexLength))
        {
            throw Unexpected(path);
        }

        return new SyncStatus(revision, dto.Fingerprint, size, updated);
    }

    private static bool IsLowerHex([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    // ---- transport ----

    /// <summary>
    /// One call. Resolves to the parsed body, or <see langword="null"/> for a 204. Throws
    /// <see cref="ApiException"/> for every other outcome, mapped from the status alone: the body of
    /// an error response is never read into anything.
    /// </summary>
    private async Task<TResponse?> SendAsync<TResponse>(
        HttpMethod method, string path, object? body, string? accessToken, CancellationToken cancellationToken, Call? call = null)
        where TResponse : class
    {
        (_, TResponse? parsed) = await SendCoreAsync<TResponse>(method, path, body, accessToken, call ?? Call.Default(path), cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>How one call is bounded and logged: what is read back at most, how long it may take, and the endpoint's name for the log (never an id).</summary>
    private sealed record Call(string Endpoint, int MaxResponseBytes, bool IsEnvelopeTransfer)
    {
        public static Call Default(string endpoint) => new(endpoint, HttpBitosXApiClient.MaxResponseBytes, false);

        public static Call Envelope(string endpoint, int maxResponseBytes) => new(endpoint, maxResponseBytes, true);
    }

    /// <summary>As <see cref="SendAsync{TResponse}"/>, also returning the 2xx status for the one call whose status carries meaning (login: 200 vs 202).</summary>
    private async Task<(int Status, TResponse? Body)> SendCoreAsync<TResponse>(
        HttpMethod method, string path, object? body, string? accessToken, Call call, CancellationToken cancellationToken)
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // One deadline for the whole call — connect, send, headers and body — linked to the caller's
        // token. A stall anywhere ends as a network failure instead of holding the caller (and the
        // locks it holds: the refresh, the sync) for good.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(call.IsEnvelopeTransfer ? _envelopeTimeout : _callTimeout);
        CancellationToken token = deadline.Token;
        string endpoint = call.Endpoint;
        int maxResponseBytes = call.MaxResponseBytes;

        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        HttpResponseMessage response;
        try
        {
            // Headers only: the body is read below, under this class's own cap, so that an oversized
            // answer is judged as such rather than as a transport failure.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Fail(endpoint, ApiFailure.Network, 0, null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The call's own deadline, not the caller's cancellation.
            throw Fail(endpoint, ApiFailure.Network, 0, null, ex);
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            if (status == 204)
            {
                return (status, null);
            }

            if (status is < 200 or > 299)
            {
                throw Fail(endpoint, FailureFor(status), status, RetryAfter(response), null);
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(endpoint, ApiFailure.Unexpected, status, null, null);
            }

            if (response.Content.Headers.ContentLength is { } length && length > maxResponseBytes)
            {
                throw Fail(endpoint, ApiFailure.Unexpected, status, null, null);
            }

            string text;
            try
            {
                text = await ReadCappedAsync(response.Content, maxResponseBytes, token).ConfigureAwait(false)
                    ?? throw Fail(endpoint, ApiFailure.Unexpected, status, null, null);
            }
            catch (HttpRequestException ex)
            {
                throw Fail(endpoint, ApiFailure.Network, status, null, ex);
            }
            catch (IOException ex)
            {
                throw Fail(endpoint, ApiFailure.Network, status, null, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw Fail(endpoint, ApiFailure.Network, status, null, ex);
            }

            try
            {
                TResponse parsed = JsonSerializer.Deserialize<TResponse>(text, ReadJson) ?? throw Fail(endpoint, ApiFailure.Unexpected, status, null, null);
                return (status, parsed);
            }
            catch (JsonException ex)
            {
                throw Fail(endpoint, ApiFailure.Unexpected, status, null, ex);
            }
        }
    }

    /// <summary>The body as UTF-8 text, or <see langword="null"/> when it is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var collected = new MemoryStream();
        byte[] chunk = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            collected.Write(chunk, 0, read);
        }

        byte[] buffer = collected.GetBuffer();

        try
        {
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer, 0, total);
        }
        catch (ArgumentException)
        {
            return null; // not UTF-8: not JSON either
        }
    }

    private static ApiFailure FailureFor(int status) => status switch
    {
        400 or 413 or 415 or 422 => ApiFailure.InvalidRequest,
        401 => ApiFailure.Unauthenticated,
        403 => ApiFailure.Forbidden,
        404 => ApiFailure.NotFound,
        409 => ApiFailure.Conflict,
        429 => ApiFailure.RateLimited,
        502 or 503 or 504 => ApiFailure.Unavailable,
        _ => ApiFailure.Unexpected,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromDays(1))
        {
            return delta;
        }

        return null;
    }

    private ApiException Fail(string path, ApiFailure failure, int status, TimeSpan? retryAfter, Exception? inner)
    {
        ApiLog.CallFailed(_logger, path, failure, status);
        return new ApiException(failure, status, retryAfter, inner);
    }

    private ApiException Unexpected(string path) => Fail(path, ApiFailure.Unexpected, 200, null, null);

    private static bool IsText([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, int maxLength)
        => !string.IsNullOrEmpty(value) && value.Length <= maxLength;

    private static AccountTokens ToTokens(TokenDto? dto)
    {
        if (dto is null
            || !IsText(dto.AccessToken, MaxTokenLength)
            || !IsText(dto.RefreshToken, MaxRefreshTokenLength)
            || dto.AccessTokenExpiresAt is not { } accessExpires
            || dto.RefreshTokenExpiresAt is not { } refreshExpires
            || (dto.TokenType is not null && !string.Equals(dto.TokenType, "Bearer", StringComparison.Ordinal)))
        {
            throw new ApiException(ApiFailure.Unexpected, 200);
        }

        return new AccountTokens(dto.AccessToken, accessExpires, dto.RefreshToken, refreshExpires);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    // ---- wire shapes (every field optional, so a missing one is a validation failure, not a crash) ----

    private sealed class NoBody
    {
    }

    private sealed class HealthDto
    {
        public string? Service { get; init; }

        public string? Status { get; init; }
    }

    private class TokenDto
    {
        public string? AccessToken { get; init; }

        public DateTimeOffset? AccessTokenExpiresAt { get; init; }

        public string? RefreshToken { get; init; }

        public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

        public string? TokenType { get; init; }
    }

    /// <summary>The login answer: either the token fields (200) or the second-factor fields (202).</summary>
    private sealed class LoginDto : TokenDto
    {
        public string? MfaToken { get; init; }

        public DateTimeOffset? MfaTokenExpiresAt { get; init; }

        public string? Method { get; init; }

        public string?[]? Methods { get; init; }
    }

    private sealed class ProfileDto
    {
        public Guid? AccountId { get; init; }

        public string? Email { get; init; }

        public bool? EmailVerified { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }
    }

    private class SyncStatusDto
    {
        public long? Revision { get; init; }

        public string? Fingerprint { get; init; }

        public int? SizeBytes { get; init; }

        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class SyncEnvelopeDto : SyncStatusDto
    {
        public string? Envelope { get; init; }
    }

    private sealed class TotpStatusDto
    {
        public bool? Enabled { get; init; }

        public DateTimeOffset? ConfirmedAt { get; init; }

        public int? RecoveryCodesRemaining { get; init; }

        public bool? PendingEnrollment { get; init; }
    }

    private sealed class PasskeyListDto
    {
        public PasskeyDto[]? Passkeys { get; init; }

        public int? Max { get; init; }
    }

    private sealed class PasskeyDto
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }

        public DateTimeOffset? LastUsedAt { get; init; }

        public bool? BackedUp { get; init; }
    }

    private sealed class SessionDto
    {
        public Guid? Id { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }

        public DateTimeOffset? LastSeenAt { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }

        public bool? Current { get; init; }
    }
}

/// <summary>
/// Reads the server's timestamps as ISO 8601 and, defensively, takes one without an offset as UTC
/// (the server always sends <c>Z</c>; an offset-less value read as local time would be wrong by the
/// machine's offset). A timestamp with an explicit offset keeps it.
/// </summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTime(out DateTime parsed))
        {
            throw new JsonException("Not an ISO 8601 timestamp.");
        }

        if (parsed.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        }

        return reader.TryGetDateTimeOffset(out DateTimeOffset withOffset)
            ? withOffset
            : throw new JsonException("Not an ISO 8601 timestamp.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}
