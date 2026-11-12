using System.Text;
using BitosX.ApiClient.Sync;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Crypto;

namespace BitosX.LiveTests;

/// <summary>
/// Authenticator keys across two devices, against the real API (docs/TOTP.md): a key added on one
/// device goes up inside the encrypted vault (content schema version 2), comes down on the other,
/// and both make the same code at the same instant; removing it there brings version 1 back.
/// </summary>
public sealed class TotpLiveTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The schema version inside a device's vault file, read with the master password.</summary>
    private static async Task<int> SchemaVersionAsync(Device device, string masterPassword)
    {
        EncryptedVault envelope = EncryptedVaultSerializer.Deserialize(await device.EnvelopeBytesAsync());
        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(envelope, MasterPasswordEncoding.Encode(masterPassword));
        string json = Encoding.UTF8.GetString(unlocked.Plaintext);
        Assert.StartsWith("{\"schemaVersion\":", json, StringComparison.Ordinal);
        return json["{\"schemaVersion\":".Length] - '0';
    }

    [LiveFact]
    public async Task AKeyAddedOnOneDevice_SyncsToTheOther_AndBothMakeTheSameCode()
    {
        // One instant, the same on both devices: the codes are made by each device from its own copy.
        var instant = new DateTimeOffset(2026, 11, 12, 12, 0, 7, TimeSpan.Zero);
        var clock = new FixedClock(instant);

        (string email, string password) = await LiveStack.NewAccountAsync();
        string master = LiveStack.NewPassword();
        using var first = new Device(clock);
        using var second = new Device(clock);
        await first.CreateVaultAsync(master);
        await second.CreateVaultAsync(LiveStack.NewPassword());
        await first.SignInAsync(email, password);
        await second.SignInAsync(email, password);

        // The key is typed into the first device as a site shows it: an otpauth:// link.
        TotpKey key = TotpKeyParser.Parse(
            "otpauth://totp/FAKE:someone@example.test?secret=gezd gnbv gy3t qojq gezd gnbv gy3t qojq&issuer=FAKE&algorithm=SHA256&digits=8&period=60").Key!;
        await first.Vault.AddLoginAsync(new LoginItem
        {
            Name = "With a key", Username = "someone@example.test", Password = LiveStack.NewPassword(), Totp = key,
        });
        Assert.Equal(2, await SchemaVersionAsync(first, master));

        Assert.Equal(SyncOutcome.Uploaded, (await first.Sync.SyncAsync()).Outcome);

        // The second device takes the server's vault, which replaces its own, and opens it.
        SyncDownload download = await second.Sync.PrepareDownloadAsync(master);
        Assert.Equal(1, download.Preview.ItemCount);
        await second.Sync.CommitDownloadAsync(download);
        await second.Vault.UnlockAsync(master);
        await second.SignInAsync(email, password);
        Assert.Equal(await first.EnvelopeBytesAsync(), await second.EnvelopeBytesAsync());
        Assert.Equal(2, await SchemaVersionAsync(second, master));

        LoginItem onFirst = Assert.Single(first.Vault.Items.OfType<LoginItem>());
        LoginItem onSecond = Assert.Single(second.Vault.Items.OfType<LoginItem>());
        Assert.Equal(key, onSecond.Totp);

        TotpCode codeOnFirst = TotpGenerator.Generate(onFirst.Totp!, clock.GetUtcNow());
        TotpCode codeOnSecond = TotpGenerator.Generate(onSecond.Totp!, clock.GetUtcNow());
        Assert.Equal(codeOnFirst.Code, codeOnSecond.Code);
        Assert.Equal(8, codeOnSecond.Code.Length);
        Assert.Equal(53, codeOnSecond.SecondsLeft); // 12:00:07, a step of 60
        Assert.Equal(TotpGenerator.Generate(key, instant.ToUnixTimeSeconds()).Code, codeOnSecond.Code);

        // Removing the key on the second device goes up as one change, and takes version 1 back with it.
        await second.Vault.UpdateItemAsync(onSecond with { Totp = null });
        Assert.Equal(1, await SchemaVersionAsync(second, master));
        Assert.Equal(SyncOutcome.Uploaded, (await second.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.ServerAhead, (await first.Sync.SyncAsync()).Outcome);
        SyncDownload back = await first.Sync.PrepareDownloadAsync(master);
        await first.Sync.CommitDownloadAsync(back);
        await first.Vault.UnlockAsync(master);
        Assert.Null(Assert.Single(first.Vault.Items.OfType<LoginItem>()).Totp);
        Assert.Equal(1, await SchemaVersionAsync(first, master));
    }
}
