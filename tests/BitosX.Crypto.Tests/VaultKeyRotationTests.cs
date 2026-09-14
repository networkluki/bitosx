using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

/// <summary>
/// Tests for vault key rotation (BitosX 0.5.0): generate a fresh random vault key and re-encrypt
/// all content under it, keeping the on-disk format (v1) and Argon2 cost parameters unchanged.
/// </summary>
public sealed class VaultKeyRotationTests
{
    private static readonly Argon2Parameters FastParams = Argon2Parameters.ForTesting;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    private static byte[] Password(string p) => Encoding.UTF8.GetBytes(p);
    private static byte[] Plaintext(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Rotate_ProducesNewVaultKey_AndPreservesPlaintext()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("vault-data"), FastParams);
        UnlockResult before = _cryptor.Unlock(created.Encrypted, Password("pw"));

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        // The vault key is genuinely new.
        Assert.Equal(CryptoConstants.KeySizeBytes, rotated.NewVaultKey.Length);
        Assert.NotEqual(before.VaultKey, rotated.NewVaultKey);

        // The same master password still unlocks, yielding the new key and the original content.
        UnlockResult after = _cryptor.Unlock(rotated.Encrypted, Password("pw"));
        Assert.Equal("vault-data", Encoding.UTF8.GetString(after.Plaintext));
        Assert.Equal(rotated.NewVaultKey, after.VaultKey);
    }

    [Fact]
    public void Rotate_KeepsFormatVersion_AndKdfParameters()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        Assert.Equal(CryptoConstants.FormatVersion, rotated.Encrypted.Version); // v1, unchanged (D1)
        Assert.Equal(created.Encrypted.Version, rotated.Encrypted.Version);
        Assert.Equal(created.Encrypted.Kdf, rotated.Encrypted.Kdf);
        Assert.Equal(created.Encrypted.Algorithm, rotated.Encrypted.Algorithm);
        Assert.Equal(created.Encrypted.KdfParameters, rotated.Encrypted.KdfParameters); // D3: params preserved
    }

    [Fact]
    public void Rotate_GeneratesFreshSaltNoncesAndReEncryptsContent()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        Assert.NotEqual(created.Encrypted.Salt, rotated.Encrypted.Salt);
        Assert.NotEqual(created.Encrypted.KeyWrapNonce, rotated.Encrypted.KeyWrapNonce);
        Assert.NotEqual(created.Encrypted.ContentNonce, rotated.Encrypted.ContentNonce);
        Assert.NotEqual(created.Encrypted.EncryptedVaultKey, rotated.Encrypted.EncryptedVaultKey);
        Assert.NotEqual(created.Encrypted.Ciphertext, rotated.Encrypted.Ciphertext); // content re-encrypted
    }

    [Fact]
    public void Rotate_FreshMaterialEachTime()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        VaultKeyRotationResult a = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));
        VaultKeyRotationResult b = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        Assert.NotEqual(a.NewVaultKey, b.NewVaultKey);
        Assert.NotEqual(a.Encrypted.Salt, b.Encrypted.Salt);
        Assert.NotEqual(a.Encrypted.KeyWrapNonce, b.Encrypted.KeyWrapNonce);
        Assert.NotEqual(a.Encrypted.ContentNonce, b.Encrypted.ContentNonce);
    }

    [Fact]
    public void Rotate_OldVaultKey_CanNoLongerDecryptContent()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("secret"), FastParams);
        UnlockResult before = _cryptor.Unlock(created.Encrypted, Password("pw"));

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        var cipher = new AesGcmCipher();
        byte[] contentAad = Encoding.UTF8.GetBytes("BitosX|content|v1|AES-256-GCM");
        Assert.Throws<VaultAuthenticationException>(
            () => cipher.Decrypt(before.VaultKey, rotated.Encrypted.ContentNonce, rotated.Encrypted.Ciphertext, contentAad));
    }

    [Fact]
    public void Rotate_NewPasswordIsUnchanged_OldPasswordStillRequired()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        // Rotation does not change the master password.
        _ = _cryptor.Unlock(rotated.Encrypted, Password("pw"));
        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(rotated.Encrypted, Password("different")));
    }

    [Fact]
    public void Rotate_WrongCurrentPassword_ThrowsAndProducesNothing()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        Assert.Throws<VaultAuthenticationException>(
            () => _cryptor.RotateVaultKey(created.Encrypted, Password("wrong")));
    }

    [Fact]
    public void Rotate_TamperedContent_FailsClosed_BeforeAnyRotation()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        created.Encrypted.Ciphertext[0] ^= 0xFF;

        // The existing vault is authenticated-decrypted first; tampering fails before a new key exists.
        Assert.Throws<VaultAuthenticationException>(
            () => _cryptor.RotateVaultKey(created.Encrypted, Password("pw")));
    }

    [Fact]
    public void Rotate_UnsupportedVersion_ThrowsFormatException()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        var tampered = created.Encrypted with { Version = 999 };

        Assert.Throws<UnsupportedVaultFormatException>(() => _cryptor.RotateVaultKey(tampered, Password("pw")));
    }

    [Fact]
    public void Rotate_TwiceInARow_ChainsCorrectly()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        VaultKeyRotationResult first = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));
        VaultKeyRotationResult second = _cryptor.RotateVaultKey(first.Encrypted, Password("pw"));

        Assert.NotEqual(first.NewVaultKey, second.NewVaultKey);
        UnlockResult after = _cryptor.Unlock(second.Encrypted, Password("pw"));
        Assert.Equal("data", Encoding.UTF8.GetString(after.Plaintext));
    }

    [Fact]
    public void Rotate_EmptyVault_Succeeds()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), ReadOnlySpan<byte>.Empty, FastParams);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        UnlockResult after = _cryptor.Unlock(rotated.Encrypted, Password("pw"));
        Assert.Empty(after.Plaintext);
    }

    [Fact]
    public void Rotate_RotatedVault_DoesNotContainPlaintext()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("super-secret-value"), FastParams);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, Password("pw"));

        string json = EncryptedVaultSerializer.Serialize(rotated.Encrypted);
        Assert.DoesNotContain("super-secret-value", json, StringComparison.Ordinal);
    }
}
