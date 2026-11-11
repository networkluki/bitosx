using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>What the tests of the import library share. Every value in them is made up.</summary>
internal static class ImportTesting
{
    public static ParsedImport Parse(string text) => ImportParser.Parse(Encoding.UTF8.GetBytes(text));

    public static ParsedImport Parse(byte[] bytes) => ImportParser.Parse(bytes);

    /// <summary>The kind of refusal parsing <paramref name="text"/> ends in.</summary>
    public static ImportRefusal RefusalOf(string text) => RefusalOf(Encoding.UTF8.GetBytes(text));

    public static ImportRefusal RefusalOf(byte[] bytes)
        => Assert.Throws<ImportRefusedException>(() => ImportParser.Parse(bytes)).Kind;

    public static ImportPlan Plan(string text, ImportOptions? options = null, params VaultItem[] existing)
        => ImportPlanner.Plan(Parse(text), existing, options ?? ImportOptions.Default);

    public static ImportPlan Plan(ParsedImport parsed, ImportOptions? options = null, params VaultItem[] existing)
        => ImportPlanner.Plan(parsed, existing, options ?? ImportOptions.Default);

    /// <summary>The one candidate of a file with one login.</summary>
    public static ImportCandidate Only(string text, ImportOptions? options = null)
        => Assert.Single(Plan(text, options).Candidates);

    public static LoginItem Login(string name, string? username = null, string? password = null, params string[] urls)
        => new() { Name = name, Username = username, Password = password, Urls = urls };

    /// <summary>A CSV file in Chrome's layout with one row.</summary>
    public static string ChromeRow(string name = "", string url = "", string username = "", string password = "", string note = "")
        => "name,url,username,password,note\n" + string.Join(',', Quote(name), Quote(url), Quote(username), Quote(password), Quote(note)) + "\n";

    public static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
