namespace BitosX.ApiClient;

/// <summary>
/// The BitosX server, as the application sees it: the account endpoints of <c>api.bitosx.se</c>.
/// </summary>
/// <remarks>
/// <para>
/// BitosX is zero-knowledge: nothing here ever carries the master password, the vault key, an
/// item password, a note, or any other vault content, and no method of this interface may be
/// given a way to. What crosses the wire is the <b>account</b> email and password — a different
/// secret from the master password, used only to sign in — the tokens the server issues for the
/// session, and, since 0.9.0, the <b>encrypted vault envelope</b>: the vault file as it is on
/// disk, ciphertext and public key-derivation parameters, which the server stores as opaque
/// bytes and cannot read. See docs/SECURITY_MODEL.md, docs/ACCOUNT.md and docs/SYNC.md.
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

    // ---- encrypted sync (docs/SYNC.md) ----

    /// <summary>What the server holds for the account: revision, fingerprint and size of the stored envelope, or <see langword="null"/> when it holds nothing.</summary>
    Task<SyncStatus?> GetSyncStatusAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>The stored envelope itself, verified against the fingerprint the server states, or <see langword="null"/> when the server holds nothing.</summary>
    Task<SyncEnvelope?> DownloadEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="envelope"/> as the account's vault, if the server still holds revision
    /// <paramref name="baseRevision"/> (0 for "nothing yet"). Otherwise fails with
    /// <see cref="ApiFailure.Conflict"/> and the server keeps what it had.
    /// </summary>
    Task<SyncStatus> UploadEnvelopeAsync(string accessToken, long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored envelope. Succeeds when there was none.</summary>
    Task DeleteEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default);
}
