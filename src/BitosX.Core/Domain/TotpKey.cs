namespace BitosX.Core.Domain;

/// <summary>
/// The authenticator key (RFC 6238 TOTP) of a login: the shared secret and the three parameters
/// that, with the time, give its codes. As secret as the password: it lives in memory only while
/// the vault is unlocked, and is persisted only inside the encrypted vault. See docs/TOTP.md.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> leaves the secret out, so that printing an item (or a key) by accident
/// does not put it in a log or a message. Validation is <see cref="Validation.VaultValidation"/>'s.
/// </remarks>
public sealed record TotpKey
{
    /// <summary>
    /// The shared secret in canonical base32: RFC 4648's alphabet <c>A-Z2-7</c>, upper case, no
    /// padding, no spaces; the encoding of 10 to 128 bytes.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>The HMAC hash function.</summary>
    public TotpAlgorithm Algorithm { get; init; } = TotpAlgorithm.Sha1;

    /// <summary>The number of digits of a code: 6, 7 or 8.</summary>
    public int Digits { get; init; } = 6;

    /// <summary>The time step in seconds: 1 to 300.</summary>
    public int Period { get; init; } = 30;

    /// <summary>A description without the secret.</summary>
    public override string ToString()
        => $"TotpKey {{ Algorithm = {Algorithm}, Digits = {Digits}, Period = {Period} }}";
}
