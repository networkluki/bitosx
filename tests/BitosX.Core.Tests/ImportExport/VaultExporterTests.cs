using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The two unencrypted exports: exactly what is written, and that what is written is read back as
/// the same items.
/// </summary>
public sealed class VaultExporterTests
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static LoginItem Item(
        string name,
        string? username = null,
        string? password = null,
        string? notes = null,
        bool favorite = false,
        params string[] urls)
        => new()
        {
            Name = name,
            Username = username,
            Password = password,
            Notes = notes,
            Favorite = favorite,
            Urls = urls,
            CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
        };

    private static string Text(byte[] file) => Encoding.UTF8.GetString(file);

    // ---- CSV ----

    [Fact]
    public void Csv_IsChromesLayout_EveryValueQuoted_CrLfAfterEveryLine()
    {
        VaultItem[] items =
        [
            Item("Example", "fake.user@example.test", "FAKE-pw-1", "a note", favorite: true, "https://example.com/login", "https://example.org"),
            Item("Bare"),
        ];

        Assert.Equal(
            "name,url,username,password,note\r\n" +
            "\"Example\",\"https://example.com/login\",\"fake.user@example.test\",\"FAKE-pw-1\",\"a note\"\r\n" +
            "\"Bare\",\"\",\"\",\"\",\"\"\r\n",
            Text(VaultExporter.ToCsv(items)));
    }

    [Fact]
    public void Csv_OfAnEmptyVault_IsTheHeaderLine()
    {
        Assert.Equal("name,url,username,password,note\r\n", Text(VaultExporter.ToCsv([])));
    }

    [Fact]
    public void Csv_IsUtf8_WithoutAByteOrderMark()
    {
        byte[] file = VaultExporter.ToCsv([Item("caf\u00E9 \u65E5\u672C", password: "FAKE-\u00E5")]);

        Assert.Equal((byte)'n', file[0]);
        Assert.Equal("caf\u00E9 \u65E5\u672C", Assert.Single(Parse(file).Entries).Name);
        Assert.Contains("caf\u00E9", Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_ValuesAreWrittenExactlyAsTheyAre()
    {
        VaultItem[] items =
        [
            Item("say \"hi\", twice", "  spaced user ", " FAKE \"pw\",\twith\\everything ", "line one\r\nline two\n\nlast line\r", urls: "https://example.com/?a=1,2&b=\"x\""),
        ];

        Assert.Equal(
            "name,url,username,password,note\r\n" +
            "\"say \"\"hi\"\", twice\",\"https://example.com/?a=1,2&b=\"\"x\"\"\",\"  spaced user \",\" FAKE \"\"pw\"\",\twith\\everything \",\"line one\r\nline two\n\nlast line\r\"\r\n",
            Text(VaultExporter.ToCsv(items)));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+49 30 000000")]
    [InlineData("-FAKE-starts-with-a-minus")]
    [InlineData("@FAKE-handle")]
    [InlineData("=HYPERLINK(\"https://attacker.example\",\"click\")")]
    [InlineData("\t=FAKE")]
    [InlineData("'FAKE-starts-with-a-quote-mark")]
    public void Csv_NothingIsPutInFrontOfAValueThatASpreadsheetWouldTakeForAFormula(string value)
    {
        // The file is for another password manager; a password with something in front of it is another password.
        byte[] file = VaultExporter.ToCsv([Item(value, value, value, value)]);

        ImportEntry entry = Assert.Single(Parse(file).Entries);
        Assert.Equal(value, entry.Username);
        Assert.Equal(value, entry.Password);
        Assert.Equal(value, entry.Notes);
        Assert.Contains("\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_HoldsTheFirstAddressOfEachItem_AndTheNumberWithMoreIsKnownBeforehand()
    {
        VaultItem[] items =
        [
            Item("None"),
            Item("One", urls: "https://one.example"),
            Item("Two", urls: ["https://first.example", "https://second.example"]),
            Item("Three", urls: ["https://a.example", "https://b.example", "https://c.example"]),
        ];

        Assert.Equal(2, VaultExporter.CountItemsWithSeveralUrls(items));
        Assert.Equal(
            [[], ["https://one.example"], ["https://first.example"], ["https://a.example"]],
            Parse(VaultExporter.ToCsv(items)).Entries.Select(e => e.Addresses.ToArray()));
    }

    [Fact]
    public void Csv_IsRecognisedAsChromesLayout_ByThisReader()
    {
        ParsedImport parsed = Parse(VaultExporter.ToCsv([Item("Example", password: "FAKE")]));

        Assert.Equal("Chrome or Edge (CSV)", parsed.Label);
        Assert.Empty(parsed.IgnoredColumns);
    }

    // ---- JSON ----

    [Fact]
    public void Json_IsExactlyThis()
    {
        VaultItem[] items =
        [
            Item("Example", "fake.user@example.test", "FAKE-pw+<1>&'x'", "first line\nsecond \"line\"", favorite: true, "https://example.com/login", "https://example.org")
                with { Totp = new TotpKey { Secret = "GEZDGNBVGY3TQOJQ", Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 } },
            new LoginItem
            {
                Name = "Bare",
                CreatedAt = new DateTimeOffset(2025, 1, 2, 5, 4, 5, TimeSpan.FromHours(2)).AddTicks(1_234_500), // not UTC, and with a fraction
                UpdatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, 678, TimeSpan.Zero),
            },
        ];

        Assert.Equal(
            """
            {
              "format": "bitosx-export",
              "version": 1,
              "exportedAt": "2026-11-10T12:00:00Z",
              "items": [
                {
                  "type": "login",
                  "name": "Example",
                  "username": "fake.user@example.test",
                  "password": "FAKE-pw+<1>&'x'",
                  "urls": [
                    "https://example.com/login",
                    "https://example.org"
                  ],
                  "totp": {
                    "secret": "GEZDGNBVGY3TQOJQ",
                    "algorithm": "SHA256",
                    "digits": 8,
                    "period": 60
                  },
                  "notes": "first line\nsecond \"line\"",
                  "favorite": true,
                  "createdAt": "2025-01-02T03:04:05Z",
                  "updatedAt": "2026-02-03T04:05:06Z"
                },
                {
                  "type": "login",
                  "name": "Bare",
                  "username": "",
                  "password": "",
                  "urls": [],
                  "totp": null,
                  "notes": "",
                  "favorite": false,
                  "createdAt": "2025-01-02T03:04:05.12345Z",
                  "updatedAt": "2025-01-02T03:04:05.678Z"
                }
              ]
            }

            """.ReplaceLineEndings("\n"),
            Text(VaultExporter.ToBitosXJson(items, ExportedAt)));
    }

    [Fact]
    public void CountItemsWithAuthenticatorKeys_CountsLoginsWithAKey()
    {
        var key = new TotpKey { Secret = "GEZDGNBVGY3TQOJQ" };
        Assert.Equal(
            2,
            VaultExporter.CountItemsWithAuthenticatorKeys(
                [Item("a", password: "FAKE") with { Totp = key }, Item("b", password: "FAKE"), Item("c", password: "FAKE") with { Totp = key }]));
    }

    [Fact]
    public void Json_AKey_ReadsBackAsTheSameKey()
    {
        var key = new TotpKey { Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Algorithm = TotpAlgorithm.Sha512, Digits = 7, Period = 300 };
        ParsedImport parsed = Parse(VaultExporter.ToBitosXJson([Item("Example", password: "FAKE") with { Totp = key }], ExportedAt));
        Assert.Equal(key, Assert.Single(parsed.Entries).TotpKey);
        Assert.False(parsed.Entries[0].TotpInvalid);
    }

    [Fact]
    public void Json_OfAnEmptyVault()
    {
        Assert.Equal(
            "{\n  \"format\": \"bitosx-export\",\n  \"version\": 1,\n  \"exportedAt\": \"2026-11-10T12:00:00Z\",\n  \"items\": []\n}\n",
            Text(VaultExporter.ToBitosXJson([], ExportedAt)));
    }

    [Fact]
    public void Json_TheTimeOfTheExport_IsUtc_ToTheSecond()
    {
        var local = new DateTimeOffset(2026, 11, 10, 14, 30, 15, 999, TimeSpan.FromHours(2));

        Assert.Contains("\"exportedAt\": \"2026-11-10T12:30:15Z\"", Text(VaultExporter.ToBitosXJson([], local)), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_IsUtf8_WithoutAByteOrderMark_WithLfLineEnds_WhateverTheSystem()
    {
        byte[] file = VaultExporter.ToBitosXJson([Item("caf\u00E9", notes: "a\r\nb")], ExportedAt);
        string text = Text(file);

        Assert.Equal((byte)'{', file[0]);
        Assert.Equal((byte)'\n', file[^1]);
        Assert.DoesNotContain('\r', text);                                       // a CR in a value is written as an escape
        Assert.Contains("\"notes\": \"a\\r\\nb\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"caf\u00E9\"", text, StringComparison.Ordinal);    // text outside ASCII as it is
    }

    // ---- Both ----

    public static TheoryData<ExportFormat> Formats => new() { ExportFormat.Csv, ExportFormat.BitosXJson };

    private static VaultItem[] ItemsThatAreHardToWrite() =>
    [
        Item("Plain", "fake.user", "FAKE-plain", "a note", urls: "https://example.com"),
        Item("Quotes \"and\" commas, here", "user,\"quoted\"", "FAKE,\"pw\"\"", "note with \"quotes\", commas\nand lines\r\nand a\rcarriage return"),
        Item("Spaces", "  leading and trailing  ", "  FAKE with spaces  ", "  notes  "),
        Item("Backslashes", "DOMAIN\\fake.user", "FAKE\\\\pw\\n", "C:\\dir\\file"),
        Item("Unicode caf\u00E9 \u65E5\u672C \uD83D\uDE00", "anv\u00E4ndare", "FAKE-l\u00F6senord-\uD83D\uDD11", "\u2028line separator and \u00A0no-break space"),
        Item("Control characters", "tab\there", "FAKE\twith\u0001control", "bell\u0007 and del\u007F"),
        Item("Formula", "=1+1", "@FAKE", "+note"),
        Item("Looks like JSON", "{\"a\": 1}", "[\"FAKE\"]", "</script><!--"),
        Item("Empty strings", string.Empty, string.Empty, string.Empty),
        Item("Favorite", "u", "FAKE-fav", favorite: true),
        Item("Long", new string('u', 320), new string('p', 4096), new string('n', 16384), urls: "https://example.com/" + new string('a', 2028)),
    ];

    [Theory]
    [MemberData(nameof(Formats))]
    public void WhatIsWritten_IsReadBack_AsTheSameValues(ExportFormat format)
    {
        VaultItem[] items = ItemsThatAreHardToWrite();

        ImportPlan plan = Plan(Parse(VaultExporter.Export(items, format, ExportedAt)));

        Assert.Equal(items.Length, plan.ToAdd);
        Assert.Empty(plan.NotImported);
        IReadOnlyList<LoginItem> readBack = plan.CreateItems(ExportedAt);
        foreach ((LoginItem written, LoginItem read) in items.Cast<LoginItem>().Zip(readBack))
        {
            Assert.Equal(written.Name, read.Name);
            Assert.Equal(written.Username ?? string.Empty, read.Username ?? string.Empty);
            Assert.Equal(written.Password ?? string.Empty, read.Password ?? string.Empty);
            Assert.Equal(written.Notes ?? string.Empty, read.Notes ?? string.Empty);
            Assert.Equal(written.Urls, read.Urls);
            Assert.Equal(format == ExportFormat.BitosXJson && written.Favorite, read.Favorite); // CSV has no column for it
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ImportingAnExportIntoTheVaultItCameFrom_AddsNothing(ExportFormat format)
    {
        VaultItem[] items = ItemsThatAreHardToWrite();

        ImportPlan plan = Plan(Parse(VaultExporter.Export(items, format, ExportedAt)), ImportOptions.Default, items);

        Assert.Equal(0, plan.ToAdd);
        Assert.Equal(items.Length, plan.Duplicates);
        Assert.Empty(plan.NotImported);
        Assert.Equal(0, plan.EmptyEntries);
        Assert.Empty(plan.CreateItems(ExportedAt));
    }

    [Fact]
    public void Json_KeepsEveryAddress_SoItemsWithSeveralAreDuplicatesToo()
    {
        VaultItem[] items = [Item("Two", "u", "FAKE", urls: ["https://first.example", "https://second.example"])];

        ImportPlan plan = Plan(Parse(VaultExporter.ToBitosXJson(items, ExportedAt)), ImportOptions.Default, items);

        Assert.Equal(1, plan.Duplicates);
        Assert.Equal(0, plan.ToAdd);
    }

    [Fact]
    public void Csv_DoesNot_SoAnItemWithSeveralAddressesIsNotTheItemThatComesBack()
    {
        // What the export dialog warns about: only JSON keeps every address.
        VaultItem[] items = [Item("Two", "u", "FAKE", urls: ["https://first.example", "https://second.example"])];

        ImportPlan plan = Plan(Parse(VaultExporter.ToCsv(items)), ImportOptions.Default, items);

        Assert.Equal(0, plan.Duplicates);
        Assert.Equal(["https://first.example"], Assert.Single(plan.CreateItems(ExportedAt)).Urls);
    }

    [Fact]
    public void AVaultOfTenThousandItems_IsExportedAndReadBack()
    {
        VaultItem[] items = [.. Enumerable.Range(0, 10_000).Select(i => Item("Item " + i, "user" + i, "FAKE-" + i, urls: "https://example.com/" + i))];

        foreach (ExportFormat format in Enum.GetValues<ExportFormat>())
        {
            ImportPlan plan = Plan(Parse(VaultExporter.Export(items, format, ExportedAt)), ImportOptions.Default, items);
            Assert.Equal(10_000, plan.Duplicates);
            Assert.Equal(0, plan.ToAdd);
        }
    }

    [Fact]
    public void HalfOfASurrogatePair_IsWrittenAsTheReplacementCharacter_AsTheVaultItselfStoresIt()
    {
        VaultItem[] items = [Item("Half", password: "FAKE-" + (char)0xD83D)];

        Assert.Equal("FAKE-" + (char)0xFFFD, Assert.Single(Parse(VaultExporter.ToCsv(items)).Entries).Password);
        Assert.Equal("FAKE-" + (char)0xFFFD, Assert.Single(Parse(VaultExporter.ToBitosXJson(items, ExportedAt)).Entries).Password);
    }

    [Theory]
    [InlineData(ExportFormat.Csv, "BitosX-export-2026-11-10.csv")]
    [InlineData(ExportFormat.BitosXJson, "BitosX-export-2026-11-10.json")]
    public void TheSuggestedFileName_HasTheDateAsItIsGiven(ExportFormat format, string name)
    {
        // The caller passes the local time: late in the evening here, already the next day in UTC.
        var localEvening = new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.FromHours(-5));

        Assert.Equal(name, VaultExporter.SuggestedFileName(format, localEvening));
    }

    [Fact]
    public void AFormatThatDoesNotExist_IsAnError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultExporter.Export([], (ExportFormat)7, ExportedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultExporter.SuggestedFileName((ExportFormat)7, ExportedAt));
    }
}
