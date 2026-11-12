using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Validation;

namespace BitosX.Core.Tests.Authenticator;

/// <summary>
/// The files under <c>tests/fixtures/totp</c> are exactly what this library produces, and together
/// they show the web vault every rule of docs/TOTP.md §3 and §4.
/// </summary>
public sealed class TotpFixtureTests
{
    private static void Check(string fileName, Func<JsonObject, string> render)
    {
        string path = Path.Combine(TotpFixtures.Directory, fileName);
        string committed = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        string produced = render(TotpFixtures.Read(fileName));

        if (TotpFixtures.IsUpdating)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(produced));
            return;
        }

        Assert.True(
            string.Equals(committed, produced, StringComparison.Ordinal),
            $"{fileName} is not what the library produces. If the change is intended, run the tests with " +
            $"{TotpFixtures.UpdateVariable}=1, look at what changed, and say so in docs/TOTP.md: the web vault follows these files.");
    }

    [Fact]
    public void KeysJson_IsExactlyWhatTheLibraryProduces() => Check("keys.json", TotpFixtures.RenderKeys);

    [Fact]
    public void CodesJson_IsExactlyWhatTheLibraryProduces() => Check("codes.json", TotpFixtures.RenderCodes);

    [Fact]
    public void TheDirectory_HoldsOnlyTheTwoFiles()
        => Assert.Equal(
            ["codes.json", "keys.json"],
            System.IO.Directory.EnumerateFiles(TotpFixtures.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    [Fact]
    public void KeysJson_ShowsEveryOutcome()
    {
        var refusals = new HashSet<string>(StringComparer.Ordinal);
        var algorithms = new HashSet<string>(StringComparer.Ordinal);
        var digits = new HashSet<int>();
        bool empty = false, link = false, plain = false;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonNode? testCase in TotpFixtures.Read("keys.json")["cases"]!.AsArray())
        {
            Assert.True(names.Add(testCase!["name"]!.GetValue<string>()), "two cases have one name");
            JsonObject result = testCase["result"]!.AsObject();
            if (result["refused"] is JsonValue refused)
            {
                refusals.Add(refused.GetValue<string>());
            }
            else if (result["key"] is JsonObject key)
            {
                // Every key a fixture expects is one the vault accepts.
                Assert.True(VaultValidation.IsValidTotpKey(TotpFixtures.KeyOf(key)));
                algorithms.Add(key["algorithm"]!.GetValue<string>());
                digits.Add(key["digits"]!.GetValue<int>());
                bool isLink = TotpFixtures.InputText(testCase["input"]!).Contains("otpauth://", StringComparison.OrdinalIgnoreCase);
                link |= isLink;
                plain |= !isLink;
            }
            else
            {
                empty |= result["empty"]!.GetValue<bool>();
            }
        }

        Assert.Equal(Enum.GetValues<TotpRefusal>().Select(TotpTexts.Code).Order(), refusals.Order());
        Assert.Equal(["SHA1", "SHA256", "SHA512"], algorithms.Order());
        Assert.Equal([6, 7, 8], digits.Order());
        Assert.True(empty && link && plain);
    }

    [Fact]
    public void CodesJson_HoldsRfc6238AppendixB()
    {
        // The RFC's table, written out again here, so that writing the fixture again can never
        // carry a wrong value into it unnoticed.
        string[] expected =
        [
            "59 SHA1 94287082", "59 SHA256 46119246", "59 SHA512 90693936",
            "1111111109 SHA1 07081804", "1111111109 SHA256 68084774", "1111111109 SHA512 25091201",
            "1111111111 SHA1 14050471", "1111111111 SHA256 67062674", "1111111111 SHA512 99943326",
            "1234567890 SHA1 89005924", "1234567890 SHA256 91819424", "1234567890 SHA512 93441116",
            "2000000000 SHA1 69279037", "2000000000 SHA256 90698825", "2000000000 SHA512 38618901",
            "20000000000 SHA1 65353130", "20000000000 SHA256 77737706", "20000000000 SHA512 47863826",
        ];

        var found = new List<string>();
        foreach (JsonNode? testCase in TotpFixtures.Read("codes.json")["cases"]!.AsArray())
        {
            if (!testCase!["name"]!.GetValue<string>().StartsWith("RFC 6238", StringComparison.Ordinal))
            {
                continue;
            }

            TotpKey key = TotpFixtures.KeyOf(testCase["key"]!);
            Assert.Equal(TotpGeneratorTests.RfcKey(key.Algorithm), key);
            found.Add($"{testCase["unixSeconds"]!.GetValue<long>()} {TotpTexts.AlgorithmName(key.Algorithm)} {testCase["result"]!["code"]!.GetValue<string>()}");
        }

        Assert.Equal(expected, found);
    }

    [Fact]
    public void CodesJson_EveryKeyIsOneTheVaultAccepts()
    {
        foreach (JsonNode? testCase in TotpFixtures.Read("codes.json")["cases"]!.AsArray())
        {
            Assert.True(VaultValidation.IsValidTotpKey(TotpFixtures.KeyOf(testCase!["key"]!)), testCase["name"]!.GetValue<string>());
        }
    }
}
