namespace BitosX.Crypto;

/// <summary>
/// An authenticated encryption with associated data (AEAD) primitive.
/// </summary>
public interface IAuthenticatedCipher
{
    /// <summary>Identifier of the algorithm (persisted in the vault format), e.g. "AES-256-GCM".</summary>
    string Name { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns the ciphertext with the authentication
    /// tag appended (ciphertext || tag). The <paramref name="associatedData"/> is authenticated
    /// but not encrypted.
    /// </summary>
    byte[] Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData);

    /// <summary>
    /// Verifies and decrypts <paramref name="ciphertextWithTag"/> (ciphertext || tag).
    /// Throws <see cref="VaultAuthenticationException"/> if authentication fails.
    /// </summary>
    byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextWithTag,
        ReadOnlySpan<byte> associatedData);
}
