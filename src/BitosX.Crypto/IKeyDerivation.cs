namespace BitosX.Crypto;

/// <summary>
/// Derives a key-encryption key (KEK) from a low-entropy master password using a
/// memory-hard password KDF.
/// </summary>
public interface IKeyDerivation
{
    /// <summary>Identifier of the KDF (persisted in the vault format), e.g. "argon2id".</summary>
    string Name { get; }

    /// <summary>
    /// Derives a key of <see cref="Argon2Parameters.KeyLengthBytes"/> bytes from the given
    /// password and salt. The caller owns the returned buffer and should wipe it when done.
    /// </summary>
    byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters);
}
