using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The conformance fixtures of the import: <c>tests/fixtures/import</c>. Each
/// <c>&lt;name&gt;.expected.json</c> names an input and one or more cases (the choices, and the
/// vault to import into), and holds what this library makes of them. The web vault copies the
/// directory and has to arrive at the same results; docs/IMPORT_EXPORT.md describes the files.
/// </summary>
/// <remarks>
/// The expected files are written by this class and by nothing else. To write them again after an
/// intended change, run the tests with <c>BITOSX_UPDATE_IMPORT_FIXTURES=1</c>; without it, a file
/// that differs from what the library produces fails ConformanceFixtureTests. A new fixture starts
/// as an expected file with its <c>input</c> and its <c>cases</c> and no results.
/// </remarks>
internal static class ConformanceFixtures
{
    public const string UpdateVariable = "BITOSX_UPDATE_IMPORT_FIXTURES";
    public const string ExpectedSuffix = ".expected.json";
    public const int Schema = 1;

    /// <summary>The time of the two exports among the fixtures.</summary>
    public static readonly DateTimeOffset ExportedAt = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool IsUpdating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>The directory of the fixtures in the source tree.</summary>
    public static string Directory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "tests", "fixtures", "import");
                if (System.IO.Directory.Exists(path))
                {
                    return path;
                }
            }

            throw new DirectoryNotFoundException("tests/fixtures/import was not found above the test's directory.");
        }
    }

    public static IEnumerable<string> ExpectedFiles
        => System.IO.Directory.EnumerateFiles(Directory, "*" + ExpectedSuffix).Order(StringComparer.Ordinal);

    /// <summary>
    /// The vault the two exports among the fixtures are exports of. Changing it changes
    /// <c>bitosx-export.csv</c> and <c>bitosx-export.json</c>, which then have to be written again.
    /// </summary>
    public static IReadOnlyList<VaultItem> ExportedVault { get; } =
    [
        Item("Example Mail", "fake.user@example.test", "FAKE-mail-pw-1", "The address book is shared.", favorite: true, created: 0, "https://mail.example.com/login")
            with { Totp = new TotpKey { Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Algorithm = TotpAlgorithm.Sha1, Digits = 6, Period = 30 } },
        Item("Two addresses", "fake.two", "FAKE-two-pw", null, favorite: false, created: 1, "https://first.example.org/", "https://second.example.org/sign-in?next=%2Fhome")
            with { Totp = new TotpKey { Secret = "JBSWY3DPEHPK3PXP", Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 } },
        Item("Quotes \"and\", commas", "fake,\"user\"", "FAKE \"pw\", with a comma", "line one\nline two\r\nline three, with a \"quote\"", favorite: false, created: 2, "https://example.net/?ids=1,2,3"),
        Item("Caf\u00E9 r\u00E4ksm\u00F6rg\u00E5s \u65E5\u672C", "anv\u00E4ndare@example.test", "FAKE-l\u00F6senord-\uD83D\uDD11", "Notes with an emoji \uD83D\uDE00 and a no-break\u00A0space.", favorite: false, created: 3, "https://xn--bcher-kva.example/"),
        Item("Only a name", null, null, null, favorite: false, created: 4),
        Item("Spaces kept", "  fake spaced  ", "  FAKE spaced pw  ", "  notes with spaces around them  ", favorite: false, created: 5, "https://example.com/spaces"),
        Item("=SUM(1,2)", "=fake", "+FAKE-@formula", "@note -x", favorite: true, created: 6, "https://example.com/formula"),
        Item("Backslashes", "DOMAIN\\fake.user", "FAKE\\pw\\\\two", "C:\\Users\\fake\\file.txt", favorite: false, created: 7),
    ];

    private static LoginItem Item(string name, string? username, string? password, string? notes, bool favorite, int created, params string[] urls)
        => new()
        {
            Id = new Guid(0x0B1705E0, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(created + 1)),
            Name = name,
            Username = username,
            Password = password,
            Notes = notes,
            Favorite = favorite,
            Urls = urls,
            CreatedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero).AddDays(created).AddTicks(created * 1_234_567L),
            UpdatedAt = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero).AddDays(created),
        };

    /// <summary>The bytes an expected file's <c>input</c> stands for: a file next to it, or parts that are repeated.</summary>
    public static byte[] InputBytes(JsonObject input)
    {
        if (input["file"] is JsonValue file)
        {
            return File.ReadAllBytes(Path.Combine(Directory, file.GetValue<string>()));
        }

        // An input too large or too dull to keep as a file: pieces of text, each repeated.
        var text = new StringBuilder();
        foreach (JsonNode? part in input["parts"]!.AsArray())
        {
            string piece = part!["text"]!.GetValue<string>();
            int repeat = part["repeat"]?.GetValue<int>() ?? 1;
            text.Insert(text.Length, piece, repeat);
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>
    /// The expected file as this library fills it in: <paramref name="current"/> for its input and
    /// its cases, and the results computed anew.
    /// </summary>
    public static string Render(JsonObject current)
    {
        JsonObject input = current["input"]!.AsObject();
        byte[] bytes = InputBytes(input);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WritePropertyName("input");
            input.WriteTo(writer);

            writer.WriteStartArray("cases");
            foreach (JsonNode? node in current["cases"]!.AsArray())
            {
                JsonObject testCase = node!.AsObject();
                JsonObject options = testCase["options"]?.AsObject() ?? [];
                JsonArray vault = testCase["vault"]?.AsArray() ?? [];
                var importOptions = new ImportOptions(
                    CopyExtrasToNotes: options["copyExtrasToNotes"]?.GetValue<bool>() ?? false,
                    ImportDuplicates: options["importDuplicates"]?.GetValue<bool>() ?? false);

                writer.WriteStartObject();
                writer.WriteString("name", testCase["name"]!.GetValue<string>());
                writer.WriteStartObject("options");
                writer.WriteBoolean("copyExtrasToNotes", importOptions.CopyExtrasToNotes);
                writer.WriteBoolean("importDuplicates", importOptions.ImportDuplicates);
                writer.WriteEndObject();
                writer.WritePropertyName("vault");
                vault.WriteTo(writer);
                writer.WritePropertyName("result");
                WriteResult(writer, bytes, [.. vault.Select(item => VaultItemOf(item!.AsObject()))], importOptions);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The items of a vault as a case lists them: the four values a duplicate is told by.</summary>
    public static JsonArray VaultOf(IEnumerable<VaultItem> items)
    {
        var vault = new JsonArray();
        foreach (LoginItem item in items.Cast<LoginItem>())
        {
            vault.Add(new JsonObject
            {
                ["name"] = item.Name,
                ["username"] = item.Username ?? string.Empty,
                ["password"] = item.Password ?? string.Empty,
                ["urls"] = new JsonArray([.. item.Urls.Select(url => (JsonNode)url)]),
            });
        }

        return vault;
    }

    private static LoginItem VaultItemOf(JsonObject item) => new()
    {
        Name = item["name"]!.GetValue<string>(),
        Username = item["username"]!.GetValue<string>(),
        Password = item["password"]!.GetValue<string>(),
        Urls = [.. item["urls"]!.AsArray().Select(url => url!.GetValue<string>())],
    };

    private static void WriteResult(Utf8JsonWriter writer, byte[] file, VaultItem[] vault, ImportOptions options)
    {
        writer.WriteStartObject();

        ParsedImport parsed;
        try
        {
            parsed = ImportParser.Parse(file);
        }
        catch (ImportRefusedException refused)
        {
            writer.WriteString("refused", refused.Code);
            if (refused.Line is int line)
            {
                writer.WriteNumber("line", line);
            }

            writer.WriteEndObject();
            return;
        }

        ImportPlan plan = ImportPlanner.Plan(parsed, vault, options);

        writer.WriteString("format", ImportTexts.Code(parsed.Format));
        writer.WriteString("label", parsed.Label);
        writer.WriteNumber("records", parsed.RecordCount);

        writer.WriteStartObject("unsupported");
        foreach (ImportUnsupportedKind kind in Enum.GetValues<ImportUnsupportedKind>())
        {
            if (parsed.Unsupported.TryGetValue(kind, out int count))
            {
                writer.WriteNumber(ImportTexts.Code(kind), count);
            }
        }

        writer.WriteEndObject();
        writer.WriteNumber("inTrash", parsed.InTrash);
        writer.WriteStartArray("ignoredColumns");
        foreach (string column in parsed.IgnoredColumns)
        {
            writer.WriteStringValue(column);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("counts");
        writer.WriteNumber("toAdd", plan.ToAdd);
        writer.WriteNumber("duplicates", plan.Duplicates);
        writer.WriteNumber("notImported", plan.NotImported.Count);
        writer.WriteNumber("empty", plan.EmptyEntries);
        writer.WriteNumber("httpsAdded", plan.HttpsAdded);
        writer.WriteNumber("namesDerived", plan.NamesDerived);
        writer.WriteNumber("totpKeys", plan.TotpKeys);
        writer.WriteNumber("totpUnreadable", plan.TotpUnreadable);
        writer.WriteNumber("vaultItemsBefore", plan.VaultItemsBefore);
        writer.WriteNumber("vaultItemsAfter", plan.VaultItemsAfter);
        writer.WriteBoolean("exceedsVaultItemLimit", plan.ExceedsVaultItemLimit);
        writer.WriteEndObject();

        writer.WriteStartObject("extras");
        foreach (ImportExtraKind kind in Enum.GetValues<ImportExtraKind>())
        {
            if (plan.Extras.TryGetValue(kind, out int count))
            {
                writer.WriteNumber(ImportTexts.Code(kind), count);
            }
        }

        writer.WriteEndObject();

        writer.WriteStartArray("candidates");
        foreach (ImportCandidate candidate in plan.Candidates)
        {
            writer.WriteStartObject();
            writer.WriteNumber("position", candidate.Position);
            writer.WriteString("status", ImportTexts.Code(candidate.Status));
            writer.WriteBoolean("duplicate", candidate.IsDuplicate);
            if (candidate.Reason is ImportReason reason)
            {
                writer.WriteString("reason", ImportTexts.Code(reason));
            }
            else
            {
                writer.WriteNull("reason");
            }

            writer.WriteString("name", candidate.Name);
            writer.WriteString("username", candidate.Username);
            writer.WriteString("password", candidate.Password);
            writer.WriteStartArray("urls");
            foreach (string url in candidate.Urls)
            {
                writer.WriteStringValue(url);
            }

            writer.WriteEndArray();
            writer.WriteString("notes", candidate.Notes);
            writer.WriteBoolean("favorite", candidate.Favorite);
            writer.WriteBoolean("nameWasDerived", candidate.NameWasDerived);
            writer.WriteNumber("httpsAdded", candidate.HttpsAdded);
            if (candidate.Totp is { } totp)
            {
                writer.WriteStartObject("totp");
                writer.WriteString("secret", totp.Secret);
                writer.WriteString("algorithm", BitosX.Core.Authenticator.TotpTexts.AlgorithmName(totp.Algorithm));
                writer.WriteNumber("digits", totp.Digits);
                writer.WriteNumber("period", totp.Period);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("totp");
            }

            if (candidate.TotpRefusal is { } totpRefusal)
            {
                writer.WriteString("totpRefused", BitosX.Core.Authenticator.TotpTexts.Code(totpRefusal));
            }
            else
            {
                writer.WriteNull("totpRefused");
            }

            writer.WriteStartObject("extras");
            writer.WriteString("totp", candidate.Extras.Totp);
            writer.WriteString("folder", candidate.Extras.Folder);
            writer.WriteStartArray("otherAddresses");
            foreach (string address in candidate.Extras.OtherAddresses)
            {
                writer.WriteStringValue(address);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("customFields");
            foreach (ImportCustomField field in candidate.Extras.CustomFields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("value", field.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
