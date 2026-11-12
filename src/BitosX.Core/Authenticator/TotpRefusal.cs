namespace BitosX.Core.Authenticator;

/// <summary>
/// Why a text is not an authenticator key (docs/TOTP.md §3, §6). Each has a code, which the
/// shared fixtures spell, and a fixed text, the same in both clients (<see cref="TotpTexts"/>).
/// </summary>
public enum TotpRefusal
{
    /// <summary><c>too_long</c>: more than 2048 UTF-16 code units.</summary>
    TooLong,

    /// <summary><c>steam_not_supported</c>: a <c>steam://</c> link.</summary>
    SteamNotSupported,

    /// <summary><c>invalid_link</c>: an <c>otpauth://</c> link that cannot be read.</summary>
    InvalidLink,

    /// <summary><c>hotp_not_supported</c>: an <c>otpauth://hotp/</c> link.</summary>
    HotpNotSupported,

    /// <summary><c>missing_secret</c>: a link without a <c>secret</c>, or with an empty one.</summary>
    MissingSecret,

    /// <summary><c>unsupported_algorithm</c>: an <c>algorithm</c> other than SHA1, SHA256, SHA512.</summary>
    UnsupportedAlgorithm,

    /// <summary><c>unsupported_digits</c>: <c>digits</c> other than 6, 7, 8.</summary>
    UnsupportedDigits,

    /// <summary><c>unsupported_period</c>: a <c>period</c> other than 1 to 300.</summary>
    UnsupportedPeriod,

    /// <summary><c>invalid_secret</c>: a character that is not base32.</summary>
    InvalidSecret,

    /// <summary><c>secret_too_short</c>: fewer than 10 bytes.</summary>
    SecretTooShort,

    /// <summary><c>secret_too_long</c>: more than 128 bytes.</summary>
    SecretTooLong,
}
