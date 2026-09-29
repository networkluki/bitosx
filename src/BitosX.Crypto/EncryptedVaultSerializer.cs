using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitosX.Crypto;

/// <summary>
/// Serializes <see cref="EncryptedVault"/> to and from the versioned JSON on-disk format.
/// Parsing fails closed: unknown versions, unsupported algorithms, malformed base64, and
/// structurally invalid fields all raise <see cref="UnsupportedVaultFormatException"/>.
/// There is never a plaintext fallback.
/// </summary>
public static class EncryptedVaultSerializer
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Strict parsing for untrusted vault/backup input: reject duplicate JSON properties so a
    // crafted file cannot smuggle an ambiguous second value for a security-relevant field
    // (e.g. two "kdfParameters" or two "algorithm" entries with last-one-wins semantics).
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        AllowDuplicateProperties = false,

        // Unknown properties are rejected rather than ignored: every field of the envelope is
        // security relevant, and nothing this build does not understand belongs in a v1 file.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Serializes an encrypted vault to its JSON representation.</summary>
    public static string Serialize(EncryptedVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);

        var document = new EncryptedVaultDocument
        {
            Version = vault.Version,
            Kdf = vault.Kdf,
            KdfParameters = new KdfParametersDocument
            {
                MemoryKiB = vault.KdfParameters.MemoryKiB,
                Iterations = vault.KdfParameters.Iterations,
                Parallelism = vault.KdfParameters.DegreeOfParallelism,
                KeyLengthBytes = vault.KdfParameters.KeyLengthBytes,
            },
            Salt = Convert.ToBase64String(vault.Salt),
            Algorithm = vault.Algorithm,
            KeyWrapNonce = Convert.ToBase64String(vault.KeyWrapNonce),
            EncryptedVaultKey = Convert.ToBase64String(vault.EncryptedVaultKey),
            ContentNonce = Convert.ToBase64String(vault.ContentNonce),
            Ciphertext = Convert.ToBase64String(vault.Ciphertext),
        };

        return JsonSerializer.Serialize(document, SerializeOptions);
    }

    /// <summary>Serializes an encrypted vault to UTF-8 JSON, without a byte order mark.</summary>
    public static byte[] SerializeToUtf8Bytes(EncryptedVault vault) => Encoding.UTF8.GetBytes(Serialize(vault));

    /// <summary>Parses and validates an encrypted vault from its JSON representation.</summary>
    public static EncryptedVault Deserialize(string json)
    {
        // Vault content is untrusted input read from disk: empty or blank content fails closed
        // as a format error rather than an argument-validation exception.
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new UnsupportedVaultFormatException("The vault file is empty.");
        }

        EncryptedVaultDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<EncryptedVaultDocument>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            throw new UnsupportedVaultFormatException("The vault file is not valid JSON.", ex);
        }

        return FromDocument(document);
    }

    /// <summary>
    /// Parses and validates an encrypted vault from the raw bytes of a vault or backup file. The
    /// bytes must be valid UTF-8 JSON (invalid sequences are rejected, never replaced); a leading
    /// byte order mark is accepted.
    /// </summary>
    public static EncryptedVault Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.StartsWith(Encoding.UTF8.Preamble))
        {
            utf8Json = utf8Json[Encoding.UTF8.Preamble.Length..];
        }

        if (utf8Json.IndexOfAnyExcept(" \t\r\n"u8) < 0)
        {
            throw new UnsupportedVaultFormatException("The vault file is empty.");
        }

        EncryptedVaultDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<EncryptedVaultDocument>(utf8Json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            throw new UnsupportedVaultFormatException("The vault file is not valid JSON.", ex);
        }

        return FromDocument(document);
    }

    private static EncryptedVault FromDocument(EncryptedVaultDocument? document)
    {
        if (document is null)
        {
            throw new UnsupportedVaultFormatException("The vault file is empty.");
        }

        if (document.Version != CryptoConstants.FormatVersion)
        {
            throw new UnsupportedVaultFormatException(
                $"Unsupported vault format version {document.Version}. This build supports version {CryptoConstants.FormatVersion}.");
        }

        if (!string.Equals(document.Kdf, CryptoConstants.KdfArgon2id, StringComparison.Ordinal))
        {
            throw new UnsupportedVaultFormatException($"Unsupported key-derivation function '{document.Kdf}'.");
        }

        if (!string.Equals(document.Algorithm, CryptoConstants.AlgorithmAes256Gcm, StringComparison.Ordinal))
        {
            throw new UnsupportedVaultFormatException($"Unsupported encryption algorithm '{document.Algorithm}'.");
        }

        // An explicit JSON null replaces the default instance.
        if (document.KdfParameters is null)
        {
            throw new UnsupportedVaultFormatException("The vault field 'kdfParameters' is missing.");
        }

        var parameters = new Argon2Parameters(
            document.KdfParameters.MemoryKiB,
            document.KdfParameters.Iterations,
            document.KdfParameters.Parallelism,
            document.KdfParameters.KeyLengthBytes);
        parameters.Validate();

        byte[] salt = DecodeBase64(document.Salt, nameof(document.Salt));
        byte[] keyWrapNonce = DecodeBase64(document.KeyWrapNonce, nameof(document.KeyWrapNonce));
        byte[] encryptedVaultKey = DecodeBase64(document.EncryptedVaultKey, nameof(document.EncryptedVaultKey));
        byte[] contentNonce = DecodeBase64(document.ContentNonce, nameof(document.ContentNonce));
        byte[] ciphertext = DecodeBase64(document.Ciphertext, nameof(document.Ciphertext));

        RequireLength(salt, CryptoConstants.SaltSizeBytes, "salt");
        RequireLength(keyWrapNonce, CryptoConstants.NonceSizeBytes, "key-wrap nonce");
        RequireLength(contentNonce, CryptoConstants.NonceSizeBytes, "content nonce");
        // Wrapped vault key = 32-byte key ciphertext + 16-byte tag.
        RequireLength(encryptedVaultKey, CryptoConstants.KeySizeBytes + CryptoConstants.TagSizeBytes, "encrypted vault key");
        // Ciphertext must at least contain a tag.
        if (ciphertext.Length < CryptoConstants.TagSizeBytes)
        {
            throw new UnsupportedVaultFormatException("The vault ciphertext is too short.");
        }

        return new EncryptedVault(
            document.Version,
            document.Kdf,
            parameters,
            salt,
            document.Algorithm,
            keyWrapNonce,
            encryptedVaultKey,
            contentNonce,
            ciphertext);
    }

    private static byte[] DecodeBase64(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new UnsupportedVaultFormatException($"The vault field '{fieldName}' is missing.");
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new UnsupportedVaultFormatException($"The vault field '{fieldName}' is not valid base64.", ex);
        }
    }

    private static void RequireLength(byte[] value, int expected, string fieldName)
    {
        if (value.Length != expected)
        {
            throw new UnsupportedVaultFormatException(
                $"The vault {fieldName} has an invalid length ({value.Length} bytes; expected {expected}).");
        }
    }

    // ---- JSON data-transfer objects ----

    private sealed class EncryptedVaultDocument
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("kdf")] public string Kdf { get; set; } = string.Empty;
        [JsonPropertyName("kdfParameters")] public KdfParametersDocument? KdfParameters { get; set; } = new();
        [JsonPropertyName("salt")] public string Salt { get; set; } = string.Empty;
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = string.Empty;
        [JsonPropertyName("keyWrapNonce")] public string KeyWrapNonce { get; set; } = string.Empty;
        [JsonPropertyName("encryptedVaultKey")] public string EncryptedVaultKey { get; set; } = string.Empty;
        [JsonPropertyName("contentNonce")] public string ContentNonce { get; set; } = string.Empty;
        [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = string.Empty;
    }

    private sealed class KdfParametersDocument
    {
        [JsonPropertyName("memoryKiB")] public int MemoryKiB { get; set; }
        [JsonPropertyName("iterations")] public int Iterations { get; set; }
        [JsonPropertyName("parallelism")] public int Parallelism { get; set; }
        [JsonPropertyName("keyLengthBytes")] public int KeyLengthBytes { get; set; }
    }
}
