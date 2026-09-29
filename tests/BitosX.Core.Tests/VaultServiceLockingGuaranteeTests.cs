using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Security regression tests (0.5.1) for what "locked" guarantees: no vault key in memory, no matter
/// what was in flight when the lock happened, and no way for a failing subscriber to leave the
/// service unlocked around a destroyed key.
/// </summary>
public sealed class VaultServiceLockingGuaranteeTests
{
    private const string MasterPassword = "correct horse battery";
    private const string NewPassword = "a different master password";

    private static VaultService CreateService(InstrumentedVaultStorage storage) =>
        new(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    private static LoginItem Login(string name) =>
        new() { Name = name, Username = "user", Password = "P@ssw0rd-Strong-123" };

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedWithOneItemAsync()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));
        return (service, storage);
    }

    private static async Task<int> ItemCountAfterReopenAsync(InstrumentedVaultStorage storage, string password)
    {
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(password);
        return reopen.Items.Count;
    }

    // ---- 1. A failing StateChanged subscriber ----

    [Fact]
    public async Task Unlock_WhenASubscriberThrows_FailsClosed_AndNeverSavesUnderADestroyedKey()
    {
        (_, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        VaultService service = CreateService(storage);
        bool failOnce = true;
        service.StateChanged += (_, state) =>
        {
            if (state == VaultState.Unlocked && failOnce)
            {
                failOnce = false;
                throw new InvalidOperationException("The UI could not show the unlocked vault.");
            }
        };

        // Before the fix the key was zeroed while the session stayed Unlocked, and the next save
        // re-encrypted the whole vault under an all-zero key.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnlockAsync(MasterPassword));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.Empty(service.Items);
        Assert.False(service.HoldsKeyMaterial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLoginAsync(Login("Second")));

        // The stored vault is untouched and a clean unlock works normally afterwards.
        await service.UnlockAsync(MasterPassword);
        await service.AddLoginAsync(Login("Second"));
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Create_WhenASubscriberThrows_FailsClosed_AndTheNewVaultStillOpens()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        service.StateChanged += (_, state) =>
        {
            if (state == VaultState.Unlocked)
            {
                throw new InvalidOperationException("The UI could not show the unlocked vault.");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateVaultAsync(MasterPassword));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Equal(0, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task StateChanged_NotifiesEverySubscriber_EvenWhenAnEarlierOneThrows()
    {
        (VaultService service, _) = await UnlockedWithOneItemAsync();
        var seenByLater = new List<VaultState>();
        service.StateChanged += (_, _) => throw new InvalidOperationException("First subscriber failed.");
        service.StateChanged += (_, state) => seenByLater.Add(state);

        // E.g. auto-lock must still learn about the transition when the UI subscriber fails.
        Assert.Throws<InvalidOperationException>(service.Lock);

        Assert.Equal([VaultState.Locked], seenByLater);
        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task Unlock_WhenASubscriberThrows_TellsTheOthersTheVaultIsLockedAgain()
    {
        (_, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        VaultService service = CreateService(storage);
        await service.RefreshStateAsync();
        var seen = new List<VaultState>();
        service.StateChanged += (_, state) => seen.Add(state);
        service.StateChanged += (_, state) =>
        {
            if (state == VaultState.Unlocked)
            {
                throw new InvalidOperationException("Second subscriber failed.");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnlockAsync(MasterPassword));

        // A subscriber that armed itself on Unlocked is told to stand down.
        Assert.Equal([VaultState.Unlocked, VaultState.Locked], seen);
    }

    // ---- 2. Lock() while an operation is in flight ----

    [Fact]
    public async Task Lock_DuringRotation_LeavesNoKeyInMemory_AndTheRotatedVaultOpens()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        EncryptedVault before = storage.Stored!;

        // Auto-lock fires while the rotated vault is being written.
        storage.OnWrite = service.Lock;
        await service.RotateVaultKeyAsync(MasterPassword);
        storage.OnWrite = null;

        // Before the fix the new key was adopted into the locked service and stayed in memory.
        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Empty(service.Items);

        Assert.NotEqual(before.EncryptedVaultKey, storage.Stored!.EncryptedVaultKey);
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Lock_DuringMasterPasswordChange_LeavesTheServiceLocked_AndTheNewPasswordWorks()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        storage.OnWrite = service.Lock;
        await service.ChangeMasterPasswordAsync(MasterPassword, NewPassword);
        storage.OnWrite = null;

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, NewPassword));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lock_DuringSave_NeverWritesUnderAWipedKey(bool lockDuringWrite)
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        // Lock() wipes the session key while the save is between reading and writing the file.
        if (lockDuringWrite)
        {
            storage.OnWrite = service.Lock;
        }
        else
        {
            storage.OnRead = service.Lock;
        }

        await service.AddLoginAsync(Login("Second"));
        storage.OnRead = null;
        storage.OnWrite = null;

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Empty(service.Items);

        // Whatever reached the disk is a vault the master password opens, with both items.
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Lock_DuringUnlock_Wins_AndTheVaultStaysLocked()
    {
        (_, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        VaultService service = CreateService(storage);
        await service.RefreshStateAsync();

        storage.OnRead = service.Lock;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UnlockAsync(MasterPassword));
        storage.OnRead = null;

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Lock_WipesTheKey_AndUnlockRestoresIt()
    {
        (VaultService service, _) = await UnlockedWithOneItemAsync();
        Assert.True(service.HoldsKeyMaterial);

        service.Lock();
        Assert.False(service.HoldsKeyMaterial);

        await service.UnlockAsync(MasterPassword);
        Assert.True(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task Unlock_WrongPassword_LeavesNoKeyInMemory()
    {
        (_, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();
        VaultService service = CreateService(storage);

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.UnlockAsync("not the password"));

        Assert.False(service.HoldsKeyMaterial);
        Assert.NotEqual(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task Unlock_WhileAlreadyUnlocked_ReplacesTheSessionCleanly()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        await service.UnlockAsync(MasterPassword);
        await service.AddLoginAsync(Login("Second"));

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task KdfParameters_DescribeTheOpenVault_AndAreGoneWhenLocked()
    {
        // A vault created with one profile, opened by a build whose default is another.
        var storage = new InstrumentedVaultStorage();
        var created = new Argon2Parameters(2048, 2, 1, 32);
        var creator = new VaultService(VaultCryptor.CreateDefault(), storage, created, NullLogger<VaultService>.Instance);
        await creator.CreateVaultAsync(MasterPassword);

        VaultService service = CreateService(storage); // would create with Argon2Parameters.ForTesting
        Assert.Null(service.KdfParameters);

        await service.UnlockAsync(MasterPassword);
        Assert.Equal(created, service.KdfParameters);

        await service.ChangeMasterPasswordAsync(MasterPassword, NewPassword);
        await service.RotateVaultKeyAsync(NewPassword);
        Assert.Equal(created, service.KdfParameters); // both preserve the vault's own profile

        service.Lock();
        Assert.Null(service.KdfParameters);
    }

    // ---- 3. Overlapping operations ----

    [Fact]
    public async Task OverlappingSaves_RunOneAtATime_AndBothArePersisted()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writesStarted = 0;
        storage.BeforeWriteAsync = () =>
        {
            if (Interlocked.Increment(ref writesStarted) == 1)
            {
                firstWriteStarted.SetResult();
                return releaseFirstWrite.Task;
            }

            return Task.CompletedTask;
        };

        Task<LoginItem> first = service.AddLoginAsync(Login("Second"));
        await firstWriteStarted.Task;
        Task<LoginItem> second = service.AddLoginAsync(Login("Third")); // e.g. a double click

        // The second operation must not start writing while the first is still in flight.
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref writesStarted));
        Assert.False(second.IsCompleted);

        releaseFirstWrite.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(3, service.Items.Count);
        Assert.Equal(3, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task OverlappingRotationAndSave_BothSucceed_AndTheVaultOpens()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedWithOneItemAsync();

        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writesStarted = 0;
        storage.BeforeWriteAsync = () =>
        {
            if (Interlocked.Increment(ref writesStarted) == 1)
            {
                firstWriteStarted.SetResult();
                return releaseFirstWrite.Task;
            }

            return Task.CompletedTask;
        };

        Task rotation = service.RotateVaultKeyAsync(MasterPassword);
        await firstWriteStarted.Task;
        Task<LoginItem> save = service.AddLoginAsync(Login("Second"));

        releaseFirstWrite.SetResult();
        await Task.WhenAll(rotation, save);

        // The save ran after the rotation and therefore under the rotated key.
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    // ---- 4. Disposal ----

    [Fact]
    public async Task Dispose_WipesTheKey_AndRejectsFurtherUse()
    {
        (VaultService service, _) = await UnlockedWithOneItemAsync();

        service.Dispose();
        service.Dispose(); // idempotent

        Assert.False(service.HoldsKeyMaterial);
        Assert.Empty(service.Items);
        Assert.NotEqual(VaultState.Unlocked, service.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.UnlockAsync(MasterPassword));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AddLoginAsync(Login("Second")));
    }
}
