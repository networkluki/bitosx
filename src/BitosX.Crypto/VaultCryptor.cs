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
        byte[] kek = DeriveToProtect(masterPassword, salt, parameters);
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

        byte[] kek = DeriveToOpen(masterPassword, encrypted.Salt, encrypted.KdfParameters);
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

        return ReWrap(current, currentPassword, newPassword, current.KdfParameters);
    }

    /// <summary>
    /// Moves the vault to stronger key-derivation settings by re-wrapping the SAME vault key under a
    /// key-encryption key derived with <paramref name="newParameters"/> and a fresh salt. Like a
    /// master-password change, this does not rotate the vault key and does not re-encrypt the
    /// content. The current password is verified by authenticating the existing wrapped key, so a
    /// wrong password throws <see cref="VaultAuthenticationException"/> before anything new is
    /// produced.
    /// </summary>
    /// <param name="current">The vault as it is stored.</param>
    /// <param name="currentPassword">The password encoding the vault is currently wrapped under.</param>
    /// <param name="wrapPassword">
    /// The password encoding to wrap under. The same password, in its canonical encoding: this is
    /// how a vault that was created with another encoding of the password moves to the canonical one.
    /// </param>
    /// <param name="newParameters">
    /// The settings to move to. They must be stronger than the vault's own
    /// (<see cref="WeakerKeyDerivationException"/> otherwise) and within the bounds of the format.
    /// </param>
    public EncryptedVault ChangeKeyDerivation(
        EncryptedVault current,
        ReadOnlySpan<byte> currentPassword,
        ReadOnlySpan<byte> wrapPassword,
        Argon2Parameters newParameters)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newParameters);
        ValidateFormat(current);
        newParameters.Validate();

        if (!newParameters.IsStrongerThan(current.KdfParameters))
        {
            throw new WeakerKeyDerivationException();
        }

        return ReWrap(current, currentPassword, wrapPassword, newParameters);
    }

    /// <summary>
    /// Unwraps the vault key with <paramref name="unwrapPassword"/> and the vault's own settings, and
    /// wraps it again under <paramref name="wrapPassword"/> and <paramref name="wrapParameters"/> with
    /// a fresh salt and nonce. The content is carried over unchanged.
    /// </summary>
    private EncryptedVault ReWrap(
        EncryptedVault current,
        ReadOnlySpan<byte> unwrapPassword,
        ReadOnlySpan<byte> wrapPassword,
        Argon2Parameters wrapParameters)
    {
        byte[] oldKek = DeriveToOpen(unwrapPassword, current.Salt, current.KdfParameters);
        byte[]? vaultKey = null;
        byte[]? newKek = null;
        try
        {
            byte[] currentAad = BuildKeyWrapAad(current.Version, current.KdfParameters, current.Salt);
            // Authenticated unwrap; a wrong current password fails closed here, before any change.
            vaultKey = _cipher.Decrypt(oldKek, current.KeyWrapNonce, current.EncryptedVaultKey, currentAad);

            // Fresh salt and key-wrap nonce.
            byte[] newSalt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
            newKek = DeriveToProtect(wrapPassword, newSalt, wrapParameters);

            byte[] newKeyWrapNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] newKeyWrapAad = BuildKeyWrapAad(current.Version, wrapParameters, newSalt);
            byte[] newEncryptedVaultKey = _cipher.Encrypt(newKek, newKeyWrapNonce, vaultKey, newKeyWrapAad);

            // Self-check: the freshly wrapped key must unwrap back to exactly the same vault key,
            // so an unusable vault can never be produced/persisted.
            byte[] check = _cipher.Decrypt(newKek, newKeyWrapNonce, newEncryptedVaultKey, newKeyWrapAad);
            try
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(check, vaultKey))
                {
                    throw new BitosXCryptographicException("Re-wrapping the vault key failed its self-check.");
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
                wrapParameters,
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

    /// <summary>
    /// Rotates the vault key: derives the KEK from <paramref name="currentPassword"/>, authenticates and
    /// decrypts the existing vault, then generates a brand-new random vault key and <b>re-encrypts all
    /// content under it</b>. Fresh cryptographic material is produced throughout — new vault key, new
    /// salt, new key-wrap nonce, and new content nonce — while the master password, on-disk format
    /// version (v1), and Argon2 cost parameters are preserved. A wrong current password (or any tampering
    /// with the existing vault) fails closed via the authenticated decrypt <i>before</i> any new key is
    /// generated. Before returning, a self-check confirms the rotated envelope unwraps to the new key and
    /// decrypts back to the exact original plaintext, so an unusable vault can never be produced. Returns
    /// the rotated vault together with the new vault key so the caller can stay unlocked; the caller owns
    /// and must wipe <see cref="VaultKeyRotationResult.NewVaultKey"/>.
    /// </summary>
    public VaultKeyRotationResult RotateVaultKey(EncryptedVault current, ReadOnlySpan<byte> currentPassword)
        => RotateVaultKey(current, currentPassword, currentPassword);

    /// <summary>
    /// Rotates the vault key as <see cref="RotateVaultKey(EncryptedVault, ReadOnlySpan{byte})"/> does,
    /// wrapping the new key under <paramref name="wrapPassword"/>: the same password in its canonical
    /// encoding. This is how a vault that was created with another encoding of the password moves to
    /// the canonical one.
    /// </summary>
    public VaultKeyRotationResult RotateVaultKey(
        EncryptedVault current,
        ReadOnlySpan<byte> currentPassword,
        ReadOnlySpan<byte> wrapPassword)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateFormat(current);

        byte[] currentKek = DeriveToOpen(currentPassword, current.Salt, current.KdfParameters);
        byte[]? oldVaultKey = null;
        byte[]? plaintext = null;
        byte[]? newVaultKey = null;
        byte[]? newKek = null;
        try
        {
            // Authenticated unwrap + content decrypt of the EXISTING vault. A wrong current password or
            // any tampering throws here, before a new key or salt is generated.
            byte[] currentKeyWrapAad = BuildKeyWrapAad(current.Version, current.KdfParameters, current.Salt);
            oldVaultKey = _cipher.Decrypt(currentKek, current.KeyWrapNonce, current.EncryptedVaultKey, currentKeyWrapAad);

            byte[] contentAad = BuildContentAad(current.Version);
            plaintext = _cipher.Decrypt(oldVaultKey, current.ContentNonce, current.Ciphertext, contentAad);

            // Fresh cryptographic material (D3): new vault key, salt, key-wrap nonce, content nonce.
            newVaultKey = SecureRandom.GetBytes(CryptoConstants.KeySizeBytes);
            byte[] newSalt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
            newKek = DeriveToProtect(wrapPassword, newSalt, current.KdfParameters);

            byte[] newKeyWrapNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] newKeyWrapAad = BuildKeyWrapAad(current.Version, current.KdfParameters, newSalt);
            byte[] newEncryptedVaultKey = _cipher.Encrypt(newKek, newKeyWrapNonce, newVaultKey, newKeyWrapAad);

            // Full re-encryption of the content under the new vault key.
            byte[] newContentNonce = SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);
            byte[] newCiphertext = _cipher.Encrypt(newVaultKey, newContentNonce, plaintext, contentAad);

            var rotated = new EncryptedVault(
                current.Version,        // format unchanged (v1) — D1
                current.Kdf,
                current.KdfParameters,  // Argon2 cost parameters preserved — D3
                newSalt,
                current.Algorithm,
                newKeyWrapNonce,
                newEncryptedVaultKey,
                newContentNonce,
                newCiphertext);

            // Self-check (D4): the rotated envelope must unwrap to the new key AND decrypt to the exact
            // original plaintext, so an unreadable vault can never be produced/persisted.
            byte[] checkKey = _cipher.Decrypt(newKek, newKeyWrapNonce, newEncryptedVaultKey, newKeyWrapAad);
            try
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(checkKey, newVaultKey))
                {
                    throw new BitosXCryptographicException("Vault key rotation self-check (key wrap) failed.");
                }
            }
            finally
            {
                CryptoBuffers.Wipe(checkKey);
            }

            byte[] checkPlaintext = _cipher.Decrypt(newVaultKey, newContentNonce, newCiphertext, contentAad);
            try
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(checkPlaintext, plaintext))
                {
                    throw new BitosXCryptographicException("Vault key rotation self-check (content) failed.");
                }
            }
            finally
            {
                CryptoBuffers.Wipe(checkPlaintext);
            }

            // Return a copy of the new vault key; the local buffer is wiped in finally.
            return new VaultKeyRotationResult(rotated, newVaultKey.AsSpan().ToArray());
        }
        finally
        {
            CryptoBuffers.Wipe(currentKek);
            CryptoBuffers.Wipe(newKek);
            CryptoBuffers.Wipe(oldVaultKey);
            CryptoBuffers.Wipe(plaintext);
            CryptoBuffers.Wipe(newVaultKey);
        }
    }

    /// <summary>
    /// Derives the key for a password that is an attempt at opening a vault. No vault is keyed with
    /// an empty password (see <see cref="DeriveToProtect"/>), so as an attempt it is a wrong one,
    /// and is answered like every other wrong password.
    /// </summary>
    private byte[] DeriveToOpen(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
    {
        if (password.IsEmpty)
        {
            throw new VaultAuthenticationException();
        }

        return _keyDerivation.DeriveKey(password, salt, parameters);
    }

    /// <summary>Derives the key for a password that is to protect a vault, which an empty one cannot.</summary>
    private byte[] DeriveToProtect(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("A vault cannot be protected by an empty password.", nameof(password));
        }

        return _keyDerivation.DeriveKey(password, salt, parameters);
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
/// Result of rotating the vault key: the re-encrypted vault and the new (caller-owned) vault key.
/// <see cref="NewVaultKey"/> is a sensitive buffer that must be wiped when no longer needed.
/// </summary>
public sealed record VaultKeyRotationResult(EncryptedVault Encrypted, byte[] NewVaultKey);

/// <summary>
/// Result of unlocking a vault. <see cref="VaultKey"/> and <see cref="Plaintext"/> are
/// caller-owned sensitive buffers that must be wiped when no longer needed.
/// </summary>
public sealed record UnlockResult(byte[] VaultKey, EncryptedVaultHeader Header, byte[] Plaintext);
