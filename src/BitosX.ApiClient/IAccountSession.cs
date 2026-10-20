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

    /// <summary>Signs in. On success the tokens are held until sign-out, lock, or exit.</summary>
    Task SignInAsync(string email, string password, CancellationToken cancellationToken = default);

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
}
