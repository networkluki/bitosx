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

/// <summary>
/// Thrown when the key cannot be derived because the device does not have the memory the vault's
/// key-derivation settings ask for. Nothing is wrong with the vault or the password: the same vault
/// opens on a device with enough free memory. Raised before anything is decrypted or written.
/// </summary>
public sealed class KeyDerivationResourceException : BitosXCryptographicException
{
    public KeyDerivationResourceException(int memoryKiB, Exception innerException)
        : base(
            $"This device does not have enough free memory to derive the key ({memoryKiB / 1024} MiB is needed).",
            innerException)
    {
        MemoryKiB = memoryKiB;
    }

    /// <summary>The memory the key derivation asked for, in kibibytes.</summary>
    public int MemoryKiB { get; }
}

/// <summary>
/// Thrown when a vault is asked to move to key-derivation settings that are weaker than the ones it
/// has. Settings can be strengthened, never weakened: whoever holds a copy of the vault as it is now
/// keeps the benefit of the old settings, so weakening could only ever help an attacker.
/// </summary>
public sealed class WeakerKeyDerivationException : BitosXCryptographicException
{
    public WeakerKeyDerivationException()
        : base("The new key-derivation settings must be stronger than the ones the vault already uses.") { }
}
