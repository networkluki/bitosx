using System.Security.Cryptography;

namespace BitosX.Crypto;

/// <summary>
/// Thin wrapper over the platform cryptographically secure random number generator
/// (<see cref="RandomNumberGenerator"/>). All cryptographic secrets (keys, salts,
/// nonces) MUST be produced through this type. Never use <see cref="System.Random"/>,
/// timestamps, or predictable counters for cryptographic material.
/// </summary>
public static class SecureRandom
{
    /// <summary>Returns a new array of <paramref name="count"/> cryptographically random bytes.</summary>
    public static byte[] GetBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RandomNumberGenerator.GetBytes(count);
    }

    /// <summary>Fills <paramref name="buffer"/> with cryptographically random bytes.</summary>
    public static void Fill(Span<byte> buffer) => RandomNumberGenerator.Fill(buffer);

    /// <summary>
    /// Returns a uniformly distributed random integer in the range [0, <paramref name="toExclusive"/>).
    /// Uses <see cref="RandomNumberGenerator.GetInt32(int)"/>, which is free of modulo bias.
    /// </summary>
    public static int GetInt32(int toExclusive) => RandomNumberGenerator.GetInt32(toExclusive);
}
