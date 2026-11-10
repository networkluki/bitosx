using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Adding several items at once (what an import does): one write for all of them, and either all of
/// them are stored or none is.
/// </summary>
public sealed class VaultServiceBulkAddTests
{
    private const string MasterPassword = "correct horse battery";

    private static VaultService CreateService(InstrumentedVaultStorage storage) =>
        new(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    private static LoginItem Login(string name) =>
        new() { Name = name, Username = "user", Password = "FAKE-P@ssw0rd-123" };

    private static List<LoginItem> Logins(int count, string prefix = "Item ") =>
        [.. Enumerable.Range(1, count).Select(i => Login(prefix + i))];

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedAsync(int existing = 0)
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        for (int i = 1; i <= existing; i++)
        {
            await service.AddLoginAsync(Login("Existing " + i));
        }

        return (service, storage);
    }

    private static async Task<IReadOnlyList<VaultItem>> ItemsAfterReopenAsync(InstrumentedVaultStorage storage)
    {
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(MasterPassword);
        return reopen.Items;
    }

    [Fact]
    public async Task AllItems_AreStored_InOneWrite()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 2);
        int writesBefore = storage.WriteCount;
        List<LoginItem> items = Logins(250);

        IReadOnlyList<LoginItem> added = await service.AddLoginsAsync(_ => items);

        Assert.Equal(writesBefore + 1, storage.WriteCount); // one write for 250 items
        Assert.Equal(250, added.Count);
        Assert.Equal(252, service.Items.Count);

        IReadOnlyList<VaultItem> stored = await ItemsAfterReopenAsync(storage);
        Assert.Equal(252, stored.Count);
        Assert.Equal(
            ["Existing 1", "Existing 2", .. items.Select(i => i.Name)],
            stored.Select(i => i.Name)); // after what was there, in the order given
    }

    [Fact]
    public async Task WhenTheWriteFails_NothingWasAdded()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        EncryptedVault before = storage.Stored!;
        int writesBefore = storage.WriteCount;
        storage.FailNextWrite = true;

        await Assert.ThrowsAsync<IOException>(() => service.AddLoginsAsync(_ => Logins(40)));

        Assert.Same(before, storage.Stored);                  // the stored vault is the one from before
        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Equal(["Existing 1"], service.Items.Select(i => i.Name)); // and so is what the session shows
        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Single(await ItemsAfterReopenAsync(storage));

        // The session is still usable: the next attempt goes through, as one write.
        await service.AddLoginsAsync(_ => Logins(40));
        Assert.Equal(writesBefore + 1, storage.WriteCount);
        Assert.Equal(41, service.Items.Count);
    }

    [Fact]
    public async Task OneInvalidItem_MeansNoneIsAdded_AndNothingIsWritten()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        int writesBefore = storage.WriteCount;
        List<LoginItem> items = Logins(10);
        items[7] = items[7] with { Name = "   " }; // not a valid item

        await Assert.ThrowsAsync<VaultValidationException>(() => service.AddLoginsAsync(_ => items));

        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Single(service.Items);
        Assert.Single(await ItemsAfterReopenAsync(storage));
    }

    [Fact]
    public async Task TheSameIdTwice_MeansNoneIsAdded()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        int writesBefore = storage.WriteCount;
        List<LoginItem> items = Logins(5);
        items[4] = items[4] with { Id = items[1].Id };

        await Assert.ThrowsAsync<DuplicateItemException>(() => service.AddLoginsAsync(_ => items));

        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task AnIdTheVaultAlreadyHas_MeansNoneIsAdded()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        int writesBefore = storage.WriteCount;
        List<LoginItem> items = Logins(5);
        items[2] = items[2] with { Id = service.Items[0].Id };

        await Assert.ThrowsAsync<DuplicateItemException>(() => service.AddLoginsAsync(_ => items));

        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task WhenTheChooserThrows_NothingIsWritten_AndItsExceptionIsWhatTheCallerSees()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        int writesBefore = storage.WriteCount;

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AddLoginsAsync(_ => throw new InvalidDataException("refused")));

        Assert.Equal("refused", failure.Message);
        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task WhenTheChooserReturnsNothing_NothingIsWritten()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        EncryptedVault before = storage.Stored!;
        int writesBefore = storage.WriteCount;

        IReadOnlyList<LoginItem> added = await service.AddLoginsAsync(_ => []);

        Assert.Empty(added);
        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Same(before, storage.Stored);
    }

    [Fact]
    public async Task TheChooser_IsGivenWhatTheVaultHolds_AndCannotChangeIt()
    {
        (VaultService service, _) = await UnlockedAsync(existing: 3);
        IReadOnlyList<VaultItem>? seen = null;

        await service.AddLoginsAsync(current =>
        {
            seen = current;
            return Logins(2);
        });

        // What it was shown is what was there before, not the vault the items were added to.
        Assert.NotNull(seen);
        Assert.Equal(["Existing 1", "Existing 2", "Existing 3"], seen.Select(i => i.Name));
        Assert.Equal(5, service.Items.Count);
    }

    [Fact]
    public async Task TheChooser_RunsAfterAnEarlierChangeIsStored_SoItSeesThatChange()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);

        // A save that is held in the middle of its write...
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeWriteAsync = () => release.Task;
        Task<LoginItem> earlier = service.AddLoginAsync(Login("Saved a moment before"));

        // ...and several items added while it is: they wait for it, and decide on what it left.
        IReadOnlyList<VaultItem>? seen = null;
        Task<IReadOnlyList<LoginItem>> bulk = service.AddLoginsAsync(current =>
        {
            seen = current;
            return Logins(2);
        });

        Assert.Null(seen); // not asked yet: the earlier save holds the vault
        storage.BeforeWriteAsync = null;
        release.SetResult();
        await earlier;
        await bulk;

        Assert.NotNull(seen);
        Assert.Equal(["Existing 1", "Saved a moment before"], seen.Select(i => i.Name));
        Assert.Equal(4, service.Items.Count);
    }

    [Fact]
    public async Task WhileLocked_ItIsRefused_AndTheChooserIsNotAsked()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        service.Lock();
        int writesBefore = storage.WriteCount;
        bool asked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLoginsAsync(_ =>
        {
            asked = true;
            return Logins(3);
        }));

        Assert.False(asked);
        Assert.Equal(writesBefore, storage.WriteCount);
    }

    [Fact]
    public async Task WhenTheVaultChangedOnDisk_NothingIsAdded()
    {
        (VaultService stale, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        VaultService other = CreateService(storage);
        await other.UnlockAsync(MasterPassword);
        await other.AddLoginAsync(Login("Added elsewhere"));
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginsAsync(_ => Logins(5)));

        Assert.Same(before, storage.Stored);
        Assert.Single(stale.Items);
        Assert.Equal(2, (await ItemsAfterReopenAsync(storage)).Count);
    }

    [Fact]
    public async Task WhenTheWriteTookEffectBeforeItThrew_AllItemsAreThere()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        storage.ReplaceThenThrowNextWrite = true;

        await service.AddLoginsAsync(_ => Logins(6)); // reconciled against storage: it is the new vault

        Assert.Equal(7, service.Items.Count);
        Assert.Equal(7, (await ItemsAfterReopenAsync(storage)).Count);
    }

    [Fact]
    public async Task WhenTheVaultIsLockedWhileTheItemsAreWritten_TheyAreStored_AndTheSessionStaysLocked()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync(existing: 1);
        storage.OnWrite = service.Lock;

        await service.AddLoginsAsync(_ => Logins(3));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
        Assert.Empty(service.Items);
        storage.OnWrite = null;
        Assert.Equal(4, (await ItemsAfterReopenAsync(storage)).Count);
    }

    [Fact]
    public async Task ANullChooser_OrANullResult_IsAnError()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        int writesBefore = storage.WriteCount;

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AddLoginsAsync(null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLoginsAsync(_ => null!));

        Assert.Equal(writesBefore, storage.WriteCount);
    }
}
