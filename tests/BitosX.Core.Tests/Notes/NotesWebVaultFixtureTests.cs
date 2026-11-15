using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Serialization;
using BitosX.Crypto;

namespace BitosX.Core.Tests.Notes;

/// <summary>
/// <c>tests/fixtures/web/notes.vault.json</c> is an encrypted vault with secure notes that the web vault
/// opens in its tests (docs/NOTES.md §6.2). It was written with this repository's code; these tests open
/// it with the same code, so that it can never say something the desktop does not.
/// </summary>
public sealed class NotesWebVaultFixtureTests
{
    [Fact]
    public void TheVault_OpensWithTheDesktopsCode_AndIsWhatItsMetaFileSays()
    {
        string directory = NoteVaultContentTests.FixtureDirectory("web");
        byte[] envelope = File.ReadAllBytes(Path.Combine(directory, "notes.vault.json"));
        JsonObject meta = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "notes.meta.json")))!.AsObject();

        Assert.Equal(meta["sizeBytes"]!.GetValue<int>(), envelope.Length);
        Assert.Equal(meta["fingerprint"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(envelope)));

        byte[] password = MasterPasswordEncoding.Encode(meta["typedPassword"]!.GetValue<string>());
        Assert.Equal(meta["passwordBytesHex"]!.GetValue<string>(), Convert.ToHexStringLower(password));

        EncryptedVault encrypted = EncryptedVaultSerializer.Deserialize(envelope);
        Assert.Equal(1024, encrypted.KdfParameters.MemoryKiB); // the test profile
        Assert.Equal(meta["kdf"]!["MemoryKiB"]!.GetValue<int>(), encrypted.KdfParameters.MemoryKiB);
        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(encrypted, password);
        string plaintext = Encoding.UTF8.GetString(unlocked.Plaintext);
        Assert.Equal(meta["plaintextUtf8"]!.GetValue<string>(), plaintext);

        // Written by the desktop's own serializer: reading and writing it again gives the same bytes.
        Vault vault = VaultSerializer.Deserialize(unlocked.Plaintext);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault)));
        Assert.Equal(3, vault.SchemaVersion);
        Assert.Equal(3, vault.RequiredSchemaVersion);

        // What the contract asks of it: two notes (one of several lines, outside ASCII, with what looks
        // like markup; one without a text), a login with a key and one without.
        NoteItem[] notes = [.. vault.Items.OfType<NoteItem>()];
        LoginItem[] logins = [.. vault.Items.OfType<LoginItem>()];
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, n => n.Notes is { } text && text.Contains('\n') && text.Contains('<') && text.Any(c => c > 0x7F));
        Assert.Contains(notes, n => n.Notes is null);
        Assert.Equal(2, logins.Length);
        Assert.Single(logins, l => l.Totp is not null);

        JsonArray items = meta["items"]!.AsArray();
        Assert.Equal(vault.Items.Count, items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            VaultItem item = vault.Items[i];
            JsonNode expected = items[i]!;
            Assert.Equal(expected["id"]!.GetValue<string>(), item.Id.ToString("D"));
            Assert.Equal(expected["type"]!.GetValue<string>(), item is NoteItem ? "note" : "login");
            Assert.Equal(expected["name"]!.GetValue<string>(), item.Name);
            Assert.Equal(expected["notes"]?.GetValue<string>(), item.Notes);
            Assert.Equal(expected["favorite"]!.GetValue<bool>(), item.Favorite);
            if (item is LoginItem login)
            {
                Assert.Equal(expected["hasTotp"]!.GetValue<bool>(), login.Totp is not null);
            }
        }
    }
}
