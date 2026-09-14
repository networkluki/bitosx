using System.Security.Cryptography;
using System.Text;

namespace BitosX.Crypto;

/// <summary>
/// Computes a stable SHA-256 fingerprint over the <b>entire</b> encrypted-vault envelope — every
/// cryptographic field and the ciphertext. Two envelopes with the same fingerprint are cryptographically
/// identical and represent the same encrypted plaintext state; <b>any</b> external change — a different
/// vault key/wrapped key, salt, KDF parameters, nonce, or content — produces a different fingerprint.
/// </summary>
/// <remarks>
/// Used to detect that the on-disk vault still corresponds to the state the current session loaded or
/// last persisted, before a destructive operation such as vault key rotation. The baseline fingerprint
/// is held in trusted process memory and compared against the freshly-read on-disk envelope, so a plain
/// hash (not a keyed MAC) is sufficient: an attacker cannot make a <i>different</i> envelope collide
/// with the retained fingerprint. This is a content fingerprint, not a timestamp — it never relies on
/// file modification times.
/// </remarks>
public static class EncryptedVaultFingerprint
{
    /// <summary>Computes the SHA-256 fingerprint of the encrypted vault envelope.</summary>
    public static byte[] Compute(EncryptedVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            // Length-prefixed, fixed field order so distinct envelopes can never hash the same.
            writer.Write(vault.Version);
            writer.Write(vault.Kdf);
            writer.Write(vault.KdfParameters.MemoryKiB);
            writer.Write(vault.KdfParameters.Iterations);
            writer.Write(vault.KdfParameters.DegreeOfParallelism);
            writer.Write(vault.KdfParameters.KeyLengthBytes);
            WriteBlob(writer, vault.Salt);
            writer.Write(vault.Algorithm);
            WriteBlob(writer, vault.KeyWrapNonce);
            WriteBlob(writer, vault.EncryptedVaultKey);
            WriteBlob(writer, vault.ContentNonce);
            WriteBlob(writer, vault.Ciphertext);
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteBlob(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }
}
