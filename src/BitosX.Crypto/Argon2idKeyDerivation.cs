using Konscious.Security.Cryptography;

namespace BitosX.Crypto;

/// <summary>
/// Argon2id key derivation backed by the pure-managed
/// <c>Konscious.Security.Cryptography.Argon2</c> library.
/// </summary>
/// <remarks>
/// Argon2id is a memory-hard password KDF and is intentionally slow and memory intensive
/// to raise the cost of offline brute-force attacks against a stolen vault file. It is not
/// part of the .NET base class library, so a verified third-party implementation is used.
/// </remarks>
public sealed class Argon2idKeyDerivation : IKeyDerivation
{
    private const int MinSaltLengthBytes = 8; // Argon2 specification minimum.

    public string Name => CryptoConstants.KdfArgon2id;

    public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
    {
        parameters.Validate();

        if (salt.Length < MinSaltLengthBytes)
        {
            throw new UnsupportedVaultFormatException("The vault salt is too short.");
        }

        // Konscious requires managed arrays; copy into short-lived buffers and wipe them afterwards.
        byte[] passwordBytes = password.ToArray();
        byte[] saltBytes = salt.ToArray();
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = saltBytes,
                DegreeOfParallelism = parameters.DegreeOfParallelism,
                Iterations = parameters.Iterations,
                MemorySize = parameters.MemoryKiB, // Konscious measures memory in kibibytes.
            };

            return argon2.GetBytes(parameters.KeyLengthBytes);
        }
        finally
        {
            CryptoBuffers.Wipe(passwordBytes);
            CryptoBuffers.Wipe(saltBytes);
        }
    }
}
