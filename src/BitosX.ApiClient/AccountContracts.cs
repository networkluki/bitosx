namespace BitosX.ApiClient;

/// <summary>
/// The tokens of a signed-in account session, as the server issued them. The access token is a
/// short-lived bearer credential sent with every authenticated call; the refresh token is an
/// opaque, single-use credential exchanged for the next pair. Both are secrets: never logged,
/// never written to disk by this milestone, never shown.
/// </summary>
public sealed record AccountTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>The signed-in account's own profile, as <c>GET /v1/account/me</c> reports it.</summary>
public sealed record AccountProfile(Guid AccountId, string Email, bool EmailVerified, DateTimeOffset CreatedAt);

/// <summary>One of the account's current sessions, as <c>GET /v1/account/sessions</c> lists them.</summary>
public sealed record AccountSessionInfo(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, bool Current);
