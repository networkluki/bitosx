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

    [Fact]
    public void Deserialize_DuplicateJsonProperty_Rejected()
    {
        // Strict parsing: a crafted file must not smuggle a second value for a security-relevant
        // field via a duplicate JSON property (default last-one-wins would be ambiguous).
        string json = EncryptedVaultSerializer.Serialize(SampleVault());
        // Inject a duplicate "algorithm" property just before the closing brace.
        int lastBrace = json.LastIndexOf('}');
        string tampered = json[..lastBrace] + ",\"algorithm\":\"AES-256-GCM\"" + json[lastBrace..];

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(tampered));
    }

    // ---- Strict parsing (0.5.1) ----

    public static TheoryData<string> EnvelopeFields =>
    [
        "version", "kdf", "kdfParameters", "salt", "algorithm",
        "keyWrapNonce", "encryptedVaultKey", "contentNonce", "ciphertext",
    ];

    [Theory]
    [MemberData(nameof(EnvelopeFields))]
    public void Deserialize_FieldSetToNull_FailsClosed(string field)
    {
        // "kdfParameters": null used to escape as a NullReferenceException.
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        node[field] = null;
        string json = node.ToJsonString();

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(json));
        Assert.Throws<UnsupportedVaultFormatException>(
            () => EncryptedVaultSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [MemberData(nameof(EnvelopeFields))]
    public void Deserialize_FieldMissing_FailsClosed(string field)
    {
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        node.Remove(field);
        string json = node.ToJsonString();

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("salt", 15)]
    [InlineData("salt", 17)]
    [InlineData("keyWrapNonce", 11)]
    [InlineData("keyWrapNonce", 13)]
    [InlineData("contentNonce", 11)]
    [InlineData("contentNonce", 13)]
    [InlineData("encryptedVaultKey", 47)]
    [InlineData("encryptedVaultKey", 49)]
    [InlineData("ciphertext", 15)]
    public void Deserialize_FieldWithWrongLength_FailsClosed(string field, int length)
    {
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        node[field] = Convert.ToBase64String(new byte[length]);

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void Deserialize_UnknownProperty_Rejected()
    {
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        node["hint"] = "anything";

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void Deserialize_UnknownKdfParameter_Rejected()
    {
        var node = (JsonObject)JsonNode.Parse(EncryptedVaultSerializer.Serialize(SampleVault()))!;
        ((JsonObject)node["kdfParameters"]!)["secret"] = "AAAA";

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void Deserialize_Utf8Bytes_RoundTrips_AndMatchesTheStringOverload()
    {
        EncryptedVault original = SampleVault();
        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(original);

        EncryptedVault fromBytes = EncryptedVaultSerializer.Deserialize(bytes);
        EncryptedVault fromString = EncryptedVaultSerializer.Deserialize(Encoding.UTF8.GetString(bytes));

        Assert.Equal(EncryptedVaultFingerprint.Compute(original), EncryptedVaultFingerprint.Compute(fromBytes));
        Assert.Equal(EncryptedVaultFingerprint.Compute(original), EncryptedVaultFingerprint.Compute(fromString));
    }

    [Fact]
    public void SerializeToUtf8Bytes_WritesNoByteOrderMark()
    {
        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(SampleVault());
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Deserialize_Utf8Bytes_AcceptsAByteOrderMark()
    {
        EncryptedVault original = SampleVault();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. EncryptedVaultSerializer.SerializeToUtf8Bytes(original)];

        EncryptedVault parsed = EncryptedVaultSerializer.Deserialize(bytes);

        Assert.Equal(original.Ciphertext, parsed.Ciphertext);
    }

    [Fact]
    public void Deserialize_Utf8Bytes_InvalidUtf8_FailsClosed()
    {
        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(SampleVault());
        bytes[bytes.AsSpan().IndexOf("AES-256-GCM"u8)] = 0xC0; // an invalid UTF-8 lead byte

        Assert.Throws<UnsupportedVaultFormatException>(() => EncryptedVaultSerializer.Deserialize(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{\"version\":1")]
    public void Deserialize_Utf8Bytes_MalformedInput_FailsClosed(string input)
    {
        Assert.Throws<UnsupportedVaultFormatException>(
            () => EncryptedVaultSerializer.Deserialize(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void Deserialize_Utf8Bytes_OnlyAByteOrderMark_FailsClosed()
    {
        Assert.Throws<UnsupportedVaultFormatException>(
            () => EncryptedVaultSerializer.Deserialize(new byte[] { 0xEF, 0xBB, 0xBF }));
    }
}
