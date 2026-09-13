namespace BitosX.Crypto;

/// <summary>
/// The complete encrypted vault as held in memory. All byte arrays are ciphertext or
/// public parameters; this type never contains plaintext secrets or key material.
/// </summary>
public sealed record EncryptedVault(
    int Version,
    string Kdf,
    Argon2Parameters KdfParameters,
    byte[] Salt,
    string Algorithm,
    byte[] KeyWrapNonce,
    byte[] EncryptedVaultKey,
    byte[] ContentNonce,
    byte[] Ciphertext)
{
    /// <summary>The portion of the format required to unwrap the vault key (everything except the content).</summary>
    public EncryptedVaultHeader Header =>
        new(Version, Kdf, KdfParameters, Salt, Algorithm, KeyWrapNonce, EncryptedVaultKey);
}

/// <summary>
/// The header of an encrypted vault: KDF parameters, salt, and the wrapped vault key.
/// Retained in memory while a vault is unlocked so that content can be re-encrypted on save
/// without re-deriving the key-encryption key or retaining the master password.
/// </summary>
public sealed record EncryptedVaultHeader(
    int Version,
    string Kdf,
    Argon2Parameters KdfParameters,
    byte[] Salt,
    string Algorithm,
    byte[] KeyWrapNonce,
    byte[] EncryptedVaultKey);
