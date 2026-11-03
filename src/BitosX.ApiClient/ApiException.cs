namespace BitosX.ApiClient;

/// <summary>Why a call to the BitosX server did not succeed, in terms the user interface can act on.</summary>
public enum ApiFailure
{
    /// <summary>The server could not be reached, did not answer in time, or the connection failed (TLS included).</summary>
    Network = 0,

    /// <summary>The server rejected the request as malformed or outside its limits (400, 413).</summary>
    InvalidRequest = 1,

    /// <summary>The credentials were wrong, or the session is no longer valid (401). The server never says which.</summary>
    Unauthenticated = 2,

    /// <summary>What was asked for does not exist, or is not the caller's (404).</summary>
    NotFound = 3,

    /// <summary>Too many requests; <see cref="ApiException.RetryAfter"/> says when to try again, if the server said (429).</summary>
    RateLimited = 4,

    /// <summary>The server is up but cannot take the request right now (503).</summary>
    Unavailable = 5,

    /// <summary>The server answered in a way this client does not understand: an unexpected status, a body that is not JSON, or JSON of the wrong shape.</summary>
    Unexpected = 6,

    /// <summary>The server holds a newer revision than the one the request was based on (409). Nothing was changed; the client must look again before it decides.</summary>
    Conflict = 7,

    /// <summary>
    /// The server refused a password-confirmed operation (403): the account password, or the
    /// second-factor code, was not accepted. The session is still valid — this is not a sign-out.
    /// </summary>
    Forbidden = 8,
}

/// <summary>
/// A call to the BitosX server failed. Carries a <see cref="Failure"/> kind and a fixed message for
/// it — never the server's own text, a token, a password, an address or anything else from the
/// request or the response, so it is safe to show and to log.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiFailure failure, int statusCode = 0, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(MessageFor(failure), innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public ApiException()
        : this(ApiFailure.Unexpected)
    {
    }

    public ApiException(string message)
        : base(message)
    {
        Failure = ApiFailure.Unexpected;
    }

    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = ApiFailure.Unexpected;
    }

    public ApiFailure Failure { get; }

    /// <summary>The HTTP status that led here, or 0 when there was no response.</summary>
    public int StatusCode { get; }

    /// <summary>For <see cref="ApiFailure.RateLimited"/>: how long the server asked the client to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string MessageFor(ApiFailure failure) => failure switch
    {
        ApiFailure.Network => "The BitosX server could not be reached. Check your connection and try again.",
        ApiFailure.InvalidRequest => "The server rejected the request.",
        ApiFailure.Unauthenticated => "Invalid email or password, or the session has ended.",
        ApiFailure.NotFound => "That no longer exists.",
        ApiFailure.RateLimited => "Too many attempts. Try again later.",
        ApiFailure.Unavailable => "The BitosX server is temporarily unavailable. Try again shortly.",
        ApiFailure.Conflict => "Another device changed the vault on the server first. Nothing was changed.",
        ApiFailure.Forbidden => "The server did not accept the account password or the code. You are still signed in.",

        // Not "nothing was changed": an unexpected answer can follow a request the server carried out.
        _ => "The BitosX server answered in an unexpected way.",
    };
}
