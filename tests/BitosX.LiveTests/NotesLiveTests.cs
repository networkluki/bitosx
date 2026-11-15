using System.Text;
using BitosX.ApiClient.Sync;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Crypto;

namespace BitosX.LiveTests;

/// <summary>
/// Secure notes across two devices, against the real API (docs/NOTES.md): a note added on one device
/// goes up inside the encrypted vault (content schema version 3), comes down on the other with its text
/// exactly as it was, and an edit there comes back; deleting the last note takes the lower version back.
/// The server only ever sees the envelope.
/// </summary>
public sealed class NotesLiveTests
{
    private const string Text = "Recovery codes FAKE-1111 FAKE-2222\r\nräksmörgås <b>not markup</b> \U0001F511\n\tlast line ";

    /// <summary>The decrypted content of a device's vault file, read with the master password.</summary>
    private static async Task<string> ContentAsync(Device device, string masterPassword)
    {
        EncryptedVault envelope = EncryptedVaultSerializer.Deserialize(await device.EnvelopeBytesAsync());
        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(envelope, MasterPasswordEncoding.Encode(masterPassword));
        return Encoding.UTF8.GetString(unlocked.Plaintext);
    }

    [LiveFact]
    public async Task ANoteAddedOnOneDevice_SyncsToTheOther_AndAnEditComesBack()
    {
        (string email, string password) = await LiveStack.NewAccountAsync();
        string master = LiveStack.NewPassword();
        using var first = new Device();
        using var second = new Device();
        await first.CreateVaultAsync(master);
        await second.CreateVaultAsync(LiveStack.NewPassword());
        await first.SignInAsync(email, password);
        await second.SignInAsync(email, password);

        await first.AddLoginAsync("A login");
        NoteItem note = await first.Vault.AddNoteAsync(new NoteItem { Name = "FAKE recovery codes", Notes = Text, Favorite = true });
        Assert.StartsWith("{\"schemaVersion\":3,", await ContentAsync(first, master), StringComparison.Ordinal);

        // What goes up is the envelope: nothing of the note is readable in it.
        byte[] uploaded = await first.EnvelopeBytesAsync();
        Assert.DoesNotContain("FAKE-1111", Encoding.UTF8.GetString(uploaded), StringComparison.Ordinal);
        Assert.Equal(SyncOutcome.Uploaded, (await first.Sync.SyncAsync()).Outcome);

        // The second device takes the server's vault, which replaces its own, and opens it.
        SyncDownload download = await second.Sync.PrepareDownloadAsync(master);
        Assert.Equal(2, download.Preview.ItemCount);
        await second.Sync.CommitDownloadAsync(download);
        await second.Vault.UnlockAsync(master);
        await second.SignInAsync(email, password);
        Assert.Equal(await first.EnvelopeBytesAsync(), await second.EnvelopeBytesAsync());

        NoteItem onSecond = Assert.Single(second.Vault.Items.OfType<NoteItem>());
        Assert.Equal(note, onSecond);
        Assert.Equal(Text, onSecond.Notes);
        Assert.True(onSecond.Favorite);

        // An edit on the second device goes up as one change and comes down on the first.
        const string edited = "FAKE edited text\nline two";
        await second.Vault.UpdateItemAsync(onSecond with { Notes = edited });
        Assert.Equal(SyncOutcome.Uploaded, (await second.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.ServerAhead, (await first.Sync.SyncAsync()).Outcome);
        SyncDownload back = await first.Sync.PrepareDownloadAsync(master);
        await first.Sync.CommitDownloadAsync(back);
        await first.Vault.UnlockAsync(master);
        Assert.Equal(edited, Assert.Single(first.Vault.Items.OfType<NoteItem>()).Notes);

        // Deleting the note there writes version 1 again, and that syncs too.
        await first.SignInAsync(email, password);
        await first.Vault.DeleteItemAsync(note.Id);
        Assert.StartsWith("{\"schemaVersion\":1,", await ContentAsync(first, master), StringComparison.Ordinal);
        Assert.Equal(SyncOutcome.Uploaded, (await first.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.ServerAhead, (await second.Sync.SyncAsync()).Outcome);
        SyncDownload again = await second.Sync.PrepareDownloadAsync(master);
        await second.Sync.CommitDownloadAsync(again);
        await second.Vault.UnlockAsync(master);
        Assert.Empty(second.Vault.Items.OfType<NoteItem>());
        Assert.Single(second.Vault.Items.OfType<LoginItem>());
    }
}
