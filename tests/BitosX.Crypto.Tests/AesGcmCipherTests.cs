using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

public sealed class AesGcmCipherTests
{
    private readonly AesGcmCipher _cipher = new();

    private static byte[] Key() => SecureRandom.GetBytes(CryptoConstants.KeySizeBytes);
    private static byte[] Nonce() => SecureRandom.GetBytes(CryptoConstants.NonceSizeBytes);

    [Fact]
    public void EncryptThenDecrypt_RoundTripsPlaintext()
    {
        byte[] key = Key();
        byte[] nonce = Nonce();
        byte[] plaintext = Encoding.UTF8.GetBytes("top secret content");
        byte[] aad = Encoding.UTF8.GetBytes("context");

        byte[] ciphertext = _cipher.Encrypt(key, nonce, plaintext, aad);
        byte[] decrypted = _cipher.Decrypt(key, nonce, ciphertext, aad);

        Assert.Equal(plaintext, decrypted);
        Assert.NotEqual(plaintext, ciphertext[..plaintext.Length]); // actually encrypted
    }

    [Fact]
    public void Decrypt_WithWrongKey_Fails()
    {
        byte[] nonce = Nonce();
        byte[] plaintext = Encoding.UTF8.GetBytes("data");
        byte[] ciphertext = _cipher.Encrypt(Key(), nonce, plaintext, []);

        Assert.Throws<VaultAuthenticationException>(() => _cipher.Decrypt(Key(), nonce, ciphertext, []));
    }

    [Fact]
    public void Decrypt_WithModifiedCiphertext_Fails()
    {
        byte[] key = Key();
        byte[] nonce = Nonce();
        byte[] ciphertext = _cipher.Encrypt(key, nonce, Encoding.UTF8.GetBytes("data"), []);
        ciphertext[0] ^= 0xFF;

        Assert.Throws<VaultAuthenticationException>(() => _cipher.Decrypt(key, nonce, ciphertext, []));
    }

    [Fact]
    public void Decrypt_WithModifiedTag_Fails()
    {
        byte[] key = Key();
        byte[] nonce = Nonce();
        byte[] ciphertext = _cipher.Encrypt(key, nonce, Encoding.UTF8.GetBytes("data"), []);
        ciphertext[^1] ^= 0xFF; // last byte is part of the tag

        Assert.Throws<VaultAuthenticationException>(() => _cipher.Decrypt(key, nonce, ciphertext, []));
    }

    [Fact]
    public void Decrypt_WithModifiedAssociatedData_Fails()
    {
        byte[] key = Key();
        byte[] nonce = Nonce();
        byte[] ciphertext = _cipher.Encrypt(key, nonce, Encoding.UTF8.GetBytes("data"), Encoding.UTF8.GetBytes("aad-1"));

        Assert.Throws<VaultAuthenticationException>(
            () => _cipher.Decrypt(key, nonce, ciphertext, Encoding.UTF8.GetBytes("aad-2")));
    }

    [Fact]
    public void Decrypt_WithTooShortInput_FailsClosed()
    {
        Assert.Throws<VaultAuthenticationException>(() => _cipher.Decrypt(Key(), Nonce(), new byte[4], []));
    }

    [Fact]
    public void Encrypt_WithWrongKeySize_Throws()
    {
        Assert.Throws<ArgumentException>(() => _cipher.Encrypt(new byte[16], Nonce(), [1, 2, 3], []));
    }
}
