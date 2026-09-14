using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

/// <summary>
/// Tests for <see cref="EncryptedVaultFingerprint"/>: a stable content fingerprint over the whole
/// encrypted envelope. Equal envelopes hash equal; any change — including a same-key content change —
/// changes the hash. This underpins the on-disk/in-memory consistency gate before rotation.
/// </summary>
public sealed class EncryptedVaultFingerprintTests
{
    private static readonly Argon2Parameters FastParams = Argon2Parameters.ForTesting;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    private static byte[] Password(string p) => Encoding.UTF8.GetBytes(p);
    private static byte[] Plaintext(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Compute_IsDeterministic_ForTheSameEnvelope()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        byte[] a = EncryptedVaultFingerprint.Compute(created.Encrypted);
        byte[] b = EncryptedVaultFingerprint.Compute(created.Encrypted);

        Assert.Equal(32, a.Length); // SHA-256
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DifferentVaults_ProduceDifferentFingerprints()
    {
        VaultCreationResult a = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        VaultCreationResult b = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);

        Assert.NotEqual(EncryptedVaultFingerprint.Compute(a.Encrypted), EncryptedVaultFingerprint.Compute(b.Encrypted));
    }

    [Fact]
    public void Compute_SameKey_DifferentContent_ProducesDifferentFingerprint()
    {
        // The critical case: identical vault key/header, but the content was re-encrypted (new nonce +
        // ciphertext). A key-only comparison would miss this; the fingerprint must not.
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("original"), FastParams);
        UnlockResult unlocked = _cryptor.Unlock(created.Encrypted, Password("pw"));

        EncryptedVault modified = _cryptor.ReEncryptContent(unlocked.Header, unlocked.VaultKey, Plaintext("changed"));

        // Same key material / header...
        Assert.Equal(created.Encrypted.EncryptedVaultKey, modified.EncryptedVaultKey);
        Assert.Equal(created.Encrypted.Salt, modified.Salt);
        // ...but a different fingerprint, because the content changed.
        Assert.NotEqual(
            EncryptedVaultFingerprint.Compute(created.Encrypted),
            EncryptedVaultFingerprint.Compute(modified));
    }

    [Fact]
    public void Compute_ChangingAnyField_ChangesFingerprint()
    {
        VaultCreationResult created = _cryptor.Create(Password("pw"), Plaintext("data"), FastParams);
        byte[] baseline = EncryptedVaultFingerprint.Compute(created.Encrypted);

        Assert.NotEqual(baseline, EncryptedVaultFingerprint.Compute(created.Encrypted with { Version = 2 }));
        Assert.NotEqual(baseline, EncryptedVaultFingerprint.Compute(
            created.Encrypted with { KdfParameters = FastParams with { Iterations = FastParams.Iterations + 1 } }));
    }
}
