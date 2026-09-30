using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// An empty password handed to the vault service, which is what an empty password field in the
/// user interface does. It is a wrong password — never an exception from the key-derivation
/// library, which the user interface did not expect and which ended the application.
/// </summary>
public sealed class VaultServiceEmptyPasswordTests
{
    private const string MasterPassword = "correct horse battery";

    private static readonly Argon2Parameters Settings = Argon2Parameters.ForTesting;

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedWithOneItemAsync()
    {
        var storage = new InstrumentedVaultStorage();
        var service = new VaultService(VaultCryptor.CreateDefault(), storage, Settings, NullLogger<VaultService>.Instance);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(new LoginItem { Name = "First", Username = "user", Password = "P@ssw0rd-Strong-123" });
        return (service, storage);
    }

    [Fact]
    public async Task Unlock_WithAnEmptyPassword_IsAWrongPassword()
    {
        (VaultService service, _) = await UnlockedWithOneItemAsync();
        service.Lock();

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.UnlockAsync(string.Empty));

        Assert.Equal(VaultState.Locked, service.State);
    }

    [Fact]
    public async Task Rotate_WithAnEmptyPassword_IsAWrongPassword_AndWritesNothing()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        byte[] before = EncryptedVaultFingerprint.Compute(await storage.ReadAsync());
        int writes = storage.WriteCount;

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.RotateVaultKeyAsync(string.Empty));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(before, EncryptedVaultFingerprint.Compute(await storage.ReadAsync()));
        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task ChangeMasterPassword_WithAnEmptyCurrentPassword_IsAWrongPassword_AndWritesNothing()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        int writes = storage.WriteCount;

        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => service.ChangeMasterPasswordAsync(string.Empty, "a new and different password"));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task Strengthen_WithAnEmptyPassword_IsAWrongPassword_AndWritesNothing()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        int writes = storage.WriteCount;

        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => service.StrengthenKeyDerivationAsync(string.Empty, new Argon2Parameters(4096, 2, 1, 32)));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(Settings, service.KdfParameters);
    }

    [Fact]
    public async Task AfterAnEmptyPassword_TheVaultWorksAsBefore()
    {
        (VaultService service, _) = await UnlockedWithOneItemAsync();
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.RotateVaultKeyAsync(string.Empty));

        await service.RotateVaultKeyAsync(MasterPassword);
        await service.AddLoginAsync(new LoginItem { Name = "Second", Username = "user", Password = "P@ssw0rd-Strong-456" });
        service.Lock();
        await service.UnlockAsync(MasterPassword);

        Assert.Equal(2, service.Items.Count);
    }
}
