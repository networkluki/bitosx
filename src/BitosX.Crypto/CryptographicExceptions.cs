namespace BitosX.Crypto;

/// <summary>Base type for all BitosX cryptographic failures.</summary>
public class BitosXCryptographicException : Exception
{
    public BitosXCryptographicException(string message) : base(message) { }
    public BitosXCryptographicException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when authenticated decryption fails. This deliberately does not distinguish
/// between an incorrect master password and tampered ciphertext / associated data:
/// both fail closed with the same exception and message, revealing no information about
/// which cryptographic component was invalid.
/// </summary>
public sealed class VaultAuthenticationException : BitosXCryptographicException
{
    public VaultAuthenticationException()
        : base("The vault could not be decrypted. The master password is incorrect or the vault file has been modified.") { }

    public VaultAuthenticationException(Exception innerException)
        : base("The vault could not be decrypted. The master password is incorrect or the vault file has been modified.", innerException) { }
}

/// <summary>
/// Thrown when a vault file cannot be parsed, uses an unknown format version, or names an
/// unsupported KDF / algorithm, or contains structurally invalid fields. The format fails
/// closed: unknown or malformed formats are never processed and there is no plaintext fallback.
/// </summary>
public sealed class UnsupportedVaultFormatException : BitosXCryptographicException
{
    public UnsupportedVaultFormatException(string message) : base(message) { }
    public UnsupportedVaultFormatException(string message, Exception innerException) : base(message, innerException) { }
}
