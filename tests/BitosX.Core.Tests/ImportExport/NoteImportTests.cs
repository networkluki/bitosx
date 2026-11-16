using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>Secure notes in an import and an export (docs/IMPORT_EXPORT.md §7.7, §8, §11).</summary>
public sealed class NoteImportTests
{
    private const string Header = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n";

    private static ImportCandidate OnlyNote(string row, ImportOptions? options = null)
    {
        ImportCandidate candidate = Only(Header + row + "\n", options);
        Assert.True(candidate.IsNote);
        return candidate;
    }

    [Fact]
    public void ANoteRow_IsANote_WithItsNameTextFavoriteAndExtras_AndNothingOfALogin()
    {
        ImportCandidate note = OnlyNote("Work,1,note,  Wi-Fi  ,\"FAKE text\nline two\",PIN: FAKE-0000,0,https://x.example,fake.user,FAKE-pw,FAKEFAKEFAKEFAKE");

        Assert.Equal("Wi-Fi", note.Name);
        Assert.Equal("FAKE text\nline two", note.Notes);
        Assert.True(note.Favorite);
        Assert.Equal(string.Empty, note.Username);
        Assert.Equal(string.Empty, note.Password);
        Assert.Empty(note.Urls);
        Assert.Null(note.Totp);
        Assert.Null(note.TotpRefusal);
        Assert.Equal("Work", note.Extras.Folder);
        Assert.Equal("PIN", Assert.Single(note.Extras.CustomFields).Name);
        Assert.Equal(ImportStatus.Add, note.Status);
    }

    [Theory]
    [InlineData("\"\n  FAKE first  \nsecond\"", "FAKE first")]
    [InlineData("\"   \"", "Untitled")]
    [InlineData("\"\r\n\r\nFAKE after empty lines\"", "FAKE after empty lines")]
    public void ANoteWithoutAName_IsNamedAfterTheFirstLineOfItsText(string text, string expected)
    {
        string row = ",,note,," + text + ",PIN: FAKE-0000,0,,,,";
        ImportCandidate note = OnlyNote(row);
        Assert.Equal(expected, note.Name);
        Assert.True(note.NameWasDerived);
    }

    [Fact]
    public void ANoteWhoseFirstLineIsTooLong_IsUntitled_TheNextLineIsNotTried()
    {
        ImportCandidate note = OnlyNote(",,note,,\"" + new string('x', 201) + "\nFAKE short\",,0,,,,");
        Assert.Equal(ImportTexts.UntitledName, note.Name);

        ImportCandidate fits = OnlyNote(",,note,,\"" + new string('x', 200) + "\nFAKE short\",,0,,,,");
        Assert.Equal(new string('x', 200), fits.Name);
    }

    [Fact]
    public void ANoteWithNothingButAFolder_IsEmpty()
    {
        ImportPlan plan = Plan(Header + "Work,1,note,,  ,,0,,,,\n");
        Assert.Empty(plan.Candidates);
        Assert.Equal(1, plan.EmptyEntries);
    }

    [Fact]
    public void TheLimitsOfANote_InTheirOrder()
    {
        Assert.Equal(ImportReason.NameTooLong, OnlyNote(",,note," + new string('n', 201) + "," + new string('x', 16385) + ",,0,,,,").Reason);
        Assert.Equal(ImportReason.NotesTooLong, OnlyNote(",,note,FAKE," + new string('x', 16385) + ",,0,,,,").Reason);
        Assert.Null(OnlyNote(",,note,FAKE," + new string('x', 16384) + ",,0,,,,").Reason);
        Assert.Equal(
            ImportReason.NotesTooLongWithExtras,
            OnlyNote("Work,,note,FAKE," + new string('x', 16384) + ",,0,,,,", new ImportOptions(CopyExtrasToNotes: true)).Reason);
    }

    [Fact]
    public void TheExtrasOfANote_AreCopiedIntoItsText_AsALoginsAre()
    {
        ImportCandidate note = OnlyNote("Work,,note,FAKE,FAKE text,\"PIN: FAKE-0000\nnameless\",0,,,,", new ImportOptions(CopyExtrasToNotes: true));
        Assert.Equal("FAKE text\n\nFolder: Work\nPIN: FAKE-0000\nField: nameless", note.Notes);
    }

