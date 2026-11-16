using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Services;
using BitosX.Core.Validation;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests.Notes;

/// <summary>
/// The stored form of a secure note and the content schema version (docs/NOTES.md §2): a vault
/// without notes is written exactly as 0.15.0 wrote it (version 1 or 2), one with a note as version 3;
/// 1, 2 and 3 are read, a note in version 1 or 2 and every version from 4 on are refused, and nothing
/// about a refused document is quoted.
/// </summary>
public sealed class NoteVaultContentTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Marker = "S3CR3T-NOTE-do-not-leak";
    private const string Id = "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\"";
    private const string OtherId = "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c12\"";
    private const string Stamps = "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"";
    private const string ValidTotp = "\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}";

    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Updated = DateTimeOffset.Parse("2026-01-02T00:00:00+00:00", CultureInfo.InvariantCulture);

    private static string Note(string fields, string id = Id)
        => "{\"type\":\"note\"," + id + ",\"Name\":\"Example\"" + (fields.Length > 0 ? "," + fields : string.Empty) + "," + Stamps + "}";

    private static string Login(string fields, string id = OtherId)
        => "{\"type\":\"login\"," + id + ",\"Name\":\"Login\"," + Stamps + (fields.Length > 0 ? "," + fields : string.Empty) + "}";

    private static string Document(int version, params string[] items)
        => "{\"schemaVersion\":" + version + ",\"items\":[" + string.Join(',', items) + "]}";

    private static Vault Load(string json) => VaultSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static string Write(Vault vault) => Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault));

    private static NoteItem FixedNote(string? text, bool favorite = false) => new()
    {
        Id = Guid.Parse("5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11"),
        Name = "Example",
        Notes = text,
        Favorite = favorite,
        CreatedAt = Created,
        UpdatedAt = Updated,
    };

    private static LoginItem FixedLogin(TotpKey? key) => new()
    {
        Id = Guid.Parse("5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c12"),
        Name = "Login",
        Username = "user",
        Password = "pw",
        Urls = ["https://example.com/"],
        Notes = "n",
        Favorite = true,
        CreatedAt = Created,
        UpdatedAt = Updated,
        Totp = key,
    };

    // ---- What is written ----------------------------------------------------------------------

    [Fact]
    public void Write_WithoutNotesOrKeys_IsVersion1ByteForByteAs0150()
    {
        var vault = new Vault();
        vault.Add(FixedLogin(null));

        // The bytes 0.15.0 (and 0.14.1) wrote for this vault.
        Assert.Equal(
            "{\"schemaVersion\":1,\"items\":[{\"type\":\"login\",\"Username\":\"user\",\"Password\":\"pw\",\"Urls\":[\"https://example.com/\"],"
            + "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c12\",\"Name\":\"Login\",\"Notes\":\"n\",\"Favorite\":true,"
            + "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"}]}",
            Write(vault));
        Assert.Equal(Vault.SchemaVersionWithoutAuthenticatorKeys, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void Write_WithAKeyAndNoNote_IsVersion2ByteForByteAs0150()
    {
        var vault = new Vault();
        vault.Add(FixedLogin(new TotpKey { Secret = Secret, Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 }));

        // The bytes 0.15.0 wrote for this vault.
        Assert.Equal(
            "{\"schemaVersion\":2,\"items\":[{\"type\":\"login\",\"Username\":\"user\",\"Password\":\"pw\",\"Urls\":[\"https://example.com/\"],"
            + "\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA256\",\"Digits\":8,\"Period\":60},"
            + "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c12\",\"Name\":\"Login\",\"Notes\":\"n\",\"Favorite\":true,"
            + "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"}]}",
            Write(vault));
        Assert.Equal(Vault.SchemaVersionWithAuthenticatorKeys, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void Write_TheWebFixtureOf0150_ComesOutByteForByteAsItWasWritten()
    {
        // tests/fixtures/web/totp.meta.json holds the plaintext 0.15.0 wrote for a vault of four logins.
        JsonObject meta = JsonNode.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory("web"), "totp.meta.json")))!.AsObject();
        string plaintext = meta["plaintextUtf8"]!.GetValue<string>();

        Assert.Equal(plaintext, Write(Load(plaintext)));
    }

    [Fact]
    public void Write_ANote_IsVersion3_WithExactlyTheBaseMembers()
    {
        var vault = new Vault();
        vault.Add(FixedNote("Line one\nLine two", favorite: true));

        Assert.Equal(
            "{\"schemaVersion\":3,\"items\":[{\"type\":\"note\","
            + "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\",\"Name\":\"Example\",\"Notes\":\"Line one\\nLine two\",\"Favorite\":true,"
            + "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"}]}",
            Write(vault));
        Assert.Equal(Vault.SchemaVersionWithNotes, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void Write_ANoteWithoutText_HasNoNotesMember()
    {
        var vault = new Vault();
        vault.Add(FixedNote(null));

        Assert.Equal(
            "{\"schemaVersion\":3,\"items\":[{\"type\":\"note\","
            + "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\",\"Name\":\"Example\",\"Favorite\":false,"
            + "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"}]}",
            Write(vault));
    }

    [Fact]
    public void Write_ANoteAndAKey_IsVersion3()
    {
        var vault = new Vault();
        vault.Add(FixedLogin(new TotpKey { Secret = Secret }));
        vault.Add(FixedNote("text"));

        Assert.StartsWith("{\"schemaVersion\":3,", Write(vault), StringComparison.Ordinal);
        Assert.Equal(3, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void RemovingTheLastNote_WritesTheLowerVersionAgain()
    {
        Vault vault = Load(Document(3, Note("\"Notes\":\"x\""), Login(ValidTotp)));
        Assert.True(vault.Remove(Guid.Parse("5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11")));
        Assert.StartsWith("{\"schemaVersion\":2,", Write(vault), StringComparison.Ordinal);

        vault.Update((LoginItem)vault.Items[0] with { Totp = null });
        Assert.StartsWith("{\"schemaVersion\":1,", Write(vault), StringComparison.Ordinal);
    }

    // ---- What is read -------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_KeepsEveryNote()
    {
        string text = "Zeile 1 äöü\r\n<b>not markup</b> & \"quotes\"\n\t\U0001F511";
        var vault = new Vault();
        vault.Add(FixedNote(text, favorite: true));
        vault.Add(new NoteItem { Name = "Only a name" });
        vault.Add(FixedLogin(new TotpKey { Secret = Secret }));

        Vault restored = VaultSerializer.Deserialize(VaultSerializer.SerializeToUtf8Bytes(vault));

        Assert.Equal(3, restored.SchemaVersion);
        Assert.Equal(vault.Items[0], restored.Items[0]);
        Assert.Equal(vault.Items[1], restored.Items[1]);
        NoteItem first = Assert.IsType<NoteItem>(restored.Items[0]);
        Assert.Equal(text, first.Notes);
        Assert.True(first.Favorite);
        Assert.Equal(VaultItemType.Note, first.ItemType);
        Assert.Null(Assert.IsType<NoteItem>(restored.Items[1]).Notes);
        Assert.Equal(new TotpKey { Secret = Secret }, Assert.IsType<LoginItem>(restored.Items[2]).Totp);
    }

    [Fact]
    public void Read_Version3_WithANote()
    {
        NoteItem note = Assert.IsType<NoteItem>(Assert.Single(Load(Document(3, Note("\"Notes\":\"t\",\"Favorite\":true"))).Items));
        Assert.Equal("Example", note.Name);
        Assert.Equal("t", note.Notes);
        Assert.True(note.Favorite);
        Assert.Equal(Created, note.CreatedAt);
        Assert.Equal(Updated, note.UpdatedAt);
    }

    [Fact]
    public void Read_ANoteWithAnEmptyText_OrWithoutOne()
    {
        Assert.Equal(string.Empty, ((NoteItem)Load(Document(3, Note("\"Notes\":\"\""))).Items[0]).Notes);
        Assert.Null(((NoteItem)Load(Document(3, Note(string.Empty))).Items[0]).Notes);
        Assert.Null(((NoteItem)Load(Document(3, Note("\"Notes\":null"))).Items[0]).Notes);
    }

    [Fact]
    public void Read_ANoteAtTheLongestText()
    {
        string text = new('x', VaultValidation.MaxNotesLength);
        Assert.Equal(text, ((NoteItem)Load(Document(3, Note("\"Notes\":\"" + text + "\""))).Items[0]).Notes);
    }

    [Fact]
    public void Read_Version3_WithAKeyAndWithoutNotes()
    {
        Vault vault = Load(Document(3, Login(ValidTotp)));
        Assert.Equal(3, vault.SchemaVersion);
        Assert.NotNull(((LoginItem)vault.Items[0]).Totp);
        Assert.StartsWith("{\"schemaVersion\":2,", Write(vault), StringComparison.Ordinal);

        Vault plain = Load(Document(3, Login(string.Empty)));
        Assert.StartsWith("{\"schemaVersion\":1,", Write(plain), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TheOrderOfTheMembers_IsNotPartOfTheFormat()
        => Assert.IsType<NoteItem>(Assert.Single(Load(Document(3,
            "{\"type\":\"note\"," + Stamps + ",\"Favorite\":false,\"Notes\":\"t\",\"Name\":\"Example\"," + Id + "}")).Items));

    public static TheoryData<string, string> Refused => new()
    {
        { "a note in version 1", Document(1, Note("\"Notes\":\"" + Marker + "\"")) },
        { "a note in version 2", Document(2, Note("\"Notes\":\"" + Marker + "\"")) },
        { "a note in version 2 next to a key", Document(2, Login(ValidTotp), Note("\"Notes\":\"" + Marker + "\"")) },
        { "version 4", Document(4, Note("\"Notes\":\"" + Marker + "\"")) },
        { "version 4 without items", "{\"schemaVersion\":4,\"items\":[]}" },
        { "a note with Username", Document(3, Note("\"Notes\":\"" + Marker + "\",\"Username\":\"u\"")) },
        { "a note with Password", Document(3, Note("\"Notes\":\"" + Marker + "\",\"Password\":\"p\"")) },
        { "a note with Urls", Document(3, Note("\"Notes\":\"" + Marker + "\",\"Urls\":[]")) },
        { "a note with Totp", Document(3, Note("\"Notes\":\"" + Marker + "\"," + ValidTotp)) },
        { "a note with an unknown member", Document(3, Note("\"Notes\":\"" + Marker + "\",\"Text\":\"t\"")) },
        { "a note with a member in another case", Document(3, Note("\"notes\":\"" + Marker + "\"")) },
        { "a note with Notes twice", Document(3, Note("\"Notes\":\"" + Marker + "\",\"Notes\":\"other\"")) },
        { "a note with a text too long", Document(3, Note("\"Notes\":\"" + Marker + new string('x', VaultValidation.MaxNotesLength) + "\"")) },
        { "a note without a name", Document(3, "{\"type\":\"note\"," + Id + ",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
        { "a note with a blank name", Document(3, "{\"type\":\"note\"," + Id + ",\"Name\":\" \",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
        { "a note with a name too long", Document(3, "{\"type\":\"note\"," + Id + ",\"Name\":\"" + new string('n', 201) + "\",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
        { "a note with a text that is a number", Document(3, Note("\"Notes\":42")) },
        { "a note and a login with one id", Document(3, Note("\"Notes\":\"" + Marker + "\""), Login(string.Empty, Id)) },
        { "the type in another case", Document(3, "{\"type\":\"Note\"," + Id + ",\"Name\":\"Example\",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
        { "the type secure_note", Document(3, "{\"type\":\"secure_note\"," + Id + ",\"Name\":\"Example\",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
        { "the type not first", Document(3, "{" + Id + ",\"type\":\"note\",\"Name\":\"Example\",\"Notes\":\"" + Marker + "\"," + Stamps + "}") },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Read_Refused_WithoutQuotingContent(string description, string json)
    {
        _ = description;
        VaultValidationException ex = Assert.Throws<VaultValidationException>(() => Load(json));
        Assert.DoesNotContain(Marker, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Example", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Read_ANoteInAnOlderVersion_IsTheGenericInvalidItemRefusal(int version)
        => Assert.Equal(
            "The vault contains an invalid item.",
            Assert.Throws<VaultValidationException>(() => Load(Document(version, Note("\"Notes\":\"" + Marker + "\"")))).Message);

    [Fact]
    public void Read_Version4_IsTheUnsupportedVersionRefusal()
        => Assert.Equal(
            "Unsupported vault schema version 4.",
            Assert.Throws<VaultValidationException>(() => Load("{\"schemaVersion\":4,\"items\":[]}")).Message);

    // ---- Validation on add and update -----------------------------------------------------------

    [Fact]
    public void Add_ANoteWithOnlyAName_IsAllowed()
    {
        var vault = new Vault();
        vault.Add(new NoteItem { Name = "Only a name" });
        vault.Add(new NoteItem { Name = "Empty text", Notes = string.Empty });
        Assert.Equal(2, vault.Items.Count);
    }

    [Fact]
    public void Add_ANoteThatBreaksALimit_IsRefusedWithoutQuotingIt()
    {
        var vault = new Vault();
        VaultValidationException tooLong = Assert.Throws<VaultValidationException>(
            () => vault.Add(new NoteItem { Name = "x", Notes = Marker + new string('x', VaultValidation.MaxNotesLength) }));
        Assert.DoesNotContain(Marker, tooLong.Message, StringComparison.Ordinal);

        Assert.Throws<VaultValidationException>(() => vault.Add(new NoteItem { Name = " ", Notes = Marker }));
        Assert.Throws<VaultValidationException>(() => vault.Add(new NoteItem { Name = new string('n', 201), Notes = Marker }));
        Assert.Throws<VaultValidationException>(() => vault.Add(new NoteItem { Id = Guid.Empty, Name = "x" }));
        Assert.Empty(vault.Items);
    }

    [Fact]
    public void Update_ANote_StampsItAndKeepsItANote()
    {
        var vault = new Vault();
        NoteItem note = new() { Name = "n", Notes = "old", CreatedAt = Created, UpdatedAt = Created };
        vault.Add(note);

        VaultItem stored = vault.Update(note with { Notes = "new" });

        NoteItem updated = Assert.IsType<NoteItem>(stored);
        Assert.Equal("new", updated.Notes);
        Assert.True(updated.UpdatedAt > Created);
        Assert.Throws<VaultValidationException>(() => vault.Update(note with { Notes = new string('x', VaultValidation.MaxNotesLength + 1) }));
    }

    // ---- The service ----------------------------------------------------------------------------

    [Fact]
    public async Task AddNoteAsync_IsStoredEncrypted_AsVersion3_AndReadBackAfterUnlocking()
    {
        var storage = new InMemoryVaultStorage();
        using var service = new VaultService(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await service.CreateVaultAsync("correct horse battery");

        NoteItem added = await service.AddNoteAsync(new NoteItem { Name = "Wi-Fi", Notes = Marker });
        Assert.Same(added, Assert.Single(service.Items));
        service.Lock();

        using var other = new VaultService(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await other.RefreshStateAsync();
        await other.UnlockAsync("correct horse battery");
        NoteItem restored = Assert.IsType<NoteItem>(Assert.Single(other.Items));
        Assert.Equal(added, restored);

        UnlockResult opened = VaultCryptor.CreateDefault().Unlock(storage.Stored!, Encoding.UTF8.GetBytes("correct horse battery"));
        Assert.StartsWith("{\"schemaVersion\":3,", Encoding.UTF8.GetString(opened.Plaintext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddNoteAsync_AnInvalidNote_WritesNothing()
    {
        var storage = new InMemoryVaultStorage();
        using var service = new VaultService(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await service.CreateVaultAsync("correct horse battery");
        EncryptedVault before = storage.Stored!;

        await Assert.ThrowsAsync<VaultValidationException>(() => service.AddNoteAsync(new NoteItem { Name = string.Empty, Notes = Marker }));

        Assert.Same(before, storage.Stored);
        Assert.Empty(service.Items);
    }

    internal static string FixtureDirectory(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "tests", "fixtures", name);
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException("tests/fixtures/" + name + " was not found above the test's directory.");
    }
}
