using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The files under <c>tests/fixtures/import</c> are what this library produces, all of them are
/// used, and together they show the web vault every outcome it has to reproduce.
/// </summary>
public sealed class ConformanceFixtureTests
{
    public static TheoryData<string> ExpectedFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in ConformanceFixtures.ExpectedFiles)
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    private static JsonObject Read(string fileName)
        => JsonNode.Parse(File.ReadAllBytes(Path.Combine(ConformanceFixtures.Directory, fileName)))!.AsObject();

    [Theory]
    [MemberData(nameof(ExpectedFiles))]
    public void AnExpectedFile_IsExactlyWhatTheLibraryProduces(string fileName)
    {
        string path = Path.Combine(ConformanceFixtures.Directory, fileName);
        string committed = Encoding.UTF8.GetString(File.ReadAllBytes(path));

        string produced = ConformanceFixtures.Render(Read(fileName));

        if (ConformanceFixtures.IsUpdating)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(produced));
            return;
        }

        Assert.True(
            string.Equals(committed, produced, StringComparison.Ordinal),
            $"{fileName} is not what the import library produces. If the change is intended, run the tests with " +
            $"{ConformanceFixtures.UpdateVariable}=1 to write the expected files again, look at what changed, and say so in " +
            "docs/IMPORT_EXPORT.md: the web vault follows these files.");
    }

    [Theory]
    [InlineData("bitosx-export.csv", ExportFormat.Csv)]
    [InlineData("bitosx-export.json", ExportFormat.BitosXJson)]
    public void TheExportsAmongTheFixtures_AreWhatTheExporterWrites(string fileName, ExportFormat format)
    {
        string path = Path.Combine(ConformanceFixtures.Directory, fileName);
        byte[] produced = VaultExporter.Export(ConformanceFixtures.ExportedVault, format, ConformanceFixtures.ExportedAt);

        if (ConformanceFixtures.IsUpdating)
        {
            File.WriteAllBytes(path, produced);
            return;
        }

        Assert.True(
            File.ReadAllBytes(path).AsSpan().SequenceEqual(produced),
            $"{fileName} is not what the exporter writes for the fixture vault. Run the tests with {ConformanceFixtures.UpdateVariable}=1 if the change is intended.");
    }

    [Fact]
    public void TheVaultTheExportsCameFrom_IsTheVaultTheirCasesImportInto()
    {
        // The case "the vault it was exported from" has to list that vault, or it shows nothing.
        JsonArray vault = ConformanceFixtures.VaultOf(ConformanceFixtures.ExportedVault);

        foreach (string fileName in new[] { "bitosx-export.csv.expected.json", "bitosx-export.json.expected.json" })
        {
            JsonArray cases = Read(fileName)["cases"]!.AsArray();
            Assert.Contains(cases, c => JsonNode.DeepEquals(c!["vault"], vault));
        }
    }

    [Fact]
    public void ImportingAnExportAmongTheFixtures_IntoItsVault_AddsNothing()
    {
        JsonArray vault = ConformanceFixtures.VaultOf(ConformanceFixtures.ExportedVault);
        JsonObject json = Read("bitosx-export.json.expected.json");
        JsonNode intoItsVault = Assert.Single(json["cases"]!.AsArray(), c => JsonNode.DeepEquals(c!["vault"], vault))!;

        Assert.Equal(0, intoItsVault["result"]!["counts"]!["toAdd"]!.GetValue<int>());
        Assert.Equal(ConformanceFixtures.ExportedVault.Count, intoItsVault["result"]!["counts"]!["duplicates"]!.GetValue<int>());
    }

    [Fact]
    public void EveryFileInTheDirectory_IsTheInputOfExactlyOneExpectedFile()
    {
        string[] inputs =
        [
            .. Directory.EnumerateFiles(ConformanceFixtures.Directory)
                .Select(Path.GetFileName)
                .Where(name => !name!.EndsWith(ConformanceFixtures.ExpectedSuffix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)!,
        ];

        string[] named =
        [
            .. ConformanceFixtures.ExpectedFiles
                .Select(path => Read(Path.GetFileName(path))["input"]!["file"]?.GetValue<string>())
                .Where(name => name is not null)
                .Order(StringComparer.Ordinal)!,
        ];

        Assert.Equal(inputs, named);

        // And an expected file is named after its input.
        foreach (string path in ConformanceFixtures.ExpectedFiles)
        {
            string fileName = Path.GetFileName(path);
            if (Read(fileName)["input"]!["file"]?.GetValue<string>() is string input)
            {
                Assert.Equal(input + ConformanceFixtures.ExpectedSuffix, fileName);
            }
        }
    }

    [Fact]
    public void TheFixtures_ShowEveryOutcomeThereIs()
    {
        var formats = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var refusals = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var statuses = new HashSet<string>(StringComparer.Ordinal);
        var extras = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var formatsWithKeys = new HashSet<string>(StringComparer.Ordinal);
        var formatsWithUnreadableKeys = new HashSet<string>(StringComparer.Ordinal);
        var totpRefusals = new HashSet<string>(StringComparer.Ordinal);
        bool copied = false, duplicatesImported = false, intoAVault = false, inTrash = false, empty = false,
            httpsAdded = false, nameDerived = false, ignoredColumns = false, line = false;

        foreach (string path in ConformanceFixtures.ExpectedFiles)
        {
            foreach (JsonNode? testCase in Read(Path.GetFileName(path))["cases"]!.AsArray())
            {
                JsonObject result = testCase!["result"]!.AsObject();
                copied |= testCase["options"]!["copyExtrasToNotes"]!.GetValue<bool>();
                duplicatesImported |= testCase["options"]!["importDuplicates"]!.GetValue<bool>();
                intoAVault |= testCase["vault"]!.AsArray().Count > 0;

                if (result["refused"] is JsonValue refused)
                {
                    refusals.Add(refused.GetValue<string>());
                    line |= result["line"] is not null;
                    continue;
                }

                formats.Add(result["format"]!.GetValue<string>());
                labels.Add(result["label"]!.GetValue<string>());
                inTrash |= result["inTrash"]!.GetValue<int>() > 0;
                empty |= result["counts"]!["empty"]!.GetValue<int>() > 0;
                httpsAdded |= result["counts"]!["httpsAdded"]!.GetValue<int>() > 0;
                nameDerived |= result["counts"]!["namesDerived"]!.GetValue<int>() > 0;
                ignoredColumns |= result["ignoredColumns"]!.AsArray().Count > 0;
                extras.UnionWith(result["extras"]!.AsObject().Select(pair => pair.Key));
                unsupported.UnionWith(result["unsupported"]!.AsObject().Select(pair => pair.Key));
                foreach (JsonNode? candidate in result["candidates"]!.AsArray())
                {
                    statuses.Add(candidate!["status"]!.GetValue<string>());
                    if (candidate["totp"] is JsonObject)
                    {
                        formatsWithKeys.Add(result["format"]!.GetValue<string>());
                    }

                    if (candidate["totpRefused"] is JsonValue totpRefused)
                    {
                        formatsWithUnreadableKeys.Add(result["format"]!.GetValue<string>());
                        totpRefusals.Add(totpRefused.GetValue<string>());
                    }

                    if (candidate["reason"] is JsonValue reason)
                    {
                        reasons.Add(reason.GetValue<string>());
                    }
                }
            }
        }

        Assert.Equal(Enum.GetValues<ImportFormat>().Select(ImportTexts.Code).Order(), formats.Order());
        Assert.Equal(
            new[]
            {
                ImportTexts.LabelCsv, ImportTexts.LabelChromeCsv, ImportTexts.LabelFirefoxCsv, ImportTexts.LabelBitwardenCsv,
                ImportTexts.LabelKeePassXcCsv, ImportTexts.LabelKeePass1Csv, ImportTexts.LabelBitwardenJson,
                ImportTexts.LabelKeePassXml, ImportTexts.LabelBitosXJson,
            }.Order(),
            labels.Order());

        // Every refusal but the one that is about the vault, not the file.
        Assert.Equal(
            Enum.GetValues<ImportRefusal>().Where(r => r != ImportRefusal.VaultItemLimit).Select(ImportTexts.Code).Order(),
            refusals.Order());
        Assert.Equal(Enum.GetValues<ImportReason>().Select(ImportTexts.Code).Order(), reasons.Order());
        Assert.Equal(Enum.GetValues<ImportStatus>().Select(ImportTexts.Code).Order(), statuses.Order());
        Assert.Equal(Enum.GetValues<ImportExtraKind>().Select(ImportTexts.Code).Order(), extras.Order());
        Assert.Equal(Enum.GetValues<ImportUnsupportedKind>().Select(ImportTexts.Code).Order(), unsupported.Order());

        // Authenticator keys are read from every format, and fail to be read from every format that
        // holds them as text; BitosX's own export is never guessed at (totp_invalid instead).
        Assert.Equal(Enum.GetValues<ImportFormat>().Select(ImportTexts.Code).Order(), formatsWithKeys.Order());
        Assert.Equal(["bitwarden_json", "csv", "keepass_xml"], formatsWithUnreadableKeys.Order());
        Assert.Superset(
            new HashSet<string>(["hotp_not_supported", "steam_not_supported", "secret_too_short", "invalid_secret", "missing_secret", "unsupported_algorithm", "unsupported_digits", "unsupported_period"]),
            totpRefusals);

        Assert.True(copied, "no case copies extras into the notes");
        Assert.True(duplicatesImported, "no case imports duplicates");
        Assert.True(intoAVault, "no case imports into a vault that holds something");
        Assert.True(inTrash, "no case has something in the trash");
        Assert.True(empty, "no case has an entry with nothing in it");
        Assert.True(httpsAdded, "no case puts https:// in front of a host");
        Assert.True(nameDerived, "no case derives a name");
        Assert.True(ignoredColumns, "no case has an ignored column");
        Assert.True(line, "no refusal names a line");
    }

    [Fact]
    public void NothingInTheFixtures_LooksLikeARealSecret()
    {
        // Every password, key and field value in them is made up, and says so.
        foreach (string path in Directory.EnumerateFiles(ConformanceFixtures.Directory))
        {
            if (path.EndsWith(ConformanceFixtures.ExpectedSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            string text = Encoding.Latin1.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal);
            Assert.True(
                bytes.Length == 0 || text.Contains("FAKE", StringComparison.OrdinalIgnoreCase) || text.Contains("fake", StringComparison.Ordinal)
                    || name.StartsWith("refused-", StringComparison.Ordinal),
                $"{name} does not say that its content is made up.");
            Assert.DoesNotContain("BEGIN ", text, StringComparison.Ordinal); // no key material of any kind
        }
    }
}
