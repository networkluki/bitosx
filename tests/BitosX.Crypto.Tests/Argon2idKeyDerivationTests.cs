using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

public sealed class Argon2idKeyDerivationTests
{
    private readonly Argon2idKeyDerivation _kdf = new();
    private static readonly Argon2Parameters Fast = Argon2Parameters.ForTesting;

    [Fact]
    public void DeriveKey_IsDeterministic_ForSamePasswordAndSalt()
    {
        byte[] salt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
        byte[] pw = Encoding.UTF8.GetBytes("master");

        byte[] a = _kdf.DeriveKey(pw, salt, Fast);
        byte[] b = _kdf.DeriveKey(pw, salt, Fast);

        Assert.Equal(a, b);
        Assert.Equal(Fast.KeyLengthBytes, a.Length);
    }

    [Fact]
    public void DeriveKey_DiffersForDifferentSalt()
    {
        byte[] pw = Encoding.UTF8.GetBytes("master");
        byte[] a = _kdf.DeriveKey(pw, SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes), Fast);
        byte[] b = _kdf.DeriveKey(pw, SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes), Fast);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveKey_DiffersForDifferentPassword()
    {
        byte[] salt = SecureRandom.GetBytes(CryptoConstants.SaltSizeBytes);
        byte[] a = _kdf.DeriveKey(Encoding.UTF8.GetBytes("password-a"), salt, Fast);
        byte[] b = _kdf.DeriveKey(Encoding.UTF8.GetBytes("password-b"), salt, Fast);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveKey_WithTooShortSalt_Throws()
    {
        Assert.Throws<UnsupportedVaultFormatException>(
            () => _kdf.DeriveKey(Encoding.UTF8.GetBytes("pw"), new byte[4], Fast));
    }

    [Fact]
    public void Parameters_Validate_RejectsWeakValues()
    {
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(2, 3, 4, 32).Validate());
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(65536, 0, 4, 32).Validate());
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(65536, 3, 0, 32).Validate());
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(65536, 3, 4, 16).Validate());
    }

    [Fact]
    public void Parameters_Validate_RejectsResourceExhaustingValues()
    {
        // Denial-of-service guard: a malicious/tampered vault file must not be able to request
        // absurd memory, iteration, or parallelism costs. Argon2 runs before authentication, so
        // these bounds are enforced up front (see docs/CRYPTOGRAPHY.md — "KDF parameter bounds").
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(2_000_000_000, 3, 4, 32).Validate()); // ~2 TiB memory
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(int.MaxValue, 3, 4, 32).Validate());
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(65536, int.MaxValue, 4, 32).Validate()); // CPU exhaustion
        Assert.Throws<UnsupportedVaultFormatException>(() => new Argon2Parameters(65536, 3, 65, 32).Validate()); // parallelism > 64
    }

    [Fact]
    public void Parameters_Default_IsWithinBounds()
    {
        Argon2Parameters.Default.Validate(); // must not throw
        Argon2Parameters.ForTesting.Validate();
    }
}
