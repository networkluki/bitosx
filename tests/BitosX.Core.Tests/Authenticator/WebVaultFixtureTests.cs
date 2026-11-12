using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Serialization;
using BitosX.Crypto;

namespace BitosX.Core.Tests.Authenticator;

/// <summary>
/// <c>tests/fixtures/web/totp.vault.json</c> is an encrypted vault the web vault opens in its tests
/// (docs/TOTP.md §7.3). It was written with this repository's code; these tests open it with the same
/// code, so that it can never say something the desktop does not.
/// </summary>
public sealed class WebVaultFixtureTests
{
    private static string Directory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "tests", "fixtures", "web");
                if (System.IO.Directory.Exists(path))
                {
                    return path;
                }
            }

            throw new DirectoryNotFoundException("tests/fixtures/web was not found above the test's directory.");
        }
    }

    [Fact]
    public void TheVault_OpensWithTheDesktopsCode_AndIsWhatItsMetaFileSays()
    {
        byte[] envelope = File.ReadAllBytes(Path.Combine(Directory, "totp.vault.json"));
        JsonObject meta = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Directory, "totp.meta.json")))!.AsObject();

        Assert.Equal(meta["sizeBytes"]!.GetValue<int>(), envelope.Length);
        Assert.Equal(meta["fingerprint"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(envelope)));

        byte[] password = MasterPasswordEncoding.Encode(meta["typedPassword"]!.GetValue<string>());
        Assert.Equal(meta["passwordBytesHex"]!.GetValue<string>(), Convert.ToHexStringLower(password));

        EncryptedVault encrypted = EncryptedVaultSerializer.Deserialize(envelope);
        Assert.Equal(1024, encrypted.KdfParameters.MemoryKiB); // the test profile
        UnlockResult unlocked = VaultCryptor.CreateDefault().Unlock(encrypted, password);
        Assert.Equal(meta["plaintextUtf8"]!.GetValue<string>(), Encoding.UTF8.GetString(unlocked.Plaintext));

        Vault vault = VaultSerializer.Deserialize(unlocked.Plaintext);
        Assert.Equal(2, vault.SchemaVersion);
        LoginItem[] logins = [.. vault.Items.Cast<LoginItem>()];
        Assert.Equal(4, logins.Length);
        Assert.Equal(3, logins.Count(l => l.Totp is not null));
        Assert.Equal(3, logins.Where(l => l.Totp is not null).Select(l => l.Totp!.Algorithm).Distinct().Count());
        Assert.Equal(3, logins.Where(l => l.Totp is not null).Select(l => l.Totp!.Digits).Distinct().Count());
        Assert.Equal(3, logins.Where(l => l.Totp is not null).Select(l => l.Totp!.Period).Distinct().Count());

        foreach (JsonNode? expected in meta["totpCodes"]!.AsArray())
        {
            LoginItem login = logins.Single(l => l.Id == Guid.Parse(expected!["id"]!.GetValue<string>()));
            TotpCode code = TotpGenerator.Generate(login.Totp!, expected["unixSeconds"]!.GetValue<long>());
            Assert.Equal(expected["code"]!.GetValue<string>(), code.Code);
            Assert.Equal(expected["display"]!.GetValue<string>(), code.Display);
            Assert.Equal(expected["secondsLeft"]!.GetValue<int>(), code.SecondsLeft);
        }
    }
}
