using System.Text;
using System.Text.Json.Nodes;
using BitosX.Crypto;

namespace BitosX.Crypto.Tests;

public sealed class EncryptedVaultSerializerTests
{
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    private EncryptedVault SampleVault() =>
        _cryptor.Create(Encoding.UTF8.GetBytes("pw"), Encoding.UTF8.GetBytes("data"), Argon2Parameters.ForTesting).Encrypted;

    [Fact]
    public void SerializeThenDeserialize_RoundTrips()
    {
        EncryptedVault original = SampleVault();

        string json = EncryptedVaultSerializer.Serialize(original);
        EncryptedVault restored = EncryptedVaultSerializer.Deserialize(json);

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Kdf, restored.Kdf);
        Assert.Equal(original.Algorithm, restored.Algorithm);
        Assert.Equal(original.Salt, restored.Salt);
        Assert.Equal(original.KeyWrapNonce, restored.KeyWrapNonce);
        Assert.Equal(original.EncryptedVaultKey, restored.EncryptedVaultKey);
        Assert.Equal(original.ContentNonce, restored.ContentNonce);
        Assert.Equal(original.Ciphertext, restored.Ciphertext);
        Assert.Equal(original.KdfParameters, restored.KdfParameters);
    }

    [Fact]
    public void Deserialize_RestoredVault_CanBeUnlocked()
    {
        string json = EncryptedVaultSerializer.Serialize(SampleVault());
        EncryptedVault restored = EncryptedVaultSerializer.Deserialize(json);

        UnlockResult unlocked = _cryptor.Unlock(restored, Encoding.UTF8.GetBytes("pw"));
        Assert.Equal("data", Encoding.UTF8.GetString(unlocked.Plaintext));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Deserialize_MalformedInput_FailsClosed(string input)
    {
        Assert.ThrowsAny<BitosXCryptographicException>(() => EncryptedVaultSerializer.Deserialize(input));
    }

    [Fact]
    public void Deserialize_UnsupportedVersion_Throws()
    {
        string json = EncryptedVaultSerializer.Serialize(SampleVault()).Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);
        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_UnknownAlgorithm_Throws()
    {
        string json = EncryptedVaultSerializer.Serialize(SampleVault()).Replace("AES-256-GCM", "ROT13", StringComparison.Ordinal);
        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_InvalidBase64_Throws()
    {
        // Take a valid document and set the salt to a value that is not valid base64.
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        node["salt"] = "!!!not-valid-base64!!!";
        string broken = node.ToJsonString();

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(broken));
    }

    [Theory]
    [InlineData("memoryKiB", 2_000_000_000)] // ~2 TiB memory request
    [InlineData("iterations", int.MaxValue)] // CPU exhaustion
    [InlineData("parallelism", 1000)]        // absurd lane count
    public void Deserialize_ResourceExhaustingKdfParameters_RejectedBeforeDerivation(string field, int value)
    {
        // A malicious vault file (well within the file-size limit) must not be able to make unlock
        // allocate absurd memory or burn unbounded CPU. Validation happens at parse time, before
        // any Argon2 work is attempted.
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        ((JsonObject)node["kdfParameters"]!)[field] = value;
        string malicious = node.ToJsonString();

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(malicious));
    }
}
