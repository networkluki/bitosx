using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Validation;

namespace BitosX.Core.Tests;

/// <summary>
/// Security regression tests (0.5.1): decrypted vault content is validated when it is loaded, not
/// only when items are added or edited. Whatever the document contains, loading either yields a
/// well-formed model or fails closed with <see cref="VaultValidationException"/> — never another
/// exception type, never a model with nulls in it, and never an error that quotes the content.
/// </summary>
public sealed class VaultLoadValidationTests
{
    private const string SecretMarker = "S3CR3T-MARKER-do-not-leak";

    private const string Id = "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\"";
    private const string Stamps = "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"";

    private static string Document(params string[] items) =>
        "{\"schemaVersion\":1,\"items\":[" + string.Join(',', items) + "]}";

    /// <summary>A valid login item; <paramref name="fields"/> are appended after the required ones.</summary>
    private static string LoginJson(string fields = "", string name = "\"Name\":\"Example\"", string id = Id) =>
        "{\"type\":\"login\"," + id + "," + name + "," + Stamps + (fields.Length > 0 ? "," + fields : string.Empty) + "}";

    private static Vault Load(string json) => VaultSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Load_ValidDocument_Succeeds()
    {
        Vault vault = Load(Document(LoginJson(
            "\"Username\":\"user\",\"Password\":\"pw\",\"Urls\":[\"https://example.com\"],\"Notes\":\"n\",\"Favorite\":true")));

        var login = Assert.IsType<LoginItem>(Assert.Single(vault.Items));
        Assert.Equal("Example", login.Name);
        Assert.Equal("user", login.Username);
        Assert.Equal("pw", login.Password);
        Assert.Equal(["https://example.com"], login.Urls);
        Assert.True(login.Favorite);
    }

    [Fact]
    public void Load_DocumentWithoutItems_IsAnEmptyVault()
        => Assert.Empty(Load("{\"schemaVersion\":1}").Items);

