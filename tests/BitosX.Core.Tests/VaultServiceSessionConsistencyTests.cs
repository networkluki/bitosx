using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Security regression tests (0.5.1) for the relationship between an unlocked session and the stored
/// vault. Each scenario reproduces a real defect found in review:
/// (1) a session whose stored vault was changed by another instance could overwrite it — silently
///     undoing a master-password change or key rotation, or producing a vault no password can open;
/// (2) a write that threw after taking effect left memory and disk disagreeing;
/// (3) <see cref="IVaultService.Items"/> showed changes that were never persisted.
/// </summary>
public sealed class VaultServiceSessionConsistencyTests
{
    private const string MasterPassword = "correct horse battery";
    private const string NewPassword = "a different master password";

    private static VaultService CreateService(InstrumentedVaultStorage storage) =>
        new(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    private static LoginItem Login(string name) =>
        new() { Name = name, Username = "user", Password = "P@ssw0rd-Strong-123" };

    private static byte[] Hash(EncryptedVault v) => EncryptedVaultFingerprint.Compute(v);

    /// <summary>Two services over one storage: the second plays another running instance of the app.</summary>
    private static async Task<(VaultService Stale, VaultService Other, InstrumentedVaultStorage Storage)> TwoInstancesAsync()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService stale = CreateService(storage);
        await stale.CreateVaultAsync(MasterPassword);
        await stale.AddLoginAsync(Login("First"));

        VaultService other = CreateService(storage);
        await other.UnlockAsync(MasterPassword);
        return (stale, other, storage);
    }

    private static async Task<int> ItemCountAfterReopenAsync(InstrumentedVaultStorage storage, string password)
    {
        VaultService reopen = CreateService(storage);
        await reopen.UnlockAsync(password);
        return reopen.Items.Count;
    }

    // ---- 1. A stale session must never overwrite a vault it does not know about ----

