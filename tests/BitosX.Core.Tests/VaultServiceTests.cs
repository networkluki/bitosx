using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

public sealed class VaultServiceTests
{
    private const string MasterPassword = "correct horse battery";

    private static (VaultService Service, InMemoryVaultStorage Storage) CreateService(InMemoryVaultStorage? storage = null)
    {
        storage ??= new InMemoryVaultStorage();
        var service = new VaultService(
            VaultCryptor.CreateDefault(),
            storage,
            Argon2Parameters.ForTesting,
            NullLogger<VaultService>.Instance);
        return (service, storage);
    }

    private static LoginItem SampleLogin() =>
        new() { Name = "Example", Username = "user", Password = "P@ssw0rd-Strong-123" };

    [Fact]
    public async Task RefreshState_NoFile_ReturnsNoVault()
    {
        (VaultService service, _) = CreateService();
        Assert.Equal(VaultState.NoVault, await service.RefreshStateAsync());
    }

    [Fact]
    public async Task CreateVault_LeavesServiceUnlocked_AndPersists()
    {
        (VaultService service, InMemoryVaultStorage storage) = CreateService();

        await service.CreateVaultAsync(MasterPassword);

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.NotNull(storage.Stored);
    }

    [Fact]
    public async Task CreateVault_ShortPassword_Throws()
    {
        (VaultService service, _) = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateVaultAsync("short"));
    }

    [Fact]
    public async Task FullLifecycle_CreateLockUnlock_RestoresItems()
    {
        var storage = new InMemoryVaultStorage();

        // Session 1: create and add an item.
        (VaultService s1, _) = CreateService(storage);
        await s1.CreateVaultAsync(MasterPassword);
        LoginItem added = await s1.AddLoginAsync(SampleLogin());
        s1.Lock();
        Assert.Equal(VaultState.Locked, s1.State);
        Assert.Empty(s1.Items);

        // Session 2: a fresh service over the same storage unlocks and sees the item.
        (VaultService s2, _) = CreateService(storage);
        Assert.Equal(VaultState.Locked, await s2.RefreshStateAsync());
        await s2.UnlockAsync(MasterPassword);

        Assert.Equal(VaultState.Unlocked, s2.State);
        LoginItem restored = Assert.IsType<LoginItem>(Assert.Single(s2.Items));
        Assert.Equal(added.Id, restored.Id);
        Assert.Equal("P@ssw0rd-Strong-123", restored.Password);
    }

    [Fact]
    public async Task Unlock_WrongPassword_Throws_AndStaysLocked()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s1, _) = CreateService(storage);
        await s1.CreateVaultAsync(MasterPassword);
        s1.Lock();

        (VaultService s2, _) = CreateService(storage);
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => s2.UnlockAsync("wrong password!!"));
        Assert.NotEqual(VaultState.Unlocked, s2.State);
    }

    [Fact]
    public async Task AddUpdateDelete_PersistAcrossReopen()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s1, _) = CreateService(storage);
        await s1.CreateVaultAsync(MasterPassword);

        LoginItem item = await s1.AddLoginAsync(SampleLogin());
        await s1.UpdateItemAsync(item with { Name = "Renamed" });

        (VaultService s2, _) = CreateService(storage);
        await s2.UnlockAsync(MasterPassword);
        Assert.Equal("Renamed", ((LoginItem)Assert.Single(s2.Items)).Name);

        await s2.DeleteItemAsync(item.Id);

        (VaultService s3, _) = CreateService(storage);
        await s3.UnlockAsync(MasterPassword);
        Assert.Empty(s3.Items);
    }

    [Fact]
    public async Task ToggleFavorite_Persists()
    {
        (VaultService service, _) = CreateService();
        await service.CreateVaultAsync(MasterPassword);
        LoginItem item = await service.AddLoginAsync(SampleLogin());

        VaultItem toggled = await service.ToggleFavoriteAsync(item.Id);
        Assert.True(toggled.Favorite);
    }

    [Fact]
    public async Task FailedSave_RollsBackInMemory_AndKeepsStoredVaultIntact()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService service, _) = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        EncryptedVault? beforeAdd = storage.Stored;

        storage.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => service.AddLoginAsync(SampleLogin()));

        // In-memory state rolled back and the persisted vault is unchanged.
        Assert.Empty(service.Items);
        Assert.Same(beforeAdd, storage.Stored);

        // Verify the stored (unchanged) vault still unlocks and is empty.
        (VaultService verifier, _) = CreateService(storage);
        await verifier.UnlockAsync(MasterPassword);
        Assert.Empty(verifier.Items);
    }

    [Fact]
    public async Task Operations_WhileLocked_Throw()
    {
        (VaultService service, _) = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLoginAsync(SampleLogin()));
    }

    [Fact]
    public async Task ChangeMasterPassword_ReopensWithNewPassword_OldFails()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s1, _) = CreateService(storage);
        await s1.CreateVaultAsync(MasterPassword);
        await s1.AddLoginAsync(SampleLogin());

        await s1.ChangeMasterPasswordAsync(MasterPassword, "new-master-pass");

        // Same session still works (vault key unchanged) and can persist.
        await s1.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });

        (VaultService s2, _) = CreateService(storage);
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => s2.UnlockAsync(MasterPassword));
        await s2.UnlockAsync("new-master-pass");
        Assert.Equal(2, s2.Items.Count);
    }

    [Fact]
    public async Task ChangeMasterPassword_WrongCurrent_Throws_AndStoredVaultUnchanged()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s, _) = CreateService(storage);
        await s.CreateVaultAsync(MasterPassword);
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => s.ChangeMasterPasswordAsync("wrong-current-pw", "new-master-pass"));

        Assert.Same(before, storage.Stored); // nothing written

        (VaultService s2, _) = CreateService(storage);
        await s2.UnlockAsync(MasterPassword); // old password still works
    }

    [Fact]
    public async Task ChangeMasterPassword_WhileLocked_Throws()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s, _) = CreateService(storage);
        await s.CreateVaultAsync(MasterPassword);
        s.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.ChangeMasterPasswordAsync(MasterPassword, "new-master-pass"));
    }

    [Fact]
    public async Task ChangeMasterPassword_SameAsCurrent_Throws()
    {
        (VaultService s, _) = CreateService();
        await s.CreateVaultAsync(MasterPassword);

        await Assert.ThrowsAsync<ArgumentException>(
            () => s.ChangeMasterPasswordAsync(MasterPassword, MasterPassword));
    }

    [Fact]
    public async Task ChangeMasterPassword_WriteFails_RollsBack_OldPasswordStillWorks()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService s, _) = CreateService(storage);
        await s.CreateVaultAsync(MasterPassword);
        EncryptedVault before = storage.Stored!;

        storage.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(
            () => s.ChangeMasterPasswordAsync(MasterPassword, "new-master-pass"));

        Assert.Same(before, storage.Stored); // nothing persisted

        (VaultService s2, _) = CreateService(storage);
        await s2.UnlockAsync(MasterPassword); // old password still works
        (VaultService s3, _) = CreateService(storage);
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => s3.UnlockAsync("new-master-pass"));
    }

    [Fact]
    public async Task Lock_ClearsItemsAndState()
    {
        (VaultService service, _) = CreateService();
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(SampleLogin());
        Assert.NotEmpty(service.Items);

        service.Lock();

        Assert.Equal(VaultState.Locked, service.State);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Lock_ThenRefreshState_StaysLocked_AndOperationsThrow()
    {
        (VaultService service, _) = CreateService();
        await service.CreateVaultAsync(MasterPassword);
        service.Lock();

        // Without re-authenticating there is no path back to Unlocked.
        Assert.Equal(VaultState.Locked, await service.RefreshStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLoginAsync(SampleLogin()));
    }

    [Fact]
    public async Task Lock_IsIdempotent()
    {
        (VaultService service, _) = CreateService();
        await service.CreateVaultAsync(MasterPassword);

        service.Lock();
        service.Lock(); // must not throw

        Assert.Equal(VaultState.Locked, service.State);
    }

    [Fact]
    public async Task Unlock_WithResourceExhaustingStoredParameters_FailsClosed_WithoutDeriving()
    {
        var storage = new InMemoryVaultStorage();
        (VaultService service, _) = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        // Simulate a tampered/malicious vault file requesting ~2 TiB of Argon2 memory. The unlock
        // must be rejected up front (format error) rather than attempting the allocation.
        EncryptedVault tampered = storage.Stored! with
        {
            KdfParameters = new Argon2Parameters(2_000_000_000, 3, 4, 32),
        };
        await storage.WriteAsync(tampered);

        (VaultService reopen, _) = CreateService(storage);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => reopen.UnlockAsync(MasterPassword));
        Assert.NotEqual(VaultState.Unlocked, reopen.State);
    }
}
