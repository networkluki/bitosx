using System.Security.Cryptography;

namespace BitosX.Crypto;

/// <summary>
/// AES-256-GCM authenticated encryption using the .NET base class library
/// (<see cref="AesGcm"/>). GCM provides confidentiality and integrity in a single
/// well-established primitive and is hardware accelerated (AES-NI) on modern CPUs.
/// </summary>
/// <remarks>
/// GCM security depends on never reusing a (key, nonce) pair. BitosX always generates a
/// fresh 96-bit random nonce for every encryption operation; see docs/CRYPTOGRAPHY.md.
/// </remarks>
public sealed class AesGcmCipher : IAuthenticatedCipher
{
    public string Name => CryptoConstants.AlgorithmAes256Gcm;

    public byte[] Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateKeyAndNonce(key, nonce);

        // Output layout: [ciphertext (== plaintext length)][tag (16 bytes)].
        byte[] output = new byte[plaintext.Length + CryptoConstants.TagSizeBytes];
        Span<byte> ciphertext = output.AsSpan(0, plaintext.Length);
        Span<byte> tag = output.AsSpan(plaintext.Length, CryptoConstants.TagSizeBytes);

        using var aes = new AesGcm(key, CryptoConstants.TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    public byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextWithTag,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateKeyAndNonce(key, nonce);

        if (ciphertextWithTag.Length < CryptoConstants.TagSizeBytes)
        {
            // Too short to even contain a tag: fail closed as an authentication failure.
            throw new VaultAuthenticationException();
        }

        int ciphertextLength = ciphertextWithTag.Length - CryptoConstants.TagSizeBytes;
        ReadOnlySpan<byte> ciphertext = ciphertextWithTag[..ciphertextLength];
        ReadOnlySpan<byte> tag = ciphertextWithTag.Slice(ciphertextLength, CryptoConstants.TagSizeBytes);

        byte[] plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(key, CryptoConstants.TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // Wipe any partially written plaintext and surface a single, non-revealing failure.
            CryptoBuffers.Wipe(plaintext);
            throw new VaultAuthenticationException(ex);
        }
    }

    private static void ValidateKeyAndNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != CryptoConstants.KeySizeBytes)
        {
            throw new ArgumentException($"Key must be {CryptoConstants.KeySizeBytes} bytes.", nameof(key));
        }

        if (nonce.Length != CryptoConstants.NonceSizeBytes)
        {
            throw new ArgumentException($"Nonce must be {CryptoConstants.NonceSizeBytes} bytes.", nameof(nonce));
        }
    }
}
