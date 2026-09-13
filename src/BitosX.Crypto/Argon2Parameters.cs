namespace BitosX.Crypto;

/// <summary>
/// Argon2id cost parameters. These are persisted alongside the vault so that a vault
/// created with one cost profile can always be opened later, and so the profile can be
/// upgraded over time.
/// </summary>
/// <param name="MemoryKiB">Memory cost in kibibytes (1024 = 1 MiB).</param>
/// <param name="Iterations">Time cost (number of passes).</param>
/// <param name="DegreeOfParallelism">Number of lanes / threads.</param>
/// <param name="KeyLengthBytes">Length of the derived key-encryption key in bytes.</param>
public sealed record Argon2Parameters(
    int MemoryKiB,
    int Iterations,
    int DegreeOfParallelism,
    int KeyLengthBytes)
{
    // Guard rails for parameters read from an untrusted vault file. The lower bounds reject
    // nonsensical or dangerously weak values; the UPPER bounds are a denial-of-service defence.
    // Argon2 runs BEFORE the wrapped key is authenticated, so a tampered or malicious file could
    // otherwise request absurd memory/iteration/parallelism costs and exhaust the machine on unlock.
    // The ceilings sit far above the interactive default (64 MiB / 3 / 4) to leave headroom for
    // future, stronger device-aware profiles; a profile beyond these bounds requires a format
    // version bump that widens them (see docs/CRYPTOGRAPHY.md — "KDF parameter bounds").
    private const int MinMemoryKiB = 8;              // 8 KiB absolute floor accepted from a file
    private const int MaxMemoryKiB = 1024 * 1024;    // 1 GiB ceiling — prevents absurd allocation
    private const int MinIterations = 1;
    private const int MaxIterations = 64;            // far above any interactive setting; bounds CPU cost
    private const int MinParallelism = 1;
    private const int MaxParallelism = 64;

    /// <summary>
    /// Interactive default profile: 64 MiB, 3 passes, 4 lanes, 32-byte key.
    /// Aligned with current OWASP guidance for Argon2id on interactive logins.
    /// </summary>
    public static Argon2Parameters Default { get; } = new(
        MemoryKiB: 64 * 1024,
        Iterations: 3,
        DegreeOfParallelism: 4,
        KeyLengthBytes: CryptoConstants.KeySizeBytes);

    /// <summary>
    /// Deliberately weak profile used ONLY to keep automated tests fast. Never use for real vaults.
    /// </summary>
    public static Argon2Parameters ForTesting { get; } = new(
        MemoryKiB: 1024,
        Iterations: 1,
        DegreeOfParallelism: 1,
        KeyLengthBytes: CryptoConstants.KeySizeBytes);

    /// <summary>
    /// Validates the parameters, throwing <see cref="UnsupportedVaultFormatException"/> if any
    /// value is outside the accepted range. Used both for freshly configured parameters and for
    /// parameters read from a vault file.
    /// </summary>
    public void Validate()
    {
        if (MemoryKiB < MinMemoryKiB)
        {
            throw new UnsupportedVaultFormatException($"Argon2 memory cost is too low ({MemoryKiB} KiB).");
        }

        if (MemoryKiB > MaxMemoryKiB)
        {
            throw new UnsupportedVaultFormatException(
                $"Argon2 memory cost is too high ({MemoryKiB} KiB; limit {MaxMemoryKiB} KiB).");
        }

        if (Iterations < MinIterations)
        {
            throw new UnsupportedVaultFormatException($"Argon2 iteration count is too low ({Iterations}).");
        }

        if (Iterations > MaxIterations)
        {
            throw new UnsupportedVaultFormatException(
                $"Argon2 iteration count is too high ({Iterations}; limit {MaxIterations}).");
        }

        if (DegreeOfParallelism is < MinParallelism or > MaxParallelism)
        {
            throw new UnsupportedVaultFormatException($"Argon2 parallelism is out of range ({DegreeOfParallelism}).");
        }

        if (KeyLengthBytes != CryptoConstants.KeySizeBytes)
        {
            throw new UnsupportedVaultFormatException($"Unsupported derived key length ({KeyLengthBytes} bytes).");
        }

        // Argon2 requires at least 8 * parallelism blocks of memory.
        if (MemoryKiB < 8 * DegreeOfParallelism)
        {
            throw new UnsupportedVaultFormatException("Argon2 memory cost is too low for the requested parallelism.");
        }
    }
}
