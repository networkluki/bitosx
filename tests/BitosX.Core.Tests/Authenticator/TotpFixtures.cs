using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;

namespace BitosX.Core.Tests.Authenticator;

/// <summary>
/// The shared fixtures of authenticator keys: <c>tests/fixtures/totp/keys.json</c> (a text → a key,
/// a refusal, or nothing) and <c>codes.json</c> (a key and a time → a code). The web vault copies
/// the directory and has to arrive at the same results; docs/TOTP.md §7 describes the files.
/// </summary>
/// <remarks>
/// The results are written by this class and by nothing else. Cases (a name and an input) are
/// added by hand, without a result; to write the results again after an intended change, run the
/// tests with <c>BITOSX_UPDATE_TOTP_FIXTURES=1</c>. Without it, a file that differs from what the
/// library produces fails <see cref="TotpFixtureTests"/>.
/// </remarks>
internal static class TotpFixtures
{
    public const string UpdateVariable = "BITOSX_UPDATE_TOTP_FIXTURES";
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

    /// <summary>The directory of the fixtures in the source tree.</summary>
    public static string Directory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "tests", "fixtures", "totp");
                if (System.IO.Directory.Exists(path))
                {
                    return path;
                }
            }

            throw new DirectoryNotFoundException("tests/fixtures/totp was not found above the test's directory.");
        }
    }

    public static JsonObject Read(string fileName)
        => JsonNode.Parse(File.ReadAllBytes(Path.Combine(Directory, fileName)))!.AsObject();

    /// <summary>The text of a case: a string, or <c>{"parts":[{"text":…,"repeat":n},…]}</c>.</summary>
    public static string InputText(JsonNode input)
    {
        if (input is JsonValue value)
        {
            return value.GetValue<string>();
        }

        var text = new StringBuilder();
        foreach (JsonNode? part in input["parts"]!.AsArray())
        {
            string piece = part!["text"]!.GetValue<string>();
            int repeat = part["repeat"]!.GetValue<int>();
            for (int i = 0; i < repeat; i++)
            {
                text.Append(piece);
            }
        }

        return text.ToString();
    }

    public static TotpAlgorithm Algorithm(string name) => name switch
    {
        "SHA1" => TotpAlgorithm.Sha1,
        "SHA256" => TotpAlgorithm.Sha256,
        "SHA512" => TotpAlgorithm.Sha512,
        _ => throw new InvalidDataException("Unknown algorithm in a fixture."),
    };

    public static TotpKey KeyOf(JsonNode key) => new()
    {
        Secret = key["secret"]!.GetValue<string>(),
        Algorithm = Algorithm(key["algorithm"]!.GetValue<string>()),
        Digits = key["digits"]!.GetValue<int>(),
        Period = key["period"]!.GetValue<int>(),
    };

    /// <summary>keys.json as the library makes it from the cases of <paramref name="current"/>.</summary>
    public static string RenderKeys(JsonObject current) => Render(current, (writer, testCase) =>
    {
        writer.WritePropertyName("input");
        testCase["input"]!.WriteTo(writer);

        TotpParseResult result = TotpKeyParser.Parse(InputText(testCase["input"]!));
        writer.WriteStartObject("result");
        if (result.Key is { } key)
        {
            writer.WritePropertyName("key");
            WriteKey(writer, key);
        }
        else if (result.Refusal is { } refusal)
        {
            writer.WriteString("refused", TotpTexts.Code(refusal));
        }
        else
        {
            writer.WriteBoolean("empty", true);
        }

        writer.WriteEndObject();
    });

    /// <summary>codes.json as the library makes it from the cases of <paramref name="current"/>.</summary>
    public static string RenderCodes(JsonObject current) => Render(current, (writer, testCase) =>
    {
        TotpKey key = KeyOf(testCase["key"]!);
        long unixSeconds = testCase["unixSeconds"]!.GetValue<long>();
        writer.WritePropertyName("key");
        WriteKey(writer, key);
        writer.WriteNumber("unixSeconds", unixSeconds);

        TotpCode code = TotpGenerator.Generate(key, unixSeconds);
        writer.WriteStartObject("result");
        writer.WriteString("code", code.Code);
        writer.WriteString("display", code.Display);
        writer.WriteNumber("secondsLeft", code.SecondsLeft);
        writer.WriteEndObject();
    });

    private static void WriteKey(Utf8JsonWriter writer, TotpKey key)
    {
        writer.WriteStartObject();
        writer.WriteString("secret", key.Secret);
        writer.WriteString("algorithm", TotpTexts.AlgorithmName(key.Algorithm));
        writer.WriteNumber("digits", key.Digits);
        writer.WriteNumber("period", key.Period);
        writer.WriteEndObject();
    }

    private static string Render(JsonObject current, Action<Utf8JsonWriter, JsonObject> writeCase)
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
                writeCase(writer, testCase);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