    [Fact]
    public async Task Save_AfterAnotherInstanceChangedThePassword_FailsClosed_PasswordChangeSurvives()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.ChangeMasterPasswordAsync(MasterPassword, NewPassword);
        EncryptedVault afterChange = storage.Stored!;

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginAsync(Login("Second")));

        // The stale save wrote nothing, so the old password did not come back.
        Assert.Same(afterChange, storage.Stored);
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, NewPassword));
        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => CreateService(storage).UnlockAsync(MasterPassword));
    }

    [Fact]
    public async Task Save_AfterAnotherInstanceRotatedTheKey_FailsClosed_RotationSurvives()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.RotateVaultKeyAsync(MasterPassword);
        EncryptedVault rotated = storage.Stored!;

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginAsync(Login("Second")));

        // The retired key was not written back over the rotated vault.
        Assert.Same(rotated, storage.Stored);
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Save_AfterAnotherInstanceSavedAnItem_FailsClosed_OtherItemIsNotLost()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.AddLoginAsync(Login("Added elsewhere"));
        byte[] before = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginAsync(Login("Second")));

        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task EveryMutation_OnAStaleSession_FailsClosed_AndKeepsTheSessionUnchanged()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        Guid id = stale.Items[0].Id;
        await other.AddLoginAsync(Login("Added elsewhere"));
        byte[] before = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginAsync(Login("Second")));
        await Assert.ThrowsAsync<VaultChangedExternallyException>(
            () => stale.UpdateItemAsync((LoginItem)stale.Items[0] with { Name = "Renamed" }));
        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.ToggleFavoriteAsync(id));
        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.DeleteItemAsync(id));

        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(VaultState.Unlocked, stale.State);
        VaultItem untouched = Assert.Single(stale.Items);
        Assert.Equal("First", untouched.Name);
        Assert.False(untouched.Favorite);
    }

    [Fact]
    public async Task ChangeMasterPassword_AfterAnotherInstanceRotatedTheKey_FailsClosed_VaultStillOpens()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.RotateVaultKeyAsync(MasterPassword);
        EncryptedVault rotated = storage.Stored!;

        // Before the fix this succeeded, adopted a header wrapping a key the session did not hold, and
        // the next save produced a vault that neither password could open.
        await Assert.ThrowsAsync<VaultChangedExternallyException>(
            () => stale.ChangeMasterPasswordAsync(MasterPassword, NewPassword));

        Assert.Same(rotated, storage.Stored);
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task ChangeMasterPassword_AfterAnotherInstanceSavedAnItem_FailsClosed()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.AddLoginAsync(Login("Added elsewhere"));
        byte[] before = Hash(storage.Stored!);

        await Assert.ThrowsAsync<VaultChangedExternallyException>(
            () => stale.ChangeMasterPasswordAsync(MasterPassword, NewPassword));

        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task StaleSession_RecoversByLockingAndReopening()
    {
        (VaultService stale, VaultService other, InstrumentedVaultStorage storage) = await TwoInstancesAsync();
        await other.AddLoginAsync(Login("Added elsewhere"));
        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => stale.AddLoginAsync(Login("Second")));

        stale.Lock();
        await stale.UnlockAsync(MasterPassword);
        await stale.AddLoginAsync(Login("Second"));

        Assert.Equal(3, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Save_WhenTheStoredVaultIsMissing_WritesTheSessionBack()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));

        // The file was deleted while the vault was open. The session holds the last copy of the data;
        // refusing to write it would turn a recoverable accident into data loss.
        storage.DeleteExternally();
        await service.AddLoginAsync(Login("Second"));

        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Save_WhenTheStoredVaultCannotBeRead_FailsClosed_AndDoesNotOverwriteIt()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        EncryptedVault stored = storage.Stored!;
        int writes = storage.WriteCount;

        // E.g. replaced by a vault in a newer format that this build cannot parse.
        storage.ThrowOnRead = new UnsupportedVaultFormatException("Unsupported vault format version 2.");

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => service.AddLoginAsync(Login("First")));

        Assert.Same(stored, storage.Stored);
        Assert.Equal(writes, storage.WriteCount);
        Assert.Empty(service.Items);
    }

    // ---- 2. A write that throws is reconciled against storage, never assumed ----

    [Fact]
    public async Task Save_WriteReplacesThenThrows_IsTreatedAsSaved()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        storage.ReplaceThenThrowNextWrite = true;
        await service.AddLoginAsync(Login("First")); // the item is on disk, so this is a success

        Assert.Single(service.Items);

        // Memory matches disk: the next save passes the consistency gate.
        await service.AddLoginAsync(Login("Second"));
        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Save_WriteLeavesIndeterminateDisk_LocksSession_FailsClosed()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        var otherStorage = new InstrumentedVaultStorage();
        await CreateService(otherStorage).CreateVaultAsync("some-other-password-xyz");
        storage.StoreThenThrowNextWrite = otherStorage.Stored;

        await Assert.ThrowsAsync<VaultInconsistentStateException>(() => service.AddLoginAsync(Login("First")));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.Empty(service.Items);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task Save_WriteFails_AndStoredVaultCannotBeRead_LocksSession_FailsClosed()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        storage.FailNextWrite = true;
        storage.OnWrite = () => storage.ThrowOnRead = new IOException("Disk went away.");

        await Assert.ThrowsAsync<VaultInconsistentStateException>(() => service.AddLoginAsync(Login("First")));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task Save_WriteFails_WhenNoVaultWasStored_SurfacesTheWriteFailure()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        storage.DeleteExternally();

        storage.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => service.AddLoginAsync(Login("First")));

        // Nothing was there before and nothing is there now: the session is intact and can retry.
        Assert.Equal(VaultState.Unlocked, service.State);
        await service.AddLoginAsync(Login("First"));
        Assert.Equal(1, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task ChangeMasterPassword_WriteReplacesThenThrows_AdoptsTheNewPassword()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));

        storage.ReplaceThenThrowNextWrite = true;
        await service.ChangeMasterPasswordAsync(MasterPassword, NewPassword); // on disk, so a success

        // A later save must keep the NEW wrapping rather than restore the old password.
        await service.AddLoginAsync(Login("Second"));

        Assert.Equal(2, await ItemCountAfterReopenAsync(storage, NewPassword));
        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => CreateService(storage).UnlockAsync(MasterPassword));
    }

    [Fact]
    public async Task ChangeMasterPassword_WriteLeavesIndeterminateDisk_LocksSession_FailsClosed()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        var otherStorage = new InstrumentedVaultStorage();
        await CreateService(otherStorage).CreateVaultAsync("some-other-password-xyz");
        storage.StoreThenThrowNextWrite = otherStorage.Stored;

        await Assert.ThrowsAsync<VaultInconsistentStateException>(
            () => service.ChangeMasterPasswordAsync(MasterPassword, NewPassword));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task ChangeMasterPassword_CommitWrite_IsIssuedWithNonCancellableToken()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);

        using var cts = new CancellationTokenSource();
        await service.ChangeMasterPasswordAsync(MasterPassword, NewPassword, cts.Token);

        Assert.False(storage.LastWriteToken.CanBeCanceled);
    }

    [Fact]
    public async Task ChangeMasterPassword_CancelledBeforeCommit_WritesNothing()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        byte[] before = Hash(storage.Stored!);
        int writes = storage.WriteCount;

        using var cts = new CancellationTokenSource();
        storage.CancelOnRead = cts;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ChangeMasterPasswordAsync(MasterPassword, NewPassword, cts.Token));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Equal(0, await ItemCountAfterReopenAsync(storage, MasterPassword));
    }

    [Fact]
    public async Task Save_CancelledBeforeCommit_WritesNothing_AndKeepsTheSessionUnchanged()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        byte[] before = Hash(storage.Stored!);
        int writes = storage.WriteCount;

        using var cts = new CancellationTokenSource();
        storage.CancelOnRead = cts;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AddLoginAsync(Login("First"), cts.Token));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Equal(before, Hash(storage.Stored!));
        Assert.Empty(service.Items);
    }

    // ---- 3. Items only ever shows what is on disk ----

    [Fact]
    public async Task Items_DoNotShowAChange_UntilItHasBeenPersisted()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));

        int seenDuringWrite = -1;
        storage.OnWrite = () => seenDuringWrite = service.Items.Count;
        await service.AddLoginAsync(Login("Second"));

        Assert.Equal(1, seenDuringWrite);        // not yet persisted -> not yet visible
        Assert.Equal(2, service.Items.Count);    // persisted -> visible
    }

    [Fact]
    public async Task Items_ReturnedEarlier_AreNotChangedByLaterOperations()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(Login("First"));

        IReadOnlyList<VaultItem> before = service.Items;
        await service.AddLoginAsync(Login("Second"));
        await service.DeleteItemAsync(before[0].Id);

        // A list handed to the UI is a stable snapshot; it can be enumerated from any thread.
        VaultItem still = Assert.Single(before);
        Assert.Equal("First", still.Name);
        Assert.Equal("Second", Assert.Single(service.Items).Name);
    }

    [Fact]
    public async Task RejectedChange_WritesNothing_AndLeavesItemsUnchanged()
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(MasterPassword);
        LoginItem first = await service.AddLoginAsync(Login("First"));
        int writes = storage.WriteCount;

        await Assert.ThrowsAsync<VaultValidationException>(() => service.AddLoginAsync(Login("   ")));
        await Assert.ThrowsAsync<DuplicateItemException>(() => service.AddLoginAsync(first));
        await Assert.ThrowsAsync<ItemNotFoundException>(() => service.DeleteItemAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ItemNotFoundException>(() => service.ToggleFavoriteAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ItemNotFoundException>(() => service.UpdateItemAsync(Login("Unknown")));

        Assert.Equal(writes, storage.WriteCount);
        Assert.Single(service.Items);
    }
}
