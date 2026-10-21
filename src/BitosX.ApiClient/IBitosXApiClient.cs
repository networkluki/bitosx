namespace BitosX.ApiClient;

/// <summary>
/// The BitosX server, as the application sees it: the account endpoints of <c>api.bitosx.se</c>.
/// </summary>
/// <remarks>
/// <para>
/// BitosX is zero-knowledge: nothing here ever carries the master password, the vault key, an
/// item password, a note, or any other vault content, and no method of this interface may be
/// given a way to. What crosses the wire is the <b>account</b> email and password — a different
/// secret from the master password, used only to sign in — and the tokens the server issues for
/// the session. See docs/SECURITY_MODEL.md and docs/ACCOUNT.md.
/// </para>
/// <para>
/// Every method throws <see cref="ApiException"/> when the call does not succeed, with a
/// <see cref="ApiFailure"/> that says why in terms that are safe to show. The access token is
/// passed by the caller, so the client itself holds no secret between calls; the
/// <see cref="AccountSession"/> does that.
/// </para>
/// </remarks>
public interface IBitosXApiClient
{
    /// <summary>Whether a server is configured. When not, every other method throws <see cref="InvalidOperationException"/>.</summary>
    bool IsServerIntegrationEnabled { get; }

    /// <summary>The server, or <see langword="null"/> when integration is disabled.</summary>
    ApiServer? Server { get; }

    /// <summary>Whether the server answers its liveness check. <see langword="false"/> in local-only mode or when it cannot be reached.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>Signs in with the account email and password and returns the session's tokens.</summary>
    Task<AccountTokens> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a new token pair. The token given is spent whether or not this succeeds.</summary>
    Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Ends the session the access token belongs to.</summary>
    Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Ends every session of the account.</summary>
    Task LogoutAllAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>The signed-in account's own profile.</summary>
    Task<AccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>The account's current sessions.</summary>
    Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Ends one of the account's sessions. Ending the current one is a logout.</summary>
    Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default);
}
