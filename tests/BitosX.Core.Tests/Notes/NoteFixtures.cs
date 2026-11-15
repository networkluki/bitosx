using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Tests.Authenticator;

namespace BitosX.Core.Tests.Notes;

/// <summary>
/// The shared fixture of the content schema with notes: <c>tests/fixtures/notes/content.json</c>, a
/// decrypted vault document → what is read from it and the schema version it is written back with,
/// or a refusal. The web vault copies the directory and has to arrive at the same results;
/// docs/NOTES.md §6 describes the file.
/// </summary>
/// <remarks>
/// The results are written by this class and by nothing else. Cases (a name and an input) are
/// added by hand, without a result; to write the results again after an intended change, run the
/// tests with <c>BITOSX_UPDATE_NOTE_FIXTURES=1</c>. Without it, a file that differs from what the
/// library produces fails <see cref="NoteFixtureTests"/>.
/// </remarks>
internal static class NoteFixtures
{
    public const string UpdateVariable = "BITOSX_UPDATE_NOTE_FIXTURES";
    public const int Schema = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool IsUpdating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Directory => NoteVaultContentTests.FixtureDirectory("notes");

    public static JsonObject Read(string fileName)
        => JsonNode.Parse(File.ReadAllBytes(Path.Combine(Directory, fileName)))!.AsObject();

    /// <summary>The outcome of reading <paramref name="plaintext"/>: the vault, or the kind of refusal.</summary>
    public static (Vault? Vault, string? Refusal) Outcome(string plaintext)
    {
        try
        {
            return (VaultSerializer.Deserialize(Encoding.UTF8.GetBytes(plaintext)), null);
        }
        catch (VaultValidationException ex)
        {
            // The one refusal the shared fixture tells apart: a version this build does not read.
            // Everything else is "invalid" (docs/NOTES.md §2.3).
            return (null, ex.Message.StartsWith("Unsupported vault schema version", StringComparison.Ordinal)
                ? "unsupported_version"
                : "invalid");
        }
    }

    /// <summary>content.json as the library makes it from the cases of <paramref name="current"/>.</summary>
    public static string RenderContent(JsonObject current)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteStartArray("cases");
            foreach (JsonNode? node in current["cases"]!.AsArray())
            {
                JsonObject testCase = node!.AsObject();
                writer.WriteStartObject();
                writer.WriteString("name", testCase["name"]!.GetValue<string>());
                writer.WritePropertyName("input");
                testCase["input"]!.WriteTo(writer);

                (Vault? vault, string? refusal) = Outcome(TotpFixtures.InputText(testCase["input"]!));
                writer.WriteStartObject("result");
                if (vault is null)
                {
                    writer.WriteString("refused", refusal);
                }
                else
                {
                    writer.WriteNumber("schemaVersion", vault.SchemaVersion);
                    writer.WriteStartArray("items");
                    foreach (VaultItem item in vault.Items)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", item switch
                        {
                            NoteItem => "note",
                            LoginItem => "login",
                            _ => throw new InvalidDataException("An item of an unknown type."),
                        });
                        writer.WriteString("id", item.Id.ToString("D"));
                        writer.WriteString("name", item.Name);
                        if (item.Notes is null)
                        {
                            writer.WriteNull("notes");
                        }
                        else
                        {
                            writer.WriteString("notes", item.Notes);
                        }

                        writer.WriteBoolean("favorite", item.Favorite);
                        if (item is LoginItem login)
                        {
                            writer.WriteBoolean("hasTotp", login.Totp is not null);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber("writtenSchemaVersion", vault.RequiredSchemaVersion);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