    [Fact]
    public void ANote_IsADuplicateOfANoteWithTheSameNameAndText_NeverOfALogin()
    {
        var existing = new VaultItem[]
        {
            new NoteItem { Name = "  Same  ", Notes = "FAKE text" },
            new LoginItem { Name = "Login-like", Notes = "FAKE text" },
            new NoteItem { Name = "Empty" },
        };
        ImportPlan plan = Plan(
            Header
            + ",,note,Same,FAKE text,,0,,,,\n"          // the vault's note
            + ",,note,Same,FAKE other text,,0,,,,\n"    // another text
            + ",,note,Login-like,FAKE text,,0,,,,\n"    // a login of that name and text: not a duplicate
            + ",,login,Same,FAKE text,,0,,,,\n"         // a login of the note's name and text: not a duplicate
            + ",,note,Empty,,,0,,,,\n"                  // an absent text equals an empty one
            + ",,note,Same,FAKE other text,,0,,,,\n",   // a duplicate of the second row
            ImportOptions.Default,
            existing);

        Assert.Equal([true, false, false, false, true, true], plan.Candidates.Select(c => c.IsDuplicate));
        Assert.Equal(3, plan.ToAdd);
        Assert.Equal(2, plan.NotesToAdd);
        Assert.Equal(1, plan.LoginsToAdd);
    }

    [Fact]
    public void CreateItems_MakesNotes_WithAnEmptyTextAsNone()
    {
        DateTimeOffset at = new(2026, 11, 15, 1, 2, 3, TimeSpan.Zero);
        ImportPlan plan = Plan(Header + ",1,note,With text,FAKE text,,0,,,,\n,,note,Without text,,,0,,,,\n,,login,A login,,,0,,u,FAKE,\n");

        IReadOnlyList<VaultItem> items = plan.CreateItems(at);

        NoteItem first = Assert.IsType<NoteItem>(items[0]);
        Assert.Equal(("With text", "FAKE text", true, at, at), (first.Name, first.Notes, first.Favorite, first.CreatedAt, first.UpdatedAt));
        Assert.Null(Assert.IsType<NoteItem>(items[1]).Notes);
        Assert.IsType<LoginItem>(items[2]);
        Assert.Equal(3, items.Select(i => i.Id).Distinct().Count());

        var vault = new Vault();
        vault.AddRange(items);
        Assert.Equal(Vault.SchemaVersionWithNotes, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void ABitosXNote_ReadsOnlyItsNameTextAndFavorite_AndABadKeyOnItIsIgnored()
    {
        ImportCandidate note = Assert.Single(Plan(
            "{\"format\":\"bitosx-export\",\"version\":1,\"items\":[{\"type\":\"note\",\"name\":\"FAKE\",\"notes\":\"t\",\"favorite\":true,"
            + "\"username\":\"u\",\"password\":\"p\",\"urls\":[\"https://x.example\"],\"totp\":\"not a key\"}]}").Candidates);

        Assert.True(note.IsNote);
        Assert.Null(note.Reason);
        Assert.Equal(("FAKE", "t", true, string.Empty, string.Empty, 0), (note.Name, note.Notes, note.Favorite, note.Username, note.Password, note.Urls.Count));
    }

    [Fact]
    public void TheJsonExportOfANote_IsExactlyItsSixMembers()
    {
        var note = new NoteItem
        {
            Name = "FAKE note",
            Notes = "line one\nline \"two\"",
            Favorite = true,
            CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1_230_000),
            UpdatedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
        };

        string json = System.Text.Encoding.UTF8.GetString(VaultExporter.ToBitosXJson([note, new NoteItem { Name = "Empty", CreatedAt = note.CreatedAt, UpdatedAt = note.UpdatedAt }], new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            "{\n  \"format\": \"bitosx-export\",\n  \"version\": 1,\n  \"exportedAt\": \"2026-11-15T00:00:00Z\",\n  \"items\": [\n"
            + "    {\n      \"type\": \"note\",\n      \"name\": \"FAKE note\",\n      \"notes\": \"line one\\nline \\\"two\\\"\",\n      \"favorite\": true,\n"
            + "      \"createdAt\": \"2026-01-02T03:04:05.123Z\",\n      \"updatedAt\": \"2026-02-03T04:05:06Z\"\n    },\n"
            + "    {\n      \"type\": \"note\",\n      \"name\": \"Empty\",\n      \"notes\": \"\",\n      \"favorite\": false,\n"
            + "      \"createdAt\": \"2026-01-02T03:04:05.123Z\",\n      \"updatedAt\": \"2026-02-03T04:05:06Z\"\n    }\n  ]\n}\n",
            json);
    }

    [Fact]
    public void TheCsvExport_HasNoNotes_AndTheyAreCounted()
    {
        VaultItem[] items = [new NoteItem { Name = "FAKE note", Notes = "FAKE-secret-text" }, new LoginItem { Name = "L", Password = "FAKE" }, new NoteItem { Name = "Other" }];

        string csv = System.Text.Encoding.UTF8.GetString(VaultExporter.ToCsv(items));

        Assert.Equal("name,url,username,password,note\r\n\"L\",\"\",\"\",\"FAKE\",\"\"\r\n", csv);
        Assert.Equal(2, VaultExporter.CountNotes(items));
        Assert.Equal(0, VaultExporter.CountItemsWithSeveralUrls(items));
        Assert.Equal(0, VaultExporter.CountItemsWithAuthenticatorKeys(items));
    }
}