    public static TheoryData<string, string> InvalidDocuments => new()
    {
        { "null item", Document("null") },
        { "item without a type discriminator", Document("{" + Id + ",\"Name\":\"Example\"}") },
        { "type discriminator not first", Document("{" + Id + ",\"Name\":\"Example\",\"type\":\"login\"}") },
        { "unknown item type", Document("{\"type\":\"card\"," + Id + ",\"Name\":\"Example\"}") },
        { "null URL list", Document(LoginJson("\"Urls\":null")) },
        { "null URL entry", Document(LoginJson("\"Urls\":[null]")) },
        { "blank URL entry", Document(LoginJson("\"Urls\":[\"  \"]")) },
        { "javascript URL", Document(LoginJson("\"Urls\":[\"javascript:alert(1)\"]")) },
        { "file URL", Document(LoginJson("\"Urls\":[\"file:///etc/passwd\"]")) },
        { "relative URL", Document(LoginJson("\"Urls\":[\"/login\"]")) },
        { "null name", Document(LoginJson(name: "\"Name\":null")) },
        { "blank name", Document(LoginJson(name: "\"Name\":\"   \"")) },
        { "missing name", Document("{\"type\":\"login\"," + Id + "," + Stamps + "}") },
        { "empty id", Document(LoginJson(id: "\"Id\":\"00000000-0000-0000-0000-000000000000\"")) },
        { "malformed id", Document(LoginJson(id: "\"Id\":\"not-a-guid\"")) },
        { "duplicate ids", Document(LoginJson(), LoginJson(name: "\"Name\":\"Other\"")) },
        { "duplicate property", Document(LoginJson("\"Password\":\"first\",\"Password\":\"second\"")) },
        { "duplicate document property", "{\"schemaVersion\":1,\"schemaVersion\":1,\"items\":[]}" },
        { "unknown item property", Document(LoginJson("\"Totp\":\"JBSWY3DPEHPK3PXP\"")) },
        { "unknown document property", "{\"schemaVersion\":1,\"items\":[],\"owner\":\"x\"}" },
        { "wrongly cased property", Document(LoginJson("\"password\":\"pw\"")) },
        { "name too long", Document(LoginJson(name: "\"Name\":\"" + new string('n', VaultValidation.MaxNameLength + 1) + "\"")) },
        { "username too long", Document(LoginJson("\"Username\":\"" + new string('u', VaultValidation.MaxUsernameLength + 1) + "\"")) },
        { "password too long", Document(LoginJson("\"Password\":\"" + new string('p', VaultValidation.MaxPasswordLength + 1) + "\"")) },
        { "notes too long", Document(LoginJson("\"Notes\":\"" + new string('x', VaultValidation.MaxNotesLength + 1) + "\"")) },
        { "too many URLs", Document(LoginJson("\"Urls\":[" + string.Join(',', Enumerable.Repeat("\"https://example.com\"", VaultValidation.MaxUrlCount + 1)) + "]")) },
        // Version 2 (authenticator keys, 0.15.0) is read since then; 3 is the first this build does not know.
        { "unsupported schema version", "{\"schemaVersion\":3,\"items\":[]}" },
        { "schema version 0", "{\"schemaVersion\":0,\"items\":[]}" },
        { "missing schema version", "{\"items\":[]}" },
        { "items is not a list", "{\"schemaVersion\":1,\"items\":{}}" },
        { "not an object", "[]" },
        { "json null", "null" },
        { "empty", "" },
        { "truncated", "{\"schemaVersion\":1,\"items\":[{\"type\":\"login\"," },
    };

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Load_InvalidDocument_FailsClosed_WithValidationException(string description, string json)
    {
        Exception? failure = Record.Exception(() => Load(json));

        Assert.True(failure is VaultValidationException, $"{description}: expected VaultValidationException, got {failure?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [InlineData("\"Urls\":[\"javascript:" + SecretMarker + "\"]")]
    [InlineData("\"Password\":\"" + SecretMarker + "\",\"Urls\":null")]
    [InlineData("\"Notes\":\"" + SecretMarker + "\",\"Bogus\":1")]
    [InlineData("\"Password\":\"" + SecretMarker + "\",\"Password\":\"" + SecretMarker + "\"")]
    [InlineData("\"Username\":\"" + SecretMarker + "\",\"Favorite\":\"not-a-bool\"")]
    public void Load_Failure_NeverQuotesTheDecryptedContent(string fields)
    {
        var failure = Assert.Throws<VaultValidationException>(() => Load(Document(LoginJson(fields))));

        Assert.DoesNotContain(SecretMarker, failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void Load_UpdatedBeforeCreated_IsRepaired_NotRejected()
    {
        // Written by earlier versions when the system clock had been moved back. It is harmless
        // metadata and must never be the reason a vault or a backup will not open.
        string inverted = "{\"type\":\"login\"," + Id + ",\"Name\":\"Example\","
            + "\"CreatedAt\":\"2026-06-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-01T00:00:00+00:00\"}";

        VaultItem item = Assert.Single(Load(Document(inverted)).Items);

        Assert.Equal(item.CreatedAt, item.UpdatedAt);
        VaultValidation.Validate(item); // and it can be edited again
    }

    [Fact]
    public void Load_RepairedItem_SurvivesASaveAndReload()
    {
        string inverted = "{\"type\":\"login\"," + Id + ",\"Name\":\"Example\","
            + "\"CreatedAt\":\"2026-06-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-01T00:00:00+00:00\"}";

        Vault reloaded = VaultSerializer.Deserialize(VaultSerializer.SerializeToUtf8Bytes(Load(Document(inverted))));

        VaultItem item = Assert.Single(reloaded.Items);
        Assert.Equal(item.CreatedAt, item.UpdatedAt);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryItemTheEditorCanProduce()
    {
        var vault = new Vault();
        vault.Add(new LoginItem { Name = "Minimal" });
        vault.Add(new LoginItem
        {
            Name = new string('n', VaultValidation.MaxNameLength),
            Username = new string('u', VaultValidation.MaxUsernameLength),
            Password = new string('p', VaultValidation.MaxPasswordLength),
            Notes = new string('x', VaultValidation.MaxNotesLength),
            Urls = [.. Enumerable.Repeat("https://example.com/path?q=1", VaultValidation.MaxUrlCount)],
            Favorite = true,
        });
        vault.Add(new LoginItem { Name = "Unicode åäö 🔐", Password = "\"quotes\" \\ and \u0000 control", Urls = ["http://insecure.example"] });

        Vault reloaded = VaultSerializer.Deserialize(VaultSerializer.SerializeToUtf8Bytes(vault));

        Assert.Equal(vault.Items, reloaded.Items, new LoginComparer());
    }

    private sealed class LoginComparer : IEqualityComparer<VaultItem>
    {
        public bool Equals(VaultItem? x, VaultItem? y)
            => x is LoginItem a && y is LoginItem b
               && a.Id == b.Id && a.Name == b.Name && a.Username == b.Username && a.Password == b.Password
               && a.Notes == b.Notes && a.Favorite == b.Favorite && a.Urls.SequenceEqual(b.Urls)
               && a.CreatedAt == b.CreatedAt && a.UpdatedAt == b.UpdatedAt;

        public int GetHashCode(VaultItem obj) => obj.Id.GetHashCode();
    }
}
