namespace BitosX.Crypto;

/// <summary>
/// Central, immutable constants describing the BitosX encrypted vault format.
/// Changing any of these values is a cryptographic format change and requires a
/// new <see cref="FormatVersion"/> plus an upgrade path in the serializer.
/// </summary>
public static class CryptoConstants
{
    /// <summary>Current on-disk encrypted vault format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Identifier of the key-derivation function used to derive the key-encryption key.</summary>
    public const string KdfArgon2id = "argon2id";

    /// <summary>Identifier of the authenticated encryption algorithm used for wrapping and content.</summary>
    public const string AlgorithmAes256Gcm = "AES-256-GCM";

    /// <summary>Salt length for the KDF, in bytes (128 bits).</summary>
    public const int SaltSizeBytes = 16;

    /// <summary>AES-GCM nonce length, in bytes (96 bits, the recommended size).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>AES-GCM authentication tag length, in bytes (128 bits).</summary>
    public const int TagSizeBytes = 16;

    /// <summary>Vault key and key-encryption key length, in bytes (256 bits).</summary>
    public const int KeySizeBytes = 32;
}
