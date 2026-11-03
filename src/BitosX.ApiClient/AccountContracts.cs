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
    DateTimeOffset RefreshTokenExpiresAt)
{
    // The compiler-generated ToString would print both secrets; this one never does.
    public override string ToString()
        => $"AccountTokens {{ AccessToken = <redacted>, AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, RefreshToken = <redacted>, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O} }}";
}

/// <summary>The signed-in account's own profile, as <c>GET /v1/account/me</c> reports it.</summary>
public sealed record AccountProfile(Guid AccountId, string Email, bool EmailVerified, DateTimeOffset CreatedAt);

/// <summary>One of the account's current sessions, as <c>GET /v1/account/sessions</c> lists them.</summary>
public sealed record AccountSessionInfo(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, bool Current);

/// <summary>
/// The server accepted the password but the account has a second factor: a short-lived, single-step
/// token to present with a code from the authenticator app (or a recovery code). No session exists
/// yet. The token is a secret for that one step: held in memory, never logged, never shown.
/// </summary>
/// <param name="Methods">
/// The ways the server says this step can be completed (its <c>methods</c>). This client completes it
/// with a code only — <see cref="SecondFactorMethods.Totp"/> or <see cref="SecondFactorMethods.Recovery"/>;
/// <see cref="SecondFactorMethods.Passkey"/> only tells the user that the account has passkeys, which
/// are used on the web vault.
/// </param>
public sealed record SecondFactorChallenge(string MfaToken, DateTimeOffset ExpiresAt, SecondFactorMethods Methods = SecondFactorMethods.Code)
{
    public override string ToString() => $"SecondFactorChallenge {{ MfaToken = <redacted>, ExpiresAt = {ExpiresAt:O}, Methods = {Methods} }}";
}

/// <summary>
/// The second-factor methods a sign-in challenge offers, as the server lists them in <c>methods</c>
/// (<c>"totp"</c>, <c>"recovery"</c>, <c>"passkey"</c>). Values this client does not know are ignored;
/// a server that sends no list (before passkeys) offers the code step, TOTP or a recovery code.
/// </summary>
[Flags]
public enum SecondFactorMethods
{
    None = 0,

    /// <summary>A code from the authenticator app.</summary>
    Totp = 1,

    /// <summary>One of the account's recovery codes, entered where the authenticator code goes.</summary>
    Recovery = 2,

    /// <summary>A passkey. Used on the web vault only: this application has no WebAuthn.</summary>
    Passkey = 4,

    /// <summary>What a server without a <c>methods</c> list offers, and what this client can complete.</summary>
    Code = Totp | Recovery,
}

/// <summary>What <c>POST /v1/auth/login</c> answered: tokens (signed in), or a second-factor challenge.</summary>
public sealed record SignInResult(AccountTokens? Tokens, SecondFactorChallenge? Challenge)
{
    public bool RequiresSecondFactor => Challenge is not null;

    public static SignInResult SignedIn(AccountTokens tokens) => new(tokens, null);

    public static SignInResult SecondFactor(SecondFactorChallenge challenge) => new(null, challenge);

    // Spelled out so that nothing nested can ever print a secret, whatever the records above do.
    public override string ToString()
        => Tokens is not null
            ? "SignInResult { Tokens = <redacted> }"
            : $"SignInResult {{ Challenge = {Challenge} }}";
}

/// <summary>
/// The account's second-factor state, as <c>GET /v1/account/totp</c> reports it. Carries no secret:
/// whether TOTP is on, since when, and how many recovery codes are left. Read-only on the desktop;
/// setting it up and turning it off is done on vault.bitosx.se.
/// </summary>
public sealed record TwoFactorStatus(bool Enabled, DateTimeOffset? ConfirmedAt, int RecoveryCodesRemaining, bool PendingEnrollment);

/// <summary>
/// One of the account's passkeys, as <c>GET /v1/account/passkeys</c> lists it. Carries no key material
/// and no credential id: a name for people, when it was added and last used, and whether the
/// authenticator says it is backed up (synced). Read-only on the desktop; passkeys are added, renamed
/// and removed on vault.bitosx.se.
/// </summary>
public sealed record PasskeyInfo(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool BackedUp);

/// <summary>The account's passkeys and how many it may have (<c>max</c>).</summary>
public sealed record PasskeyList(IReadOnlyList<PasskeyInfo> Passkeys, int Max);
