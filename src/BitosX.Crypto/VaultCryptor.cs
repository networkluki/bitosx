using System.Text;

namespace BitosX.Crypto;

/// <summary>
/// High-level vault encryption. Implements the BitosX key hierarchy:
/// <code>
/// master password --Argon2id(salt)--> key-encryption key (KEK)
/// random 256-bit vault key --AES-256-GCM(KEK)--> wrapped vault key
/// vault plaintext          --AES-256-GCM(vault key)--> ciphertext
/// </code>
/// The master password is only needed to create a vault or to unlock one; after unlocking,
/// only the vault key is retained (by the caller), so saving re-encrypts content without
/// re-running the KDF and without keeping the master password in memory.
/// </summary>
public sealed class VaultCryptor
{
    private readonly IKeyDerivation _keyDerivation;
    private readonly IAuthenticatedCipher _cipher;

    public VaultCryptor(IKeyDerivation keyDerivation, IAuthenticatedCipher cipher)
    {
        _keyDerivation = keyDerivation ?? throw new ArgumentNullException(nameof(keyDerivation));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    /// <summary>Creates the default cryptor (Argon2id + AES-256-GCM).</summary>
    public static VaultCryptor CreateDefault() => new(new Argon2idKeyDerivation(), new AesGcmCipher());

    /// <summary>
    /// Creates a brand new encrypted vault from plaintext, deriving a KEK from the master
    /// password and wrapping a freshly generated random vault key. Returns the encrypted vault
    /// together with the vault key so the caller can remain unlocked without re-deriving.
    /// The caller owns and must wipe <see cref="VaultCreationResult.VaultKey"/>.
    /// </summary>
    public VaultCreationResult Create(ReadOnlySpan<byte> masterPassword, ReadOnlySpan<byte> plaintext, Argon2Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        byte[] salt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
        byte[] kek = _keyDerivation.DeriveKey(masterPassword, salt, parameters);
        byte[] vaultKey = SecureRandom.GetBytes(CryptoConstants.KeySizeBytes);
        try
        {
            byte[] keyWrapNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] keyWrapAad = BuildKeyWrapAad(CryptoConstants.FormatVersion, parameters, salt);
            byte[] encryptedVaultKey = _cipher.Encrypt(kek, keyWrapNonce, vaultKey, keyWrapAad);

            byte[] contentNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] contentAad = BuildContentAad(CryptoConstants.FormatVersion);
            byte[] ciphertext = _cipher.Encrypt(vaultKey, contentNonce, plaintext, contentAad);

            var encrypted = new EncryptedVault(
                CryptoConstants.FormatVersion,
                _keyDerivation.Name,
                parameters,
                salt,
                _cipher.Name,
                keyWrapNonce,
                encryptedVaultKey,
                contentNonce,
                ciphertext);

            // Return a copy of the vault key; the local buffer is wiped in finally.
            return new VaultCreationResult(encrypted, vaultKey.AsSpan().ToArray());
        }
        finally
        {
            CryptoBuffers.Wipe(kek);
            CryptoBuffers.Wipe(vaultKey);
        }
    }

    /// <summary>
    /// Unlocks an encrypted vault: derives the KEK, unwraps the vault key, and decrypts the
    /// content. Throws <see cref="VaultAuthenticationException"/> on an incorrect password or
    /// any tampering (indistinguishable), and <see cref="UnsupportedVaultFormatException"/> for
    /// an unknown format. The caller owns and must wipe both returned buffers.
    /// </summary>
    public UnlockResult Unlock(EncryptedVault encrypted, ReadOnlySpan<byte> masterPassword)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ValidateFormat(encrypted);

        byte[] kek = _keyDerivation.DeriveKey(masterPassword, encrypted.Salt, encrypted.KdfParameters);
        byte[]? vaultKey = null;
        try
        {
            byte[] keyWrapAad = BuildKeyWrapAad(encrypted.Version, encrypted.KdfParameters, encrypted.Salt);
            vaultKey = _cipher.Decrypt(kek, encrypted.KeyWrapNonce, encrypted.EncryptedVaultKey, keyWrapAad);

            byte[] contentAad = BuildContentAad(encrypted.Version);
            byte[] plaintext = _cipher.Decrypt(vaultKey, encrypted.ContentNonce, encrypted.Ciphertext, contentAad);

            return new UnlockResult(vaultKey.AsSpan().ToArray(), encrypted.Header, plaintext);
        }
        finally
        {
            CryptoBuffers.Wipe(kek);
            CryptoBuffers.Wipe(vaultKey);
        }
    }

    /// <summary>
    /// Re-encrypts new plaintext under an already-unlocked vault key, reusing the existing
    /// header (KDF parameters, salt, wrapped key) and generating a fresh content nonce.
    /// Used to save changes without re-deriving the KEK or handling the master password.
    /// </summary>
    public EncryptedVault ReEncryptContent(EncryptedVaultHeader header, ReadOnlySpan<byte> vaultKey, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (vaultKey.Length != CryptoConstants.KeySizeBytes)
        {
            throw new ArgumentException("Invalid vault key length.", nameof(vaultKey));
        }

        byte[] contentNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
        byte[] contentAad = BuildContentAad(header.Version);
        byte[] ciphertext = _cipher.Encrypt(vaultKey, contentNonce, plaintext, contentAad);

        return new EncryptedVault(
            header.Version,
            header.Kdf,
            header.KdfParameters,
            header.Salt,
            header.Algorithm,
            header.KeyWrapNonce,
            header.EncryptedVaultKey,
            contentNonce,
            ciphertext);
    }

    /// <summary>
    /// Changes the master password by re-wrapping the SAME vault key under a key-encryption key
    /// derived from <paramref name="newPassword"/> and a fresh salt. The vault content is NOT
    /// re-encrypted: the content nonce and ciphertext are carried over unchanged (the content's
    /// associated data depends only on the format version and algorithm, not on the salt or KDF
    /// parameters). The current password is verified by authenticating the existing wrapped key, so
    /// a wrong password throws <see cref="VaultAuthenticationException"/> before anything new is
    /// produced. The vault key is not rotated and the KDF cost parameters are preserved; only the
    /// salt, key-wrap nonce, and wrapped key change.
    /// </summary>
    public EncryptedVault ChangeMasterPassword(
        EncryptedVault current,
        ReadOnlySpan<byte> currentPassword,
        ReadOnlySpan<byte> newPassword)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateFormat(current);

        byte[] oldKek = _keyDerivation.DeriveKey(currentPassword, current.Salt, current.KdfParameters);
        byte[]? vaultKey = null;
        byte[]? newKek = null;
        try
        {
            byte[] currentAad = BuildKeyWrapAad(current.Version, current.KdfParameters, current.Salt);
            // Authenticated unwrap; a wrong current password fails closed here, before any change.
            vaultKey = _cipher.Decrypt(oldKek, current.KeyWrapNonce, current.EncryptedVaultKey, currentAad);

            // Fresh salt and key-wrap nonce; keep the vault's existing KDF cost parameters.
            byte[] newSalt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
            newKek = _keyDerivation.DeriveKey(newPassword, newSalt, current.KdfParameters);

            byte[] newKeyWrapNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] newKeyWrapAad = BuildKeyWrapAad(current.Version, current.KdfParameters, newSalt);
            byte[] newEncryptedVaultKey = _cipher.Encrypt(newKek, newKeyWrapNonce, vaultKey, newKeyWrapAad);

            // Self-check: the freshly wrapped key must unwrap back to exactly the same vault key,
            // so an unusable vault can never be produced/persisted.
            byte[] check = _cipher.Decrypt(newKek, newKeyWrapNonce, newEncryptedVaultKey, newKeyWrapAad);
            try
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(check, vaultKey))
                {
                    throw new BitosXCryptographicException("Master password change self-check failed.");
                }
            }
            finally
            {
                CryptoBuffers.Wipe(check);
            }

            // Content carried over unchanged (same vault key; content AAD is independent of salt/KDF).
            return new EncryptedVault(
                current.Version,
                current.Kdf,
                current.KdfParameters,
                newSalt,
                current.Algorithm,
                newKeyWrapNonce,
                newEncryptedVaultKey,
                current.ContentNonce,
                current.Ciphertext);
        }
        finally
        {
            CryptoBuffers.Wipe(oldKek);
            CryptoBuffers.Wipe(newKek);
            CryptoBuffers.Wipe(vaultKey);
        }
    }

    private static void ValidateFormat(EncryptedVault encrypted)
    {
        if (encrypted.Version != CryptoConstants.FormatVersion)
        {
            throw new UnsupportedVaultFormatException($"Unsupported vault format version {encrypted.Version}.");
        }

        if (!string.Equals(encrypted.Kdf, CryptoConstants.KdfArgon2id, StringComparison.Ordinal))
        {
            throw new UnsupportedVaultFormatException($"Unsupported key-derivation function '{encrypted.Kdf}'.");
        }

        if (!string.Equals(encrypted.Algorithm, CryptoConstants.AlgorithmAes256Gcm, StringComparison.Ordinal))
        {
            throw new UnsupportedVaultFormatException($"Unsupported encryption algorithm '{encrypted.Algorithm}'.");
        }

        encrypted.KdfParameters.Validate();
    }

    /// <summary>
    /// Builds the associated data that binds the wrapped vault key to the KDF parameters and
    /// salt. Tampering with any of these public fields changes the AAD and causes the key-unwrap
    /// authentication to fail closed.
    /// </summary>
    private static byte[] BuildKeyWrapAad(int version, Argon2Parameters parameters, ReadOnlySpan<byte> salt)
    {
        string header = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"BitosX|kek|v{version}|{CryptoConstants.KdfArgon2id}|m={parameters.MemoryKiB}|t={parameters.Iterations}|p={parameters.DegreeOfParallelism}|klen={parameters.KeyLengthBytes}|salt={Convert.ToBase64String(salt)}");
        return Encoding.UTF8.GetBytes(header);
    }

    /// <summary>Builds the associated data that binds the content ciphertext to the format version and algorithm.</summary>
    private static byte[] BuildContentAad(int version)
    {
        string header = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"BitosX|content|v{version}|{CryptoConstants.AlgorithmAes256Gcm}");
        return Encoding.UTF8.GetBytes(header);
    }
}

/// <summary>Result of creating a new vault: the encrypted vault and the (caller-owned) vault key.</summary>
public sealed record VaultCreationResult(EncryptedVault Encrypted, byte[] VaultKey);

/// <summary>
/// Result of unlocking a vault. <see cref="VaultKey"/> and <see cref="Plaintext"/> are
/// caller-owned sensitive buffers that must be wiped when no longer needed.
/// </summary>
public sealed record UnlockResult(byte[] VaultKey, EncryptedVaultHeader Header, byte[] Plaintext);
