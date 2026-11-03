using System.Text.Json;
using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.Core.Services;
using BitosX.Core.Storage;
using BitosX.Storage;

namespace BitosX.LiveTests;

/// <summary>
/// Encrypted sync between two devices of one account, against the real API: a real vault made by
/// BitosX.Core goes up and comes down byte for byte, the sync record binds it to the server and the
/// account, revisions are compare-and-set, and a removed vault never restarts the revisions.
/// </summary>
public sealed class SyncLiveTests
{
    private static void AssertUtc(DateTimeOffset moment, string what)
        => Assert.True(moment.Offset == TimeSpan.Zero, $"{what} is not in UTC: {moment:O}");

    /// <summary>
    /// The same stored vault. The time is compared to the millisecond: the server's answer to an upload
    /// carries it to 100 ns, what it stores (and every later answer) to the microsecond.
    /// </summary>
    private static void AssertSame(SyncStatus expected, SyncStatus? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.SizeBytes, actual.SizeBytes);
        Assert.True((expected.UpdatedAt - actual.UpdatedAt).Duration() < TimeSpan.FromMilliseconds(1), $"{expected.UpdatedAt:O} / {actual.UpdatedAt:O}");
    }

    /// <summary>Two devices signed in to one new account, each with a vault of its own; the first one's is uploaded.</summary>
    private static async Task<(Device First, Device Second, string Email, string Password, string FirstMasterPassword)> TwoDevicesAsync()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        string firstMaster = LiveStack.NewPassword();
        var first = new Device();
        var second = new Device();
        await first.CreateVaultAsync(firstMaster);
        await second.CreateVaultAsync(LiveStack.NewPassword());
        await first.AddLoginAsync("first device");
        await first.SignInAsync(email, password);
        await second.SignInAsync(email, password);

        SyncResult uploaded = await first.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.Uploaded, uploaded.Outcome);
        return (first, second, email, password, firstMaster);
    }

    /// <summary>Takes the server's vault on <paramref name="device"/>, opens it, and signs in again (taking it locks the vault, which ends the session).</summary>
    private static async Task TakeTheServersVaultAsync(Device device, string masterPassword, string email, string password)
    {
        SyncDownload download = await device.Sync.PrepareDownloadAsync(masterPassword);
        await device.Sync.CommitDownloadAsync(download);
        Assert.Equal(VaultState.Locked, device.Vault.State);
        Assert.False(device.Account.IsSignedIn);
        await device.Vault.UnlockAsync(masterPassword);
        await device.SignInAsync(email, password);
    }

    [LiveFact]
    public async Task ARealVault_GoesUp_AndComesDownOnAnotherDevice_ByteForByte()
    {
        (Device first, Device second, string email, string password, string master) = await TwoDevicesAsync();
        using (first)
        using (second)
        {
            // What the server holds is the vault file of the first device, exactly.
            byte[] local = await first.EnvelopeBytesAsync();
            SyncEnvelope stored = (await first.Account.DownloadEnvelopeAsync())!;
            Assert.Equal(local, stored.Envelope);
            Assert.Equal(HttpBitosXApiClient.Fingerprint(local), stored.Status.Fingerprint);
            Assert.Equal(local.Length, stored.Status.SizeBytes);
            AssertUtc(stored.Status.UpdatedAt, "updatedAt");

            // The sync record (format 2) binds what was synced to this server and this account.
            SyncState record = (await first.SyncRecord.LoadAsync())!;
            Assert.True(record.IsFor(first.Server.BaseAddress.ToString(), (await first.Account.GetProfileAsync()).AccountId));
            Assert.Equal(stored.Status.Revision, record.Revision);
            Assert.Equal(stored.Status.Fingerprint, record.ServerFingerprint);
            using (JsonDocument file = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Location.SyncStateFilePath)))
            {
                Assert.Equal(FileSyncStateStore.CurrentVersion, file.RootElement.GetProperty("version").GetInt32());
            }

            // The second device never synced, and has a vault of its own: nothing is decided for it.
            SyncResult undecided = await second.Sync.SyncAsync();
            Assert.Equal(SyncOutcome.Conflict, undecided.Outcome);
            Assert.Equal(stored.Status.Revision, undecided.Server!.Revision);

            // The server's vault opens there with its own master password, and replaces the local one.
            SyncDownload download = await second.Sync.PrepareDownloadAsync(master);
            Assert.Equal(1, download.Preview.ItemCount);
            await TakeTheServersVaultAsync(second, master, email, password);
            Assert.Equal(local, await second.EnvelopeBytesAsync());
            Assert.Contains(second.Vault.Items, i => i.Name == "first device");

            Assert.Equal(SyncOutcome.UpToDate, (await second.Sync.SyncAsync()).Outcome);
            Assert.Equal(SyncOutcome.UpToDate, (await first.Sync.SyncAsync()).Outcome);
        }
    }

    [LiveFact]
    public async Task ChangesOnBothDevices_AreAConflict_AndTheServerKeepsWhatItHad()
    {
        (Device first, Device second, string email, string password, string master) = await TwoDevicesAsync();
        using (first)
        using (second)
        {
            await TakeTheServersVaultAsync(second, master, email, password);
            long common = (await second.Sync.SyncAsync()).Server!.Revision;

            await first.AddLoginAsync("changed on the first");
            SyncResult up = await first.Sync.SyncAsync();
            Assert.Equal(SyncOutcome.Uploaded, up.Outcome);
            Assert.True(up.Server!.Revision > common);

            await second.AddLoginAsync("changed on the second");
            SyncResult conflict = await second.Sync.SyncAsync();
            Assert.Equal(SyncOutcome.Conflict, conflict.Outcome);
            AssertSame(up.Server, conflict.Server);

            // Compare-and-set, as the server answers it: an upload based on the old revision is a 409.
            byte[] secondVault = await second.EnvelopeBytesAsync();
            ApiException refused = await Assert.ThrowsAsync<ApiException>(
                () => second.Account.UploadEnvelopeAsync(common, secondVault));
            Assert.Equal(ApiFailure.Conflict, refused.Failure);
            Assert.Equal(409, refused.StatusCode);
            AssertSame(up.Server, await second.Account.GetSyncStatusAsync());

            // The way out the user chooses: this device's vault replaces the server's.
            SyncResult overwritten = await second.Sync.OverwriteServerAsync();
            Assert.Equal(SyncOutcome.Uploaded, overwritten.Outcome);
            Assert.True(overwritten.Server!.Revision > up.Server.Revision);
            Assert.Equal(SyncOutcome.ServerAhead, (await first.Sync.SyncAsync()).Outcome);
        }
    }

    [LiveFact]
    public async Task AVaultRemovedFromTheServer_NeverRestartsTheRevisions()
    {
        (Device first, Device second, string email, string password, string master) = await TwoDevicesAsync();
        using (first)
        using (second)
        {
            await TakeTheServersVaultAsync(second, master, email, password);
            long before = (await second.Sync.SyncAsync()).Server!.Revision;

            await first.Sync.DeleteFromServerAsync();
            Assert.Null(await first.Account.GetSyncStatusAsync());
            Assert.Null(await first.Account.DownloadEnvelopeAsync());
            Assert.Null(await first.SyncRecord.LoadAsync());

            // Uploaded again against "nothing stored": a revision after every earlier one, never 1 again.
            await first.AddLoginAsync("after the removal");
            SyncResult again = await first.Sync.SyncAsync();
            Assert.Equal(SyncOutcome.Uploaded, again.Outcome);
            Assert.True(again.Server!.Revision > before, $"revision {again.Server.Revision} after {before}");

            // The other device, which synced the removed vault, sees a newer vault, not the one it had.
            Assert.Equal(SyncOutcome.ServerAhead, (await second.Sync.SyncAsync()).Outcome);
        }
    }
}
