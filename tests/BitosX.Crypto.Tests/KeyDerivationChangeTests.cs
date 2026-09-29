using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

/// <summary>
/// Tests for re-wrapping a vault key under stronger key-derivation settings and under another
/// encoding of the same password (0.6.0).
/// </summary>
public sealed class KeyDerivationChangeTests
{
    private static readonly Argon2Parameters Initial = Argon2Parameters.ForTesting;   // 1 MiB, 1 pass
    private static readonly Argon2Parameters Stronger = new(4096, 2, 1, 32);

    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();
    private readonly byte[] _password = Encoding.UTF8.GetBytes("correct horse battery");
    private readonly byte[] _plaintext = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"items\":[]}");

    private VaultCreationResult NewVault(Argon2Parameters? parameters = null)
        => _cryptor.Create(_password, _plaintext, parameters ?? Initial);

    [Fact]
    public void ChangeKeyDerivation_KeepsTheKeyAndTheContent_AndChangesTheWrapping()
    {
        VaultCreationResult created = NewVault();

        EncryptedVault changed = _cryptor.ChangeKeyDerivation(created.Encrypted, _password, _password, Stronger);

        Assert.Equal(Stronger, changed.KdfParameters);
        Assert.Equal(created.Encrypted.ContentNonce, changed.ContentNonce);
        Assert.Equal(created.Encrypted.Ciphertext, changed.Ciphertext);
        Assert.Equal(created.Encrypted.Version, changed.Version);
        Assert.NotEqual(created.Encrypted.Salt, changed.Salt);
        Assert.NotEqual(created.Encrypted.KeyWrapNonce, changed.KeyWrapNonce);
        Assert.NotEqual(created.Encrypted.EncryptedVaultKey, changed.EncryptedVaultKey);

        UnlockResult unlocked = _cryptor.Unlock(changed, _password);
        Assert.Equal(created.VaultKey, unlocked.VaultKey);
        Assert.Equal(_plaintext, unlocked.Plaintext);
    }

    [Fact]
    public void ChangeKeyDerivation_TheResultSurvivesSerialization()
    {
        EncryptedVault changed = _cryptor.ChangeKeyDerivation(NewVault().Encrypted, _password, _password, Stronger);

        EncryptedVault parsed = EncryptedVaultSerializer.Deserialize(EncryptedVaultSerializer.SerializeToUtf8Bytes(changed));

        Assert.Equal(Stronger, parsed.KdfParameters);
        Assert.Equal(_plaintext, _cryptor.Unlock(parsed, _password).Plaintext);
    }

    [Fact]
    public void ChangeKeyDerivation_IsNotRetroactive()
    {
        VaultCreationResult created = NewVault();

        _ = _cryptor.ChangeKeyDerivation(created.Encrypted, _password, _password, Stronger);

        // A copy made before the change is still protected by the old settings only.
        Assert.Equal(Initial, created.Encrypted.KdfParameters);
        Assert.Equal(_plaintext, _cryptor.Unlock(created.Encrypted, _password).Plaintext);
    }

    [Fact]
    public void ChangeKeyDerivation_TheNewSettingsAreBoundToTheWrappedKey()
    {
        EncryptedVault changed = _cryptor.ChangeKeyDerivation(NewVault().Encrypted, _password, _password, Stronger);

        // Writing weaker settings into the file does not make the vault cheaper to attack: it makes
        // it fail to authenticate.
        EncryptedVault tampered = changed with { KdfParameters = Initial };

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(tampered, _password));
    }

    [Fact]
    public void ChangeKeyDerivation_WrongPassword_FailsClosed()
    {
        VaultCreationResult created = NewVault();
        byte[] wrong = Encoding.UTF8.GetBytes("not the password");

        Assert.Throws<VaultAuthenticationException>(
            () => _cryptor.ChangeKeyDerivation(created.Encrypted, wrong, wrong, Stronger));
    }

    [Theory]
    [InlineData(4096, 2)]   // the same
    [InlineData(2048, 2)]   // less memory
    [InlineData(4096, 1)]   // fewer passes
    [InlineData(2048, 8)]   // less memory, more passes
    [InlineData(8192, 1)]   // more memory, fewer passes
    public void ChangeKeyDerivation_ToSettingsThatAreNotStronger_IsRefused(int memoryKiB, int iterations)
    {
        VaultCreationResult created = NewVault(Stronger);

        Assert.Throws<WeakerKeyDerivationException>(() => _cryptor.ChangeKeyDerivation(
            created.Encrypted, _password, _password, new Argon2Parameters(memoryKiB, iterations, 1, 32)));
    }

    [Fact]
    public void ChangeKeyDerivation_RefusesBeforeDerivingAnything()
    {
        // The refusal must not depend on the password being right.
        VaultCreationResult created = NewVault(Stronger);
        byte[] wrong = Encoding.UTF8.GetBytes("not the password");

        Assert.Throws<WeakerKeyDerivationException>(
            () => _cryptor.ChangeKeyDerivation(created.Encrypted, wrong, wrong, Initial));
    }

    [Theory]
    [InlineData(2_000_000_000, 3, 4)]
    [InlineData(65536, 1000, 4)]
    [InlineData(65536, 3, 1000)]
    public void ChangeKeyDerivation_ToSettingsOutsideTheBounds_IsRefused(int memoryKiB, int iterations, int lanes)
    {
        VaultCreationResult created = NewVault();

        Assert.Throws<UnsupportedVaultFormatException>(() => _cryptor.ChangeKeyDerivation(
            created.Encrypted, _password, _password, new Argon2Parameters(memoryKiB, iterations, lanes, 32)));
    }

    [Fact]
    public void ChangeKeyDerivation_MayChangeParallelismFreely()
    {
        VaultCreationResult created = NewVault(new Argon2Parameters(2048, 1, 4, 32));

        // Fewer lanes, more memory: stronger. Lanes decide how the work is spread, not how much there is.
        EncryptedVault changed = _cryptor.ChangeKeyDerivation(
            created.Encrypted, _password, _password, new Argon2Parameters(4096, 1, 1, 32));

        Assert.Equal(_plaintext, _cryptor.Unlock(changed, _password).Plaintext);
    }

    // ---- Wrapping under another encoding of the same password ----

    [Fact]
    public void ChangeKeyDerivation_WrapsUnderTheGivenEncoding()
    {
        byte[] asTyped = Encoding.UTF8.GetBytes("lösenord");     // decomposed
        byte[] canonical = Encoding.UTF8.GetBytes("lösenord");    // composed
        EncryptedVault legacy = _cryptor.Create(asTyped, _plaintext, Initial).Encrypted;

        EncryptedVault changed = _cryptor.ChangeKeyDerivation(legacy, asTyped, canonical, Stronger);

        Assert.Equal(_plaintext, _cryptor.Unlock(changed, canonical).Plaintext);
        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(changed, asTyped));
    }

    [Fact]
    public void RotateVaultKey_WrapsUnderTheGivenEncoding()
    {
        byte[] asTyped = Encoding.UTF8.GetBytes("lösenord");
        byte[] canonical = Encoding.UTF8.GetBytes("lösenord");
        VaultCreationResult legacy = _cryptor.Create(asTyped, _plaintext, Initial);

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(legacy.Encrypted, asTyped, canonical);

        UnlockResult unlocked = _cryptor.Unlock(rotated.Encrypted, canonical);
        Assert.Equal(_plaintext, unlocked.Plaintext);
        Assert.Equal(rotated.NewVaultKey, unlocked.VaultKey);
        Assert.NotEqual(legacy.VaultKey, unlocked.VaultKey);
        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(rotated.Encrypted, asTyped));
    }

    [Fact]
    public void RotateVaultKey_WithTheCurrentEncodingOnly_BehavesAsBefore()
    {
        VaultCreationResult created = NewVault();

        VaultKeyRotationResult rotated = _cryptor.RotateVaultKey(created.Encrypted, _password);

        Assert.Equal(_plaintext, _cryptor.Unlock(rotated.Encrypted, _password).Plaintext);
        Assert.Equal(Initial, rotated.Encrypted.KdfParameters);
    }

    [Fact]
    public void ChangeMasterPassword_StillPreservesTheSettings()
    {
        VaultCreationResult created = NewVault(Stronger);
        byte[] newPassword = Encoding.UTF8.GetBytes("a different master password");

        EncryptedVault changed = _cryptor.ChangeMasterPassword(created.Encrypted, _password, newPassword);

        Assert.Equal(Stronger, changed.KdfParameters);
        Assert.Equal(_plaintext, _cryptor.Unlock(changed, newPassword).Plaintext);
    }

    // ---- Comparing settings ----

    [Theory]
    [InlineData(65536, 3, 65536, 3, true, false)]    // equal
    [InlineData(131072, 3, 65536, 3, true, true)]    // more memory
    [InlineData(65536, 4, 65536, 3, true, true)]     // more passes
    [InlineData(131072, 4, 65536, 3, true, true)]    // both
    [InlineData(32768, 3, 65536, 3, false, false)]   // less memory
    [InlineData(65536, 2, 65536, 3, false, false)]   // fewer passes
    [InlineData(131072, 2, 65536, 3, false, false)]  // more memory cannot buy back fewer passes
    [InlineData(32768, 8, 65536, 3, false, false)]   // nor more passes less memory
    public void Settings_AreComparedInBothDimensions(
        int memory, int iterations, int otherMemory, int otherIterations, bool atLeast, bool stronger)
    {
        var candidate = new Argon2Parameters(memory, iterations, 4, 32);
        var other = new Argon2Parameters(otherMemory, otherIterations, 4, 32);

        Assert.Equal(atLeast, candidate.IsAtLeastAsStrongAs(other));
        Assert.Equal(stronger, candidate.IsStrongerThan(other));
    }

    [Fact]
    public void Settings_Parallelism_DoesNotCountAsStrength()
    {
        var fourLanes = new Argon2Parameters(65536, 3, 4, 32);
        var oneLane = new Argon2Parameters(65536, 3, 1, 32);

        Assert.True(fourLanes.IsAtLeastAsStrongAs(oneLane));
        Assert.True(oneLane.IsAtLeastAsStrongAs(fourLanes));
        Assert.False(fourLanes.IsStrongerThan(oneLane));
    }

    [Fact]
    public void NamedSettings_AreValid_AndEachStrongerThanTheLast()
    {
        Argon2Parameters.Default.Validate();
        Argon2Parameters.Strong.Validate();
        Argon2Parameters.Maximum.Validate();

        Assert.True(Argon2Parameters.Strong.IsStrongerThan(Argon2Parameters.Default));
        Assert.True(Argon2Parameters.Maximum.IsStrongerThan(Argon2Parameters.Strong));
        Assert.Equal(1024 * 1024, Argon2Parameters.Maximum.MemoryKiB);

        // The ceiling itself is accepted; one more kibibyte is not.
        Assert.Throws<UnsupportedVaultFormatException>(
            () => (Argon2Parameters.Maximum with { MemoryKiB = Argon2Parameters.Maximum.MemoryKiB + 1 }).Validate());
    }
}
