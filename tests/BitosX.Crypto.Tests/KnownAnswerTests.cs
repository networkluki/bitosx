using System.Text;
using BitosX.Crypto;
using Konscious.Security.Cryptography;

namespace BitosX.Crypto.Tests;

/// <summary>
/// Known-answer tests: the primitives must produce the published reference outputs, not merely
/// agree with themselves. A round-trip test cannot tell a correct implementation from one that is
/// consistently wrong, and an Argon2 implementation that deviated from the standard could be
/// cheaper to attack than the parameters suggest.
/// </summary>
public sealed class KnownAnswerTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] Repeat(byte value, int count) => [.. Enumerable.Repeat(value, count)];

    // ---- Argon2id ----

    /// <summary>
    /// Vectors from the test suite of the Argon2 reference implementation (phc-winner-argon2,
    /// src/test.c, version 0x13), exercised through the same wrapper the vault uses.
    /// </summary>
    [Theory]
    [InlineData(2, 65536, 1, "password", "somesalt", "09316115d5cf24ed5a15a31a3ba326e5cf32edc24702987c02b6566f61913cf7")]
    [InlineData(2, 256, 1, "password", "somesalt", "9dfeb910e80bad0311fee20f9c0e2b12c17987b4cac90c2ef54d5b3021c68bfe")]
    [InlineData(2, 256, 2, "password", "somesalt", "6d093c501fd5999645e0ea3bf620d7b8be7fd2db59c20d9fff9539da2bf57037")]
    [InlineData(1, 65536, 1, "password", "somesalt", "f6a5adc1ba723dddef9b5ac1d464e180fcd9dffc9d1cbf76cca2fed795d9ca98")]
    [InlineData(4, 65536, 1, "password", "somesalt", "9025d48e68ef7395cca9079da4c4ec3affb3c8911fe4f86d1a2520856f63172c")]
    [InlineData(2, 65536, 1, "differentpassword", "somesalt", "0b84d652cf6b0c4beaef0dfe278ba6a80df6696281d7e0d2891b817d8c458fde")]
    [InlineData(2, 65536, 1, "password", "diffsalt", "bdf32b05ccc42eb15d58fd19b1f856b113da1e9a5874fdcc544308565aa8141c")]
    public void Argon2id_MatchesTheReferenceImplementation(
        int iterations, int memoryKiB, int parallelism, string password, string salt, string expectedHex)
    {
        var kdf = new Argon2idKeyDerivation();

        byte[] key = kdf.DeriveKey(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            new Argon2Parameters(memoryKiB, iterations, parallelism, 32));

        Assert.Equal(expectedHex, Convert.ToHexStringLower(key));
    }

    /// <summary>
    /// The Argon2id test vector of RFC 9106, section 5.3. It uses a secret and associated data,
    /// which the vault does not, so it is run against the library directly.
    /// </summary>
    [Fact]
    public void Argon2id_Library_MatchesRfc9106()
    {
        using var argon2 = new Argon2id(Repeat(0x01, 32))
        {
            Salt = Repeat(0x02, 16),
            KnownSecret = Repeat(0x03, 8),
            AssociatedData = Repeat(0x04, 12),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };

        byte[] tag = argon2.GetBytes(32);

        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(tag));
    }

    // ---- AES-256-GCM ----

    /// <summary>
    /// AES-256 test cases 13, 14 and 16 from the GCM specification (McGrew and Viega, "The
    /// Galois/Counter Mode of Operation", appendix B). Also pins the output layout: ciphertext
    /// followed by the 16-byte tag.
    /// </summary>
    [Theory]
    [InlineData(
        "0000000000000000000000000000000000000000000000000000000000000000",
        "000000000000000000000000",
        "",
        "",
        "",
        "530f8afbc74536b9a963b4f1c4cb738b")]
    [InlineData(
        "0000000000000000000000000000000000000000000000000000000000000000",
        "000000000000000000000000",
        "00000000000000000000000000000000",
        "",
        "cea7403d4d606b6e074ec5d3baf39d18",
        "d0d1c8a799996bf0265b98b5d48ab919")]
    [InlineData(
        "feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308",
        "cafebabefacedbaddecaf888",
        "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39",
        "feedfacedeadbeeffeedfacedeadbeefabaddad2",
        "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662",
        "76fc6ece0f4e1768cddf8853bb2d551b")]
    public void AesGcm_MatchesThePublishedTestCases(
        string key, string nonce, string plaintext, string associatedData, string expectedCiphertext, string expectedTag)
    {
        var cipher = new AesGcmCipher();

        byte[] output = cipher.Encrypt(Hex(key), Hex(nonce), Hex(plaintext), Hex(associatedData));
        byte[] decrypted = cipher.Decrypt(Hex(key), Hex(nonce), output, Hex(associatedData));

        Assert.Equal(expectedCiphertext + expectedTag, Convert.ToHexStringLower(output));
        Assert.Equal(plaintext, Convert.ToHexStringLower(decrypted));
    }

    // ---- The vault's own associated data ----

    /// <summary>
    /// Pins the associated data of format version 1. It is derived, not stored, so any change to how
    /// it is built would silently make every existing vault fail to authenticate. The envelope below
    /// is assembled here from the primitives alone, exactly as the format is documented, and must be
    /// accepted by the real cryptor.
    /// </summary>
    [Fact]
    public void VaultFormatV1_AssociatedData_IsStable()
    {
        byte[] password = Encoding.UTF8.GetBytes("correct horse battery");
        byte[] plaintext = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"items\":[]}");
        Argon2Parameters parameters = Argon2Parameters.ForTesting;
        byte[] salt = Repeat(0x11, CryptoConstants.SaltSizeBytes);
        byte[] keyWrapNonce = Repeat(0x22, CryptoConstants.NonceSizeBytes);
        byte[] contentNonce = Repeat(0x33, CryptoConstants.NonceSizeBytes);
        byte[] vaultKey = Repeat(0x44, CryptoConstants.KeySizeBytes);

        byte[] keyWrapAad = Encoding.UTF8.GetBytes(
            "BitosX|kek|v1|argon2id|m=1024|t=1|p=1|klen=32|salt=" + Convert.ToBase64String(salt));
        byte[] contentAad = Encoding.UTF8.GetBytes("BitosX|content|v1|AES-256-GCM");

        var cipher = new AesGcmCipher();
        byte[] kek = new Argon2idKeyDerivation().DeriveKey(password, salt, parameters);
        var envelope = new EncryptedVault(
            1,
            "argon2id",
            parameters,
            salt,
            "AES-256-GCM",
            keyWrapNonce,
            cipher.Encrypt(kek, keyWrapNonce, vaultKey, keyWrapAad),
            contentNonce,
            cipher.Encrypt(vaultKey, contentNonce, plaintext, contentAad));

        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(envelope, password);

        Assert.Equal(vaultKey, unlocked.VaultKey);
        Assert.Equal(plaintext, unlocked.Plaintext);
    }
}
