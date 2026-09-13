using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

public sealed class VaultCryptorTests
{
    private static readonly Argon2Parameters FastParams = Argon2Parameters.ForTesting;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    private static byte[] Password(string p) => Encoding.UTF8.GetBytes(p);
    private static byte[] Plaintext(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void CreateThenUnlock_WithCorrectPassword_ReturnsOriginalPlaintext()
    {
        byte[] plaintext = Plaintext("{\"items\":[]}");
        VaultCreationResult created = _cryptor.Create(Password("correct horse"), plaintext, FastParams);

        UnlockResult unlocked = _cryptor.Unlock(created.Encrypted, Password("correct horse"));

        Assert.Equal(plaintext, unlocked.Plaintext);
        Assert.Equal(CryptoConstants.KeySizeBytes, unlocked.VaultKey.Length);
    }

    [Fact]
    public void CreatedVault_DoesNotContainPlaintext()
    {
        byte[] plaintext = Plaintext("super-secret-password-value");
        VaultCreationResult created = _cryptor.Create(Password("pw"), plaintext, FastParams);

        string json = EncryptedVaultSerializer.Serialize(created.Encrypted);

        Assert.DoesNotContain("super-secret-password-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unlock_WithWrongPassword_ThrowsAuthenticationException()
    {
        VaultCreationResult created = _cryptor.Create(Password("right"), Plaintext("data"), FastParams);

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(created.Encrypted, Password("wrong")));
    }

    [Fact]
    public void Unlock_WithModifiedCiphertext_Fails()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        created.Encrypted.Ciphertext[0] ^= 0xFF;

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(created.Encrypted, Password("pw")));
    }

    [Fact]
    public void Unlock_WithModifiedWrappedKey_Fails()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        created.Encrypted.EncryptedVaultKey[0] ^= 0xFF;

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(created.Encrypted, Password("pw")));
    }

    [Fact]
    public void Unlock_WithModifiedSalt_Fails()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        created.Encrypted.Salt[0] ^= 0xFF;

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(created.Encrypted, Password("pw")));
    }

    [Fact]
    public void Unlock_WithTamperedKdfParameters_Fails()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        // Same valid range, but different from what the KEK was derived with -> fails closed.
        var tampered = created.Encrypted with { KdfParameters = FastParams with { Iterations = FastParams.Iterations + 1 } };

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(tampered, Password("pw")));
    }

    [Fact]
    public void Unlock_WithModifiedContentNonce_Fails()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        created.Encrypted.ContentNonce[0] ^= 0xFF;

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(created.Encrypted, Password("pw")));
    }

    [Fact]
    public void Unlock_WithUnsupportedVersion_ThrowsFormatException()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        var tampered = created.Encrypted with { Version = 999 };

        Assert.Throws<UnsupportedVaultFormatException>(() => _cryptor.Unlock(tampered, Password("pw")));
    }

    [Fact]
    public void IndependentVaults_HaveDifferentKeys_AndCannotCrossDecrypt()
    {
        VaultCreationResult a = _cryptor.Create(Password("same-password"), Plaintext("A"), FastParams);
        VaultCreationResult b = _cryptor.Create(Password("same-password"), Plaintext("B"), FastParams);

        UnlockResult ua = _cryptor.Unlock(a.Encrypted, Password("same-password"));
        UnlockResult ub = _cryptor.Unlock(b.Encrypted, Password("same-password"));

        Assert.NotEqual(ua.VaultKey, ub.VaultKey);

        // Vault B's key must not decrypt vault A's content.
        var cipher = new AesGcmCipher();
        Assert.Throws<VaultAuthenticationException>(
            () => cipher.Decrypt(ub.VaultKey, a.Encrypted.ContentNonce, a.Encrypted.Ciphertext, Encoding.UTF8.GetBytes("BitosX|content|v1|AES-256-GCM")));
    }

    [Fact]
    public void ReEncryptContent_SavesNewContent_UnderSameKeyAndHeader()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("original"), FastParams);
        UnlockResult unlocked = _cryptor.Unlock(created.Encrypted, Password("pw"));

        EncryptedVault resaved = _cryptor.ReEncryptContent(unlocked.Header, unlocked.VaultKey, Plaintext("updated"));

        // Header (salt, wrapped key) preserved; content nonce refreshed.
        Assert.Equal(created.Encrypted.Salt, resaved.Salt);
        Assert.Equal(created.Encrypted.EncryptedVaultKey, resaved.EncryptedVaultKey);
        Assert.NotEqual(created.Encrypted.ContentNonce, resaved.ContentNonce);

        UnlockResult reunlocked = _cryptor.Unlock(resaved, Password("pw"));
        Assert.Equal("updated", Encoding.UTF8.GetString(reunlocked.Plaintext));
    }

    [Fact]
    public void Create_ProducesUniqueSaltAndNoncePerVault()
    {
        VaultCreationResult a = _cryptor.Create(Password("pw"), Plaintext("x"), FastParams);
        VaultCreationResult b = _cryptor.Create(Password("pw"), Plaintext("x"), FastParams);

        Assert.NotEqual(a.Encrypted.Salt, b.Encrypted.Salt);
        Assert.NotEqual(a.Encrypted.ContentNonce, b.Encrypted.ContentNonce);
        Assert.NotEqual(a.Encrypted.KeyWrapNonce, b.Encrypted.KeyWrapNonce);
    }

    [Fact]
    public void Unlock_EmptyPlaintextVault_Succeeds()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), ReadOnlySpan<byte>.Empty, FastParams);
        UnlockResult unlocked = _cryptor.Unlock(created.Encrypted, Password("pw"));
        Assert.Empty(unlocked.Plaintext);
    }
}
