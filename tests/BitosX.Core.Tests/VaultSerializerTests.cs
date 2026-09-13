using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;

namespace BitosX.Core.Tests;

public sealed class VaultSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesItems()
    {
        var vault = new Vault();
        var login = new LoginItem
        {
            Name = "Example",
            Username = "user@example.com",
            Password = "s3cr3t",
            Urls = ["https://example.com", "https://login.example.com"],
            Notes = "note",
            Favorite = true,
        };
        vault.Add(login);

        byte[] bytes = VaultSerializer.SerializeToUtf8Bytes(vault);
        Vault restored = VaultSerializer.Deserialize(bytes);

        Assert.Single(restored.Items);
        var restoredLogin = Assert.IsType<LoginItem>(restored.Items[0]);
        Assert.Equal(login.Id, restoredLogin.Id);
        Assert.Equal(login.Name, restoredLogin.Name);
        Assert.Equal(login.Username, restoredLogin.Username);
        Assert.Equal(login.Password, restoredLogin.Password);
        Assert.Equal(login.Urls, restoredLogin.Urls);
        Assert.True(restoredLogin.Favorite);
    }

    [Fact]
    public void Serialize_PolymorphicDiscriminator_IsPresent()
    {
        var vault = new Vault();
        vault.Add(new LoginItem { Name = "Example" });

        string json = Encoding.UTF8.GetString(VaultSerializer.SerializeToUtf8Bytes(vault));
        Assert.Contains("\"type\":\"login\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.Throws<VaultValidationException>(
            () => VaultSerializer.Deserialize(Encoding.UTF8.GetBytes("this is not json")));
    }

    [Fact]
    public void Deserialize_UnsupportedSchemaVersion_Throws()
    {
        string json = "{\"schemaVersion\":999,\"items\":[]}";
        Assert.Throws<VaultValidationException>(() => VaultSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
