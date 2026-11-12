namespace BitosX.Core.Domain;

/// <summary>
/// The HMAC hash function of an authenticator key (RFC 6238 §1.2). Stored in the vault as the
/// strings <c>SHA1</c>, <c>SHA256</c> and <c>SHA512</c>; see docs/TOTP.md §2.
/// </summary>
public enum TotpAlgorithm
{
    /// <summary>HMAC-SHA-1: what nearly every site uses, and the default of an otpauth:// link.</summary>
    Sha1,

    /// <summary>HMAC-SHA-256.</summary>
    Sha256,

    /// <summary>HMAC-SHA-512.</summary>
    Sha512,
}
