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

/// <summary>
/// The server accepted the password but the account has a second factor: a short-lived, single-step
/// token to present with a code from the authenticator app (or a recovery code). No session exists
/// yet. The token is a secret for that one step: held in memory, never logged, never shown.
/// </summary>
public sealed record SecondFactorChallenge(string MfaToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"SecondFactorChallenge {{ MfaToken = <redacted>, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>What <c>POST /v1/auth/login</c> answered: tokens (signed in), or a second-factor challenge.</summary>
public sealed record SignInResult(AccountTokens? Tokens, SecondFactorChallenge? Challenge)
{
    public bool RequiresSecondFactor => Challenge is not null;

    public static SignInResult SignedIn(AccountTokens tokens) => new(tokens, null);

    public static SignInResult SecondFactor(SecondFactorChallenge challenge) => new(null, challenge);
}
