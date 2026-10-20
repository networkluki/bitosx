using Microsoft.Extensions.Logging;

namespace BitosX.ApiClient;

/// <summary>
/// Structured logging for the server client. Nothing here carries an email, a password, a token,
/// a session id, or the server's own text: only what happened, which endpoint, and the status.
/// </summary>
public static partial class ApiLog
{
    [LoggerMessage(EventId = 2000, EventName = "api_call_failed", Level = LogLevel.Warning, Message = "Call to {Endpoint} failed: {Failure} (HTTP {StatusCode}).")]
    public static partial void CallFailed(ILogger logger, string endpoint, ApiFailure failure, int statusCode);

    [LoggerMessage(EventId = 2001, EventName = "account_signed_in", Level = LogLevel.Information, Message = "Account signed in.")]
    public static partial void SignedIn(ILogger logger);

    [LoggerMessage(EventId = 2002, EventName = "account_signed_out", Level = LogLevel.Information, Message = "Account signed out ({Reason}).")]
    public static partial void SignedOut(ILogger logger, string reason);

    [LoggerMessage(EventId = 2003, EventName = "account_tokens_refreshed", Level = LogLevel.Debug, Message = "Account tokens refreshed.")]
    public static partial void TokensRefreshed(ILogger logger);
}
