using System.Text;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

/// <summary>
/// An empty password. The key-derivation library refuses one with an exception of its own, which
/// before 0.7.0 travelled all the way up to the user interface: pressing "Rotate vault key" or
/// "Validate backup" with nothing typed ended the application. No vault can be keyed with an empty
/// password, so as an attempt at opening one it is a wrong password like any other; as a password
/// to protect a vault with, it is refused.
/// </summary>
public sealed class EmptyPasswordTests
{
    private static readonly Argon2Parameters Settings = Argon2Parameters.ForTesting;
    private static readonly Argon2Parameters Stronger = new(4096, 2, 1, 32);

    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();
    private readonly byte[] _password = Encoding.UTF8.GetBytes("correct horse battery");
    private readonly byte[] _plaintext = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"items\":[]}");

    private EncryptedVault NewVault() => _cryptor.Create(_password, _plaintext, Settings).Encrypted;

    // ---- As an attempt at opening a vault: a wrong password ----

    [Fact]
    public void Unlock_WithAnEmptyPassword_IsAWrongPassword()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(vault, []));
    }

    [Fact]
    public void ChangeMasterPassword_WithAnEmptyCurrentPassword_IsAWrongPassword()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<VaultAuthenticationException>(
            () => _cryptor.ChangeMasterPassword(vault, [], Encoding.UTF8.GetBytes("a new password")));
    }

    [Fact]
    public void ChangeKeyDerivation_WithAnEmptyCurrentPassword_IsAWrongPassword()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.ChangeKeyDerivation(vault, [], _password, Stronger));
    }

    [Fact]
    public void RotateVaultKey_WithAnEmptyCurrentPassword_IsAWrongPassword()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<VaultAuthenticationException>(() => _cryptor.RotateVaultKey(vault, []));
        Assert.Throws<VaultAuthenticationException>(() => _cryptor.RotateVaultKey(vault, [], _password));
    }

    [Fact]
    public void AnEmptyPassword_IsAnsweredLikeEveryOtherWrongPassword()
    {
        EncryptedVault vault = NewVault();

        VaultAuthenticationException empty = Assert.Throws<VaultAuthenticationException>(() => _cryptor.Unlock(vault, []));
        VaultAuthenticationException wrong = Assert.Throws<VaultAuthenticationException>(
            () => _cryptor.Unlock(vault, Encoding.UTF8.GetBytes("not the password")));

        Assert.Equal(wrong.Message, empty.Message);
    }

    // ---- As a password to protect a vault with: refused ----

    [Fact]
    public void Create_WithAnEmptyPassword_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => _cryptor.Create([], _plaintext, Settings));
    }

    [Fact]
    public void ChangeMasterPassword_ToAnEmptyPassword_IsRefused()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<ArgumentException>(() => _cryptor.ChangeMasterPassword(vault, _password, []));

        // And the vault is what it was.
        Assert.Equal(_plaintext, _cryptor.Unlock(vault, _password).Plaintext);
    }

    [Fact]
    public void ChangeKeyDerivation_WrappingUnderAnEmptyPassword_IsRefused()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<ArgumentException>(() => _cryptor.ChangeKeyDerivation(vault, _password, [], Stronger));
    }

    [Fact]
    public void RotateVaultKey_WrappingUnderAnEmptyPassword_IsRefused()
    {
        EncryptedVault vault = NewVault();

        Assert.Throws<ArgumentException>(() => _cryptor.RotateVaultKey(vault, _password, []));
    }

    // ---- The key derivation itself ----

    [Fact]
    public void DeriveKey_FromAnEmptyPassword_IsRefused_InWordsOfItsOwn()
    {
        var derivation = new Argon2idKeyDerivation();
        byte[] salt = new byte[16];

        ArgumentException refused = Assert.Throws<ArgumentException>(() => derivation.DeriveKey([], salt, Settings));

        Assert.Contains("empty password", refused.Message, StringComparison.Ordinal);
    }
}
