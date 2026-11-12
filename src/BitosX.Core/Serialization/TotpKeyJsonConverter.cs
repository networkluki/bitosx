using System.Text.Json;
using System.Text.Json.Serialization;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;

namespace BitosX.Core.Serialization;

/// <summary>
/// The stored form of an authenticator key inside the decrypted vault (docs/TOTP.md §2):
/// <c>{"Secret":"…","Algorithm":"SHA1","Digits":6,"Period":30}</c>. Read as strictly as the rest of
/// the document: all four members, each once, nothing else, each of its JSON type; an explicit
/// <c>null</c> in place of the object is refused (a login without a key has no <c>Totp</c> member).
/// Whether the values are allowed is <see cref="Validation.VaultValidation"/>'s to say.
/// </summary>
/// <remarks>Every failure is a <see cref="JsonException"/> without a message that quotes content.</remarks>
internal sealed class TotpKeyJsonConverter : JsonConverter<TotpKey?>
{
    public override bool HandleNull => true;

    public override TotpKey? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }

        string? secret = null;
        TotpAlgorithm? algorithm = null;
        int? digits = null;
        int? period = null;

        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonException();
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            int member = reader.ValueTextEquals("Secret"u8) ? 0
                : reader.ValueTextEquals("Algorithm"u8) ? 1
                : reader.ValueTextEquals("Digits"u8) ? 2
                : reader.ValueTextEquals("Period"u8) ? 3
                : throw new JsonException();

            if (!reader.Read())
            {
                throw new JsonException();
            }

            switch (member)
            {
                case 0:
                    secret = secret is null ? ReadString(ref reader) : throw new JsonException();
                    break;
                case 1:
                    algorithm = algorithm is null ? ReadAlgorithm(ref reader) : throw new JsonException();
                    break;
                case 2:
                    digits = digits is null ? ReadInt32(ref reader) : throw new JsonException();
                    break;
                default:
                    period = period is null ? ReadInt32(ref reader) : throw new JsonException();
                    break;
            }
        }

        if (secret is null || algorithm is null || digits is null || period is null)
        {
            throw new JsonException();
        }

        return new TotpKey { Secret = secret, Algorithm = algorithm.Value, Digits = digits.Value, Period = period.Value };
    }

    public override void Write(Utf8JsonWriter writer, TotpKey? value, JsonSerializerOptions options)
    {
        // A login without a key has no member at all (the serializer leaves out nulls); this is
        // reached only if that setting were changed, and then must not write a member a reader refuses.
        if (value is null)
        {
            throw new JsonException();
        }

        writer.WriteStartObject();
        writer.WriteString("Secret"u8, value.Secret);
        writer.WriteString("Algorithm"u8, TotpTexts.AlgorithmName(value.Algorithm));
        writer.WriteNumber("Digits"u8, value.Digits);
        writer.WriteNumber("Period"u8, value.Period);
        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException();

    private static TotpAlgorithm ReadAlgorithm(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException();
        }

        return reader.ValueTextEquals("SHA1"u8) ? TotpAlgorithm.Sha1
            : reader.ValueTextEquals("SHA256"u8) ? TotpAlgorithm.Sha256
            : reader.ValueTextEquals("SHA512"u8) ? TotpAlgorithm.Sha512
            : throw new JsonException();
    }

    private static int ReadInt32(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) ? value : throw new JsonException();
}
