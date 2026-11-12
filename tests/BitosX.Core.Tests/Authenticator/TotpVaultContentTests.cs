using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;

namespace BitosX.Core.Tests.Authenticator;

/// <summary>
/// The stored form of an authenticator key and the content schema version (docs/TOTP.md §2):
/// a vault without keys is written exactly as before (version 1), one with a key as version 2;
/// 1 and 2 are read, a key in version 1 and every other version are refused, and nothing about a
/// refused document is quoted.
/// </summary>
public sealed class TotpVaultContentTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Id = "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\"";
    private const string Stamps = "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"";
    private const string ValidTotp = "\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA256\",\"Digits\":8,\"Period\":60}";

    private static string Login(string fields)
        => "{\"type\":\"login\"," + Id + ",\"Name\":\"Example\"," + Stamps + (fields.Length > 0 ? "," + fields : string.Empty) + "}";

    private static string Document(int version, string item) => "{\"schemaVersion\":" + version + ",\"items\":[" + item + "]}";

    private static Vault Load(string json) => VaultSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static LoginItem Fixed(TotpKey? key) => new()
    {
        Id = Guid.Parse("5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11"),
        Name = "Example",
        Username = "user",
        Password = "pw",
        Urls = ["https://example.com/"],
        CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
        Totp = key,
    };

    [Fact]
    public void Write_WithoutKeys_IsVersion1ExactlyAsBefore()
    {
        var vault = new Vault();
        vault.Add(Fixed(null));

        // The bytes 0.14.1 wrote for this vault.
        Assert.Equal(
            "{\"schemaVersion\":1,\"items\":[{\"type\":\"login\",\"Username\":\"user\",\"Password\":\"pw\",\"Urls\":[\"https://example.com/\"],"
            + "\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\",\"Name\":\"Example\",\"Favorite\":false,"
            + "\"CreatedAt\":\"2026-01-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-02T00:00:00+00:00\"}]}",
            Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault)));
        Assert.Equal(1, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void Write_WithAKey_IsVersion2WithTheStoredForm()
    {
        var vault = new Vault();
        vault.Add(Fixed(new TotpKey { Secret = Secret, Algorithm = TotpAlgorithm.Sha512, Digits = 7, Period = 45 }));

        string json = Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault));
        Assert.StartsWith("{\"schemaVersion\":2,", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"Urls\":[\"https://example.com/\"],\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA512\",\"Digits\":7,\"Period\":45},",
            json, StringComparison.Ordinal);
        Assert.Equal(2, vault.RequiredSchemaVersion);
    }

    [Fact]
    public void RoundTrip_KeepsTheKey()
    {
        var key = new TotpKey { Secret = Secret, Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 };
        var vault = new Vault();
        vault.Add(Fixed(key));
        vault.Add(new LoginItem { Name = "Without" });

        Vault restored = VaultSerializer.Deserialize(VaultSerializer.SerializeToUtf8Bytes(vault));
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(key, ((LoginItem)restored.Items[0]).Totp);
        Assert.Null(((LoginItem)restored.Items[1]).Totp);
    }

    [Fact]
    public void Read_Version2WithoutKeys_IsWrittenBackAsVersion1()
    {
        Vault vault = Load(Document(2, Login(string.Empty)));
        Assert.Equal(2, vault.SchemaVersion);
        Assert.StartsWith("{\"schemaVersion\":1,", Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault)), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Version2WithAKey()
    {
        var login = (LoginItem)Assert.Single(Load(Document(2, Login(ValidTotp))).Items);
        Assert.Equal(new TotpKey { Secret = Secret, Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 }, login.Totp);
    }

    [Fact]
    public void RemovingTheLastKey_WritesVersion1Again()
    {
        Vault vault = Load(Document(2, Login(ValidTotp)));
        vault.Update((LoginItem)vault.Items[0] with { Totp = null });
        Assert.StartsWith("{\"schemaVersion\":1,", Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault)), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "a key in version 1", Document(1, Login(ValidTotp)) },
        { "version 3", Document(3, Login(string.Empty)) },
        { "version 0", Document(0, Login(string.Empty)) },
        { "Totp null", Document(2, Login("\"Totp\":null")) },
        { "Totp a string", Document(2, Login("\"Totp\":\"" + Secret + "\"")) },
        { "Totp twice", Document(2, Login(ValidTotp + "," + ValidTotp)) },
        { "missing Secret", Document(2, Login("\"Totp\":{\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "missing Algorithm", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Digits\":6,\"Period\":30}")) },
        { "missing Digits", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Period\":30}")) },
        { "missing Period", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6}")) },
        { "unknown member", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30,\"Issuer\":\"x\"}")) },
        { "member twice", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "member in another case", Document(2, Login("\"Totp\":{\"secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "algorithm in lower case", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"sha1\",\"Digits\":6,\"Period\":30}")) },
        { "algorithm MD5", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"MD5\",\"Digits\":6,\"Period\":30}")) },
        { "algorithm a number", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":0,\"Digits\":6,\"Period\":30}")) },
        { "digits a string", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":\"6\",\"Period\":30}")) },
        { "digits with a fraction", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6.5,\"Period\":30}")) },
        { "digits 5", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":5,\"Period\":30}")) },
        { "digits 9", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":9,\"Period\":30}")) },
        { "period 0", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":0}")) },
        { "period 301", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":301}")) },
        { "secret null", Document(2, Login("\"Totp\":{\"Secret\":null,\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret in lower case", Document(2, Login("\"Totp\":{\"Secret\":\"" + Secret.ToLowerInvariant() + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret with padding", Document(2, Login("\"Totp\":{\"Secret\":\"GEZDGNBVGY3TQOJQGE======\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret with a space", Document(2, Login("\"Totp\":{\"Secret\":\"GEZDGNBV GY3TQOJQ\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret not canonical", Document(2, Login("\"Totp\":{\"Secret\":\"GEZDGNBVGY3TQOJQGF\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret of 9 bytes", Document(2, Login("\"Totp\":{\"Secret\":\"GEZDGNBVGY3TQOI\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret of 129 bytes", Document(2, Login("\"Totp\":{\"Secret\":\"" + new string('A', 207) + "\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
        { "secret empty", Document(2, Login("\"Totp\":{\"Secret\":\"\",\"Algorithm\":\"SHA1\",\"Digits\":6,\"Period\":30}")) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Read_Refused_WithoutQuotingContent(string description, string json)
    {
        _ = description;
        VaultValidationException ex = Assert.Throws<VaultValidationException>(() => Load(json));
        Assert.DoesNotContain("GEZDGNBV", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Example", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeyInVersion1_IsTheGenericInvalidItemRefusal()
        => Assert.Equal(
            "The vault contains an invalid item.",
            Assert.Throws<VaultValidationException>(() => Load(Document(1, Login(ValidTotp)))).Message);

    [Fact]
    public void Add_InvalidKey_IsRefusedWithoutQuotingIt()
    {
        var vault = new Vault();
        var item = new LoginItem { Name = "x", Totp = new TotpKey { Secret = "gezdgnbvgy3tqojq", Digits = 6, Period = 30 } };
        VaultValidationException ex = Assert.Throws<VaultValidationException>(() => vault.Add(item));
        Assert.DoesNotContain("gezdgnbv", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vault.Items);
    }

    [Theory]
    [InlineData(5, 30, TotpAlgorithm.Sha1)]
    [InlineData(9, 30, TotpAlgorithm.Sha1)]
    [InlineData(6, 0, TotpAlgorithm.Sha1)]
    [InlineData(6, 301, TotpAlgorithm.Sha1)]
    [InlineData(6, 30, (TotpAlgorithm)3)]
    public void Update_InvalidParameters_AreRefused(int digits, int period, TotpAlgorithm algorithm)
    {
        var vault = new Vault();
        var item = new LoginItem { Name = "x" };
        vault.Add(item);
        Assert.Throws<VaultValidationException>(
            () => vault.Update(item with { Totp = new TotpKey { Secret = Secret, Digits = digits, Period = period, Algorithm = algorithm } }));
    }
}
