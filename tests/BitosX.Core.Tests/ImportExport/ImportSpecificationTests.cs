using System.Text.RegularExpressions;
using BitosX.Core.ImportExport;
using static BitosX.Core.Tests.ImportExport.ImportTesting;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// docs/IMPORT_EXPORT.md is what the web vault is written from. These tests read its tables and
/// hold them against the library, so that the document cannot say one thing and the code do another.
/// </summary>
public sealed partial class ImportSpecificationTests
{
    private static readonly string Specification = ReadSpecification();

    private static string ReadSpecification()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "docs", "IMPORT_EXPORT.md");
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }
        }

        throw new FileNotFoundException("docs/IMPORT_EXPORT.md was not found above the test's directory.");
    }

    /// <summary>The text of one section: from its heading to the next heading of the same or a higher level.</summary>
    private static string Section(string heading)
    {
        int start = Specification.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The specification has no section '{heading}'.");
        string marks = heading[..heading.IndexOf(' ', StringComparison.Ordinal)];
        Match next = Regex.Match(Specification[(start + heading.Length + 2)..], $@"^#{{1,{marks.Length}}} ", RegexOptions.Multiline);
        return next.Success ? Specification.Substring(start, heading.Length + 2 + next.Index) : Specification[start..];
    }

    /// <summary>The rows of the tables of a section whose first cell is a code in backticks: code, then the next cell.</summary>
    private static Dictionary<string, string> CodeRows(string section)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match row in CodeRow().Matches(section))
        {
            rows.Add(row.Groups[1].Value, row.Groups[2].Value.Trim());
        }

        return rows;
    }

    [GeneratedRegex(@"^\| `([a-z0-9_]+)` \| (.+?) \|$", RegexOptions.Multiline)]
    private static partial Regex CodeRow();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InBackticks();

    [Fact]
    public void EveryRefusal_HasItsCodeAndItsTextInTheSpecification()
    {
        Dictionary<string, string> rows = CodeRows(Section("### 13.1 Refusals: a file that is not read"));

        Assert.Equal(Enum.GetValues<ImportRefusal>().Select(ImportTexts.Code).Order(), rows.Keys.Order());
        foreach (ImportRefusal refusal in Enum.GetValues<ImportRefusal>())
        {
            Assert.Equal(ImportTexts.Text(refusal), rows[ImportTexts.Code(refusal)]);
        }
    }

    [Fact]
    public void EveryReason_HasItsCodeAndItsTextInTheSpecification()
    {
        Dictionary<string, string> rows = CodeRows(Section("### 13.2 Reasons: an entry that is not imported"));

        Assert.Equal(Enum.GetValues<ImportReason>().Select(ImportTexts.Code).Order(), rows.Keys.Order());
        foreach (ImportReason reason in Enum.GetValues<ImportReason>())
        {
            Assert.Equal(ImportTexts.Text(reason), rows[ImportTexts.Code(reason)]);
        }
    }

    [Fact]
    public void TheReasons_AreCheckedInTheOrderTheSpecificationGives()
    {
        // The table of §7.6 is in the order of the checks, which is the order of the enumeration.
        string[] inOrder = [.. Regex.Matches(Section("### 7.6 The limits, and the reason an entry is not imported"), @"^\| \d+ \| .+ \| `([a-z_]+)` \|$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

        Assert.Equal(Enum.GetValues<ImportReason>().Select(ImportTexts.Code), inOrder);
    }

    [Fact]
    public void EveryKind_HasItsCodeAndItsWordsInTheSpecification()
    {
        Dictionary<string, string> rows = CodeRows(Section("### 13.3 Kinds"));

        foreach (ImportExtraKind kind in Enum.GetValues<ImportExtraKind>())
        {
            Assert.Equal(ImportTexts.Text(kind), rows[ImportTexts.Code(kind)]);
        }

        foreach (ImportUnsupportedKind kind in Enum.GetValues<ImportUnsupportedKind>())
        {
            Assert.Equal(ImportTexts.Text(kind), rows[ImportTexts.Code(kind)]);
        }

        Assert.Equal(Enum.GetValues<ImportExtraKind>().Length + Enum.GetValues<ImportUnsupportedKind>().Length, rows.Count);
        Assert.Contains($"is `{ImportTexts.UntitledName}`", Section("### 13.3 Kinds"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinesExtrasAreCopiedWith_AreTheOnesOfTheSpecification()
    {
        string section = Section("### 7.4 What BitosX has no field for");

        Assert.Contains(
            $"```\n{ImportTexts.NotesLabelTotp}: <value>\n{ImportTexts.NotesLabelFolder}: <value>\n{ImportTexts.NotesLabelOtherAddress}: <value>\n<name>: <value>\n```",
            section,
            StringComparison.Ordinal);
        Assert.Contains($"has the label `{ImportTexts.NotesLabelNamelessField}`", section, StringComparison.Ordinal);

        // And they are what is written.
        string notes = Only("name,password,totp,folder,url,fields\nx,FAKE,FAKEKEY,Work,ftp://files.example,\"PIN: 0000\nnameless\"\n", new ImportOptions(CopyExtrasToNotes: true)).Notes;
        Assert.Equal(
            $"{ImportTexts.NotesLabelTotp}: FAKEKEY\n{ImportTexts.NotesLabelFolder}: Work\n{ImportTexts.NotesLabelOtherAddress}: ftp://files.example\nPIN: 0000\n{ImportTexts.NotesLabelNamelessField}: nameless",
            notes);
    }

    [Fact]
    public void WhatAPersonIsAskedAndTold_IsInTheSpecification_WordForWord()
    {
        // The web vault takes these from the document; a word changed here has to change there.
        // (A quotation runs over several lines there, each starting with "> ".)
        static string Joined(string section) => section.Replace("\n> ", " ", StringComparison.Ordinal).Replace('\n', ' ');

        Assert.Contains(" " + ExportTexts.Warning + " ", Joined(Section("### 11.3 Nothing is put in front of a value")), StringComparison.Ordinal);
        Assert.Contains($"\"{ExportTexts.Acknowledgement}\"", Joined(Section("### 11.3 Nothing is put in front of a value")), StringComparison.Ordinal);
        Assert.Contains($"\"{ImportTexts.OptionCopyExtrasToNotes}\"", Joined(Section("### 7.4 What BitosX has no field for")), StringComparison.Ordinal);
        Assert.Contains($"\"{ImportTexts.OptionImportDuplicates}\"", Joined(Section("## 8. Duplicates")), StringComparison.Ordinal);
        Assert.Contains(" " + ImportTexts.FileStillHoldsPasswords + " ", Joined(Section("## 10. Applying an import (desktop)")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheColumnNames_AreReadInTheOrderTheSpecificationGives()
    {
        // | field | `a`, `b`, `c` | where each comes from |
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match row in Regex.Matches(Section("### 3.2 The header"), @"^\| ([a-z ]+) \| (`[^|]+`) \| .+ \|$", RegexOptions.Multiline))
        {
            fields.Add(row.Groups[1].Value, [.. InBackticks().Matches(row.Groups[2].Value).Select(m => m.Groups[1].Value)]);
        }

        Assert.Equal(
            ["address", "authenticator key", "custom fields", "favorite", "folder", "name", "notes", "password", "type", "username"],
            fields.Keys.Order(StringComparer.Ordinal));

        foreach ((string field, string[] names) in fields)
        {
            // Each name alone is read...
            foreach (string name in names)
            {
                Assert.True(IsRead(field, [name], readFrom: 0), $"'{name}' is not read as the {field}.");
            }

            // ...and of two, the one the specification lists first, wherever it is in the header.
            for (int i = 0; i < names.Length; i++)
            {
                for (int j = i + 1; j < names.Length; j++)
                {
                    Assert.True(IsRead(field, [names[i], names[j]], readFrom: 0), $"{field}: '{names[i]}' before '{names[j]}'.");
                    Assert.True(IsRead(field, [names[j], names[i]], readFrom: 1), $"{field}: '{names[i]}' before '{names[j]}', to its right.");
                }
            }
        }

        // No name is listed for two fields.
        string[] all = [.. fields.Values.SelectMany(names => names)];
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Whether, of the columns named, the one at <paramref name="readFrom"/> is what the field is read from.</summary>
    private static bool IsRead(string field, string[] columns, int readFrom)
    {
        // Every file needs a password and something to tell a login by; the field under test brings its own.
        string[] header = field switch
        {
            "password" => ["name", .. columns],
            "name" or "address" or "username" => ["password", .. columns],
            _ => ["name", "password", .. columns],
        };
        int offset = header.Length - columns.Length;

        string[] values = [.. header.Select(_ => "filler")];
        for (int i = 0; i < columns.Length; i++)
        {
            values[offset + i] = i == readFrom ? Marker(field, wanted: true) : Marker(field, wanted: false);
        }

        ParsedImport parsed = Parse(string.Join(',', header) + "\n" + string.Join(',', values) + "\n");
        if (field == "type")
        {
            return parsed.Unsupported.ContainsKey(ImportUnsupportedKind.SecureNote);
        }

        ImportEntry entry = Assert.Single(parsed.Entries);
        return field switch
        {
            "name" => entry.Name == "wanted",
            "address" => entry.Addresses.SequenceEqual(["wanted"]),
            "username" => entry.Username == "wanted",
            "password" => entry.Password == "wanted",
            "notes" => entry.Notes == "wanted",
            "favorite" => entry.Favorite,
            "authenticator key" => entry.Totp == "wanted",
            "folder" => entry.Folder == "wanted",
            "custom fields" => entry.CustomFields.Count == 1 && entry.CustomFields[0].Value == "wanted",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    private static string Marker(string field, bool wanted) => field switch
    {
        "favorite" => wanted ? "1" : "0",
        "type" => wanted ? "note" : "login",
        _ => wanted ? "wanted" : "other",
    };

    [Fact]
    public void TheHeadersOfTheSpecification_GetTheLabelsItGives()
    {
        // | `Label` | `a,b,c` | source |   and, for a second header of the same label,   | | `a,b,c` | source |
        string section = Section("### 3.3 The label");
        var headers = new List<(string Label, string Header)>();
        string label = string.Empty;
        foreach (Match row in Regex.Matches(section, @"^\| (`[^`]+`)? ?\| `([a-z ,_]+)` \| .+ \|$", RegexOptions.Multiline))
        {
            if (row.Groups[1].Success)
            {
                label = row.Groups[1].Value.Trim('`');
            }

            headers.Add((label, row.Groups[2].Value));
        }

        // The row that says "the same two without archiveddate" is spelled out here.
        Assert.Contains("the same two without `archiveddate`", section, StringComparison.Ordinal);
        foreach ((string bitwardenLabel, string header) in headers.Where(h => h.Header.Contains("archiveddate", StringComparison.Ordinal)).ToArray())
        {
            headers.Add((bitwardenLabel, header.Replace("archiveddate,", string.Empty, StringComparison.Ordinal)));
        }

        Assert.Equal(10, headers.Count);
        Assert.Equal(
            new[]
            {
                ImportTexts.LabelChromeCsv, ImportTexts.LabelFirefoxCsv, ImportTexts.LabelBitwardenCsv,
                ImportTexts.LabelKeePassXcCsv, ImportTexts.LabelKeePass1Csv,
            }.Order(),
            headers.Select(h => h.Label).Distinct().Order());

        foreach ((string expected, string header) in headers)
        {
            Assert.Equal(expected, Parse(header + "\n").Label);
            Assert.Equal(expected, Parse(header.ToUpperInvariant() + "\r\n").Label);             // in any letter case
            Assert.Equal(ImportTexts.LabelCsv, Parse(header + ",one more column\n").Label);      // and only exactly
        }

        Assert.Contains($"has the label `{ImportTexts.LabelCsv}`", section, StringComparison.Ordinal);
        foreach (string other in new[] { ImportTexts.LabelBitwardenJson, ImportTexts.LabelKeePassXml, ImportTexts.LabelBitosXJson })
        {
            Assert.Contains($"`{other}`", Section("## 9. The plan: what a preview shows"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheLimitsOfTheSpecification_AreTheLimitsOfTheLibrary()
    {
        string limits = Section("## 1. Limits");

        Assert.Contains("16 MiB (16,777,216 bytes)", limits, StringComparison.Ordinal);
        Assert.Equal(16_777_216, ImportLimits.MaxFileBytes);
        Assert.Contains("| Entries in one file | 10,000 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Items in the vault after the import | 10,000 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Nesting of JSON | 64 ", limits, StringComparison.Ordinal);
        Assert.Contains("| Nesting of groups in a KeePass file | 64,", limits, StringComparison.Ordinal);
        Assert.Contains("| Nesting of elements in an XML file | 100,", limits, StringComparison.Ordinal);
        Assert.Contains("| Name | 1 to 200 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Username | at most 320 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Password | at most 4096 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Notes | at most 16384 |", limits, StringComparison.Ordinal);
        Assert.Contains("| One web address | at most 2048 |", limits, StringComparison.Ordinal);
        Assert.Contains("| Web addresses of one item | at most 64 |", limits, StringComparison.Ordinal);
        Assert.Equal(
            (200, 320, 4096, 16384, 2048, 64),
            (Validation.VaultValidation.MaxNameLength, Validation.VaultValidation.MaxUsernameLength, Validation.VaultValidation.MaxPasswordLength,
                Validation.VaultValidation.MaxNotesLength, Validation.VaultValidation.MaxUrlLength, Validation.VaultValidation.MaxUrlCount));
    }

    [Fact]
    public void TheCharactersThatAreTrimmed_AreTheOnesTheSpecificationLists()
    {
        string section = Section("### 2.1 The text operations used below");
        Match list = Regex.Match(section, @"at either end — (.+?)\. \(", RegexOptions.Singleline);
        Assert.True(list.Success);

        var listed = new SortedSet<int>();
        foreach (Match range in Regex.Matches(list.Groups[1].Value, @"U\+([0-9A-F]{4})(?: to U\+([0-9A-F]{4}))?"))
        {
            int first = Convert.ToInt32(range.Groups[1].Value, 16);
            int last = range.Groups[2].Success ? Convert.ToInt32(range.Groups[2].Value, 16) : first;
            for (int c = first; c <= last; c++)
            {
                listed.Add(c);
            }
        }

        Assert.Equal(Enumerable.Range(0, 0x10000).Where(c => ImportText.IsTrimmed((char)c)), listed);
    }

    [Fact]
    public void TheFixturesTheSpecificationNames_AreThere_AndItNamesEveryOne()
    {
        string section = Section("### 14.2 What there is");
        string[] named = [.. InBackticks().Matches(section).Select(m => m.Groups[1].Value).Where(name => name.Contains('.', StringComparison.Ordinal) && !name.Contains(' ', StringComparison.Ordinal) && !name.Contains("://", StringComparison.Ordinal))];
        string[] present = [.. Directory.EnumerateFiles(ConformanceFixtures.Directory).Select(Path.GetFileName)!];

        foreach (string name in named.Where(n => !n.Contains('*', StringComparison.Ordinal) && !n.StartsWith("ConformanceFixtures", StringComparison.Ordinal)))
        {
            Assert.Contains(name, present);
        }

        // Every input that is neither refused-* nor generated-* is described by name.
        foreach (string file in present.Where(f => !f.EndsWith(ConformanceFixtures.ExpectedSuffix, StringComparison.Ordinal) && !f.StartsWith("refused-", StringComparison.Ordinal)))
        {
            Assert.Contains($"`{file}`", section, StringComparison.Ordinal);
        }

        Assert.Contains("`refused-*`", section, StringComparison.Ordinal);
        Assert.Contains("`generated-*`", section, StringComparison.Ordinal);
        Assert.Contains($"{ConformanceFixtures.UpdateVariable}=1", Section("## 14. The fixtures"), StringComparison.Ordinal);
    }
}
