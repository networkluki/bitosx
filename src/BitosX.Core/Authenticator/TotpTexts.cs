namespace BitosX.Core.Authenticator;

/// <summary>
/// The codes and the fixed user-facing texts of docs/TOTP.md §6. The web vault shows the same
/// words. No text quotes what was entered.
/// </summary>
public static class TotpTexts
{
    /// <summary>The code of a refusal, as the fixtures spell it.</summary>
    public static string Code(TotpRefusal refusal) => refusal switch
    {
        TotpRefusal.TooLong => "too_long",
        TotpRefusal.SteamNotSupported => "steam_not_supported",
        TotpRefusal.InvalidLink => "invalid_link",
        TotpRefusal.HotpNotSupported => "hotp_not_supported",
        TotpRefusal.MissingSecret => "missing_secret",
        TotpRefusal.UnsupportedAlgorithm => "unsupported_algorithm",
        TotpRefusal.UnsupportedDigits => "unsupported_digits",
        TotpRefusal.UnsupportedPeriod => "unsupported_period",
        TotpRefusal.InvalidSecret => "invalid_secret",
        TotpRefusal.SecretTooShort => "secret_too_short",
        TotpRefusal.SecretTooLong => "secret_too_long",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>The fixed text of a refusal, shown under the editor's field.</summary>
    public static string Text(TotpRefusal refusal) => refusal switch
    {
        TotpRefusal.TooLong =>
            "This is too long to be an authenticator key or an otpauth:// link.",
        TotpRefusal.SteamNotSupported =>
            "Steam Guard codes are not supported. BitosX makes standard time-based codes (TOTP) only.",
        TotpRefusal.InvalidLink =>
            "This otpauth:// link cannot be read. Copy the link again, or enter the key itself.",
        TotpRefusal.HotpNotSupported =>
            "This link is for counter-based codes (HOTP), which BitosX does not support. BitosX makes time-based codes (TOTP) only.",
        TotpRefusal.MissingSecret =>
            "This otpauth:// link holds no key.",
        TotpRefusal.UnsupportedAlgorithm =>
            "This link asks for an algorithm BitosX does not support. BitosX supports SHA1, SHA256 and SHA512.",
        TotpRefusal.UnsupportedDigits =>
            "This link asks for codes of a length BitosX does not support. BitosX makes codes of 6, 7 or 8 digits.",
        TotpRefusal.UnsupportedPeriod =>
            "This link asks for a time step BitosX does not support. BitosX supports 1 to 300 seconds.",
        TotpRefusal.InvalidSecret =>
            "The key can hold only the letters A to Z and the digits 2 to 7. Spaces and dashes are ignored.",
        TotpRefusal.SecretTooShort =>
            "The key is too short. An authenticator key has at least 16 characters.",
        TotpRefusal.SecretTooLong =>
            "The key is too long to be an authenticator key.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>The name of an algorithm as it is stored and as a link spells it.</summary>
    public static string AlgorithmName(Domain.TotpAlgorithm algorithm) => algorithm switch
    {
        Domain.TotpAlgorithm.Sha1 => "SHA1",
        Domain.TotpAlgorithm.Sha256 => "SHA256",
        Domain.TotpAlgorithm.Sha512 => "SHA512",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}
