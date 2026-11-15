using System.Text;
using System.Text.Json.Nodes;
using BitosX.Core.Serialization;
using BitosX.Core.Tests.Authenticator;

namespace BitosX.Core.Tests.Notes;

/// <summary>
/// <c>tests/fixtures/notes/content.json</c> is exactly what this library produces, and shows the web
/// vault every rule of docs/NOTES.md §2.
/// </summary>
public sealed class NoteFixtureTests
{
    [Fact]
    public void ContentJson_IsExactlyWhatTheLibraryProduces()
    {
        string path = Path.Combine(NoteFixtures.Directory, "content.json");
        string committed = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        string produced = NoteFixtures.RenderContent(NoteFixtures.Read("content.json"));

        if (NoteFixtures.IsUpdating)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(produced));
            return;
        }

        Assert.True(
            string.Equals(committed, produced, StringComparison.Ordinal),
            "content.json is not what the library produces. If the change is intended, run the tests with "
            + NoteFixtures.UpdateVariable + "=1, look at what changed, and say so in docs/NOTES.md: the web vault follows this file.");
    }

    [Fact]
    public void TheDirectory_HoldsOnlyTheOneFile()
        => Assert.Equal(["content.json"], Directory.EnumerateFiles(NoteFixtures.Directory).Select(Path.GetFileName));

    [Fact]
    public void ContentJson_ShowsEveryOutcome()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var refusals = new HashSet<string>(StringComparer.Ordinal);
        var read = new HashSet<int>();
        var written = new HashSet<int>();
        foreach (JsonNode? testCase in NoteFixtures.Read("content.json")["cases"]!.AsArray())
        {
            Assert.True(names.Add(testCase!["name"]!.GetValue<string>()), "two cases have one name");
            JsonObject result = testCase["result"]!.AsObject();
            if (result["refused"] is JsonValue refused)
            {
                refusals.Add(refused.GetValue<string>());
            }
            else
            {
                read.Add(result["schemaVersion"]!.GetValue<int>());
                written.Add(result["writtenSchemaVersion"]!.GetValue<int>());
            }
        }

        Assert.Equal(["invalid", "unsupported_version"], refusals.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], read.Order());
        Assert.Equal([1, 2, 3], written.Order());
    }

    [Fact]
    public void EveryDocumentThatIsRead_IsReadAgainToTheSameItems_FromWhatTheDesktopWrites()
    {
        foreach (JsonNode? testCase in NoteFixtures.Read("content.json")["cases"]!.AsArray())
        {
            (BitosX.Core.Domain.Vault? vault, _) = NoteFixtures.Outcome(TotpFixtures.InputText(testCase!["input"]!));
            if (vault is null)
            {
                continue;
            }

            byte[] bytes = VaultSerializer.SerializeToUtf8Bytes(vault);
            BitosX.Core.Domain.Vault again = VaultSerializer.Deserialize(bytes);
            Assert.Equal(vault.RequiredSchemaVersion, again.SchemaVersion);
            Assert.Equal(vault.Items.Count, again.Items.Count);
            Assert.StartsWith("{\"schemaVersion\":" + vault.RequiredSchemaVersion + ",", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
    }
}
