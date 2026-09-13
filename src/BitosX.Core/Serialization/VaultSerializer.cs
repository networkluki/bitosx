using System.Text.Json;
using System.Text.Json.Serialization;
using BitosX.Core.Domain;

namespace BitosX.Core.Serialization;

/// <summary>
/// Serializes the decrypted <see cref="Vault"/> to and from UTF-8 JSON. This JSON is the
/// plaintext that is fed to the cryptographic layer; it MUST never be written to disk
/// unencrypted.
/// </summary>
public static class VaultSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a vault to UTF-8 JSON bytes.</summary>
    public static byte[] SerializeToUtf8Bytes(Vault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var document = new VaultDocument
        {
            SchemaVersion = vault.SchemaVersion,
            Items = [.. vault.Items],
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }

    /// <summary>Deserializes a vault from UTF-8 JSON bytes. Throws <see cref="VaultValidationException"/> on invalid content.</summary>
    public static Vault Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        VaultDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<VaultDocument>(utf8Json, Options);
        }
        catch (JsonException)
        {
            // Intentionally does not include the parser message: it can echo fragments of the
            // decrypted (secret) content. The exception is swallowed rather than logged.
            throw new VaultValidationException("The decrypted vault content is not valid JSON.");
        }

        if (document is null)
        {
            throw new VaultValidationException("The decrypted vault content is empty.");
        }

        if (document.SchemaVersion != Vault.CurrentSchemaVersion)
        {
            throw new VaultValidationException(
                $"Unsupported vault schema version {document.SchemaVersion}.");
        }

        return new Vault(document.SchemaVersion, document.Items ?? []);
    }

    private sealed class VaultDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("items")] public List<VaultItem>? Items { get; set; }
    }
}
