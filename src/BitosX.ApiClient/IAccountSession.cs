namespace BitosX.ApiClient;

/// <summary>
/// The signed-in account, if any: holds the session's tokens in memory for the application and
/// makes the authenticated calls. See <see cref="AccountSession"/>.
/// </summary>
public interface IAccountSession
{
    /// <summary>Whether a server is configured at all. When not, signing in is impossible and the account page says so.</summary>
    bool IsAvailable { get; }

    /// <summary>The server's name for the user interface, or <see langword="null"/> when none is configured.</summary>
    string? ServerName { get; }

    /// <summary>Whether an account is signed in: a refresh token is held and has not expired.</summary>
    bool IsSignedIn { get; }

    /// <summary>Raised after <see cref="IsSignedIn"/> changes — on signing in, and on every way of signing out.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Whether a sign-in is half done: the password was accepted and the server waits for a second-factor
    /// code. The challenge is held in memory until <see cref="CompleteSignInAsync"/> succeeds,
    /// <see cref="CancelSignIn"/>, a new <see cref="SignInAsync"/>, or any sign-out.
    /// </summary>
    bool IsAwaitingSecondFactor { get; }

    /// <summary>
    /// Signs in. <see cref="SignInOutcome.SignedIn"/>: the tokens are held until sign-out, lock, or exit.
    /// <see cref="SignInOutcome.SecondFactorRequired"/>: no session yet; call <see cref="CompleteSignInAsync"/> with a code.
    /// </summary>
    Task<SignInOutcome> SignInAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Completes a sign-in that is awaiting a second factor with a TOTP or recovery code. A refused code keeps the challenge; the user may try again or start over.</summary>
    Task CompleteSignInAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Drops a pending second-factor challenge, so the next sign-in starts with the password.</summary>
    void CancelSignIn();

    /// <summary>The signed-in account's own profile.</summary>
    Task<AccountProfile> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>The account's current sessions, this one marked.</summary>
    Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends one session. Ending this one signs out.</summary>
    Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Ends this session on the server and forgets the tokens. The tokens are forgotten even if the server cannot be told.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends every session of the account on the server and forgets the tokens. The tokens are forgotten even if the server cannot be told.</summary>
    Task SignOutEverywhereAsync(CancellationToken cancellationToken = default);

    // ---- encrypted sync, with this session's token (docs/SYNC.md) ----

    /// <summary>What the server holds for the account, or <see langword="null"/> when nothing.</summary>
    Task<SyncStatus?> GetSyncStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The stored envelope, verified against the server's own statements about it, or <see langword="null"/> when nothing is stored.</summary>
    Task<SyncEnvelope?> DownloadEnvelopeAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the envelope if the server still holds <paramref name="baseRevision"/>; <see cref="ApiFailure.Conflict"/> otherwise.</summary>
    Task<SyncStatus> UploadEnvelopeAsync(long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored envelope from the server.</summary>
    Task DeleteEnvelopeAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a sign-in attempt that did not fail led to.</summary>
public enum SignInOutcome
{
    SignedIn,
    SecondFactorRequired,
}
