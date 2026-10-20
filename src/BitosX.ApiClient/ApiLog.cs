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

    [LoggerMessage(EventId = 2010, EventName = "sync_checked", Level = LogLevel.Information, Message = "Sync checked: {Outcome} (server revision {ServerRevision}).")]
    public static partial void SyncChecked(ILogger logger, string outcome, long serverRevision);

    [LoggerMessage(EventId = 2011, EventName = "sync_uploaded", Level = LogLevel.Information, Message = "Encrypted vault uploaded as revision {Revision} ({SizeBytes} bytes).")]
    public static partial void SyncUploaded(ILogger logger, long revision, int sizeBytes);

    [LoggerMessage(EventId = 2012, EventName = "sync_downloaded", Level = LogLevel.Information, Message = "Encrypted vault revision {Revision} downloaded and validated ({SizeBytes} bytes).")]
    public static partial void SyncDownloaded(ILogger logger, long revision, int sizeBytes);

    [LoggerMessage(EventId = 2013, EventName = "sync_restored", Level = LogLevel.Information, Message = "Local vault replaced with server revision {Revision}.")]
    public static partial void SyncRestored(ILogger logger, long revision);

    [LoggerMessage(EventId = 2014, EventName = "sync_deleted", Level = LogLevel.Information, Message = "Encrypted vault removed from the server.")]
    public static partial void SyncDeleted(ILogger logger);

    [LoggerMessage(EventId = 2015, EventName = "sync_conflict", Level = LogLevel.Warning, Message = "Sync conflict: the server holds revision {ServerRevision}, this device last synced revision {LocalRevision}. Nothing was changed.")]
    public static partial void SyncConflict(ILogger logger, long serverRevision, long localRevision);
}
