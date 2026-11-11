namespace BitosX.Core.ImportExport;

/// <summary>
/// Reads a comma-separated export of any password manager: one reader, which finds the columns it
/// knows by the names in the header line. Which names, and where each one comes from, is in
/// docs/IMPORT_EXPORT.md.
/// </summary>
internal static class CsvImportReader
{
    // For each field, the header names it is read from, in the order they are tried. Names are
    // compared after trimming and with A to Z as a to z.
    private static readonly string[] NameColumns = ["name", "title", "account"];
    private static readonly string[] UrlColumns = ["url", "urls", "login_uri", "uri", "website", "web site"];
    private static readonly string[] UsernameColumns = ["username", "login_username", "user name", "login name"];
    private static readonly string[] PasswordColumns = ["password", "login_password"];
    private static readonly string[] NotesColumns = ["note", "notes", "comments", "extra"];
    private static readonly string[] FavoriteColumns = ["favorite", "fav"];
    private static readonly string[] TotpColumns = ["totp", "login_totp", "otpauth"];
    private static readonly string[] TypeColumns = ["type"];
    private static readonly string[] FolderColumns = ["folder", "group", "grouping"];
    private static readonly string[] CustomFieldColumns = ["fields"];

    // KeePass 2's "KeePass CSV (1.x)" export. It is also the one layout with its own way of quoting.
    private static readonly string[] KeePass1Header = ["account", "login name", "password", "web site", "comments"];

    // The headers of the exporters whose source was read, exactly as they write them (in lower case).
    private static readonly (string Label, string[] Header)[] KnownHeaders =
    [
        (ImportTexts.LabelChromeCsv, ["name", "url", "username", "password", "note"]),
        (ImportTexts.LabelChromeCsv, ["name", "url", "username", "password"]),
        (ImportTexts.LabelFirefoxCsv,
            ["url", "username", "password", "httprealm", "formactionorigin", "guid", "timecreated", "timelastused", "timepasswordchanged"]),
        (ImportTexts.LabelBitwardenCsv,
            ["folder", "favorite", "type", "name", "notes", "fields", "reprompt", "archiveddate", "login_uri", "login_username", "login_password", "login_totp"]),
        (ImportTexts.LabelBitwardenCsv,
            ["folder", "favorite", "type", "name", "notes", "fields", "reprompt", "login_uri", "login_username", "login_password", "login_totp"]),
        (ImportTexts.LabelBitwardenCsv,
            ["collections", "type", "name", "notes", "fields", "reprompt", "archiveddate", "login_uri", "login_username", "login_password", "login_totp"]),
        (ImportTexts.LabelBitwardenCsv,
            ["collections", "type", "name", "notes", "fields", "reprompt", "login_uri", "login_username", "login_password", "login_totp"]),
        (ImportTexts.LabelKeePassXcCsv,
            ["group", "title", "username", "password", "url", "notes", "totp", "icon", "last modified", "created"]),
        (ImportTexts.LabelKeePassXcCsv, ["group", "title", "username", "password", "url", "notes"]),
        (ImportTexts.LabelKeePass1Csv, KeePass1Header),
    ];

    public static ParsedImport Read(string text)
    {
        var reader = new CsvRecordReader(text);
        var values = new List<string>();

        // The header is the first record that is not blank.
        do
        {
            if (!reader.TryReadRecord(values))
            {
                throw new ImportRefusedException(ImportRefusal.NoRecognisedColumns);
            }
        }
        while (IsBlankRecord(values));

        int columnCount = values.Count;
        string[] header = new string[columnCount];
        var firstColumnNamed = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < columnCount; i++)
        {
            header[i] = ImportText.ToLowerAscii(ImportText.Trim(values[i]));
            if (header[i].Length > 0)
            {
                firstColumnNamed.TryAdd(header[i], i); // of two columns with one name, the left one
            }
        }

        int nameColumn = Find(firstColumnNamed, NameColumns, out _);
        int urlColumn = Find(firstColumnNamed, UrlColumns, out string? urlColumnName);
        int usernameColumn = Find(firstColumnNamed, UsernameColumns, out _);
        int passwordColumn = Find(firstColumnNamed, PasswordColumns, out _);
        int notesColumn = Find(firstColumnNamed, NotesColumns, out _);
        int favoriteColumn = Find(firstColumnNamed, FavoriteColumns, out _);
        int totpColumn = Find(firstColumnNamed, TotpColumns, out _);
        int typeColumn = Find(firstColumnNamed, TypeColumns, out _);
        int folderColumn = Find(firstColumnNamed, FolderColumns, out _);
        int customFieldColumn = Find(firstColumnNamed, CustomFieldColumns, out _);

        if (passwordColumn < 0 || (nameColumn < 0 && urlColumn < 0 && usernameColumn < 0))
        {
            throw new ImportRefusedException(ImportRefusal.NoRecognisedColumns);
        }

        // Bitwarden writes the addresses of an item into one cell, with commas between them.
        bool addressesAreCommaSeparated = urlColumnName is "login_uri" or "uri";

        string label = LabelFor(header);
        if (header.AsSpan().SequenceEqual(KeePass1Header))
        {
            // KeePass writes a quote inside a value as \" and a backslash as \\ in this layout.
            reader.BackslashEscapes = true;
        }

        var used = new HashSet<int>
        {
            nameColumn, urlColumn, usernameColumn, passwordColumn, notesColumn,
            favoriteColumn, totpColumn, typeColumn, folderColumn, customFieldColumn,
        };
        var ignoredColumns = new List<string>();
        for (int i = 0; i < columnCount; i++)
        {
            if (header[i].Length > 0 && !used.Contains(i))
            {
                ignoredColumns.Add(ImportText.Trim(values[i]));
            }
        }

        var entries = new List<ImportEntry>();
        var unsupported = new Dictionary<ImportUnsupportedKind, int>();
        int records = 0;

        while (reader.TryReadRecord(values))
        {
            if (IsBlankRecord(values))
            {
                continue;
            }

            records++;
            if (records > ImportLimits.MaxEntries)
            {
                throw new ImportRefusedException(ImportRefusal.TooManyEntries);
            }

            // The one value of a type column that is known to mean "not a login": Bitwarden's notes.
            if (ImportText.ToLowerAscii(ImportText.Trim(Cell(values, typeColumn))) == "note")
            {
                unsupported[ImportUnsupportedKind.SecureNote] =
                    unsupported.GetValueOrDefault(ImportUnsupportedKind.SecureNote) + 1;
                continue;
            }

            string addressCell = Cell(values, urlColumn);
            string favorite = ImportText.ToLowerAscii(ImportText.Trim(Cell(values, favoriteColumn)));

            entries.Add(new ImportEntry
            {
                Position = records,
                Name = Cell(values, nameColumn),
                Username = Cell(values, usernameColumn),
                Password = Cell(values, passwordColumn),
                Notes = Cell(values, notesColumn),
                Favorite = favorite is "1" or "true",
                Addresses = addressCell.Length == 0
                    ? []
                    : addressesAreCommaSeparated ? addressCell.Split(',') : [addressCell],
                Totp = Cell(values, totpColumn),
                Folder = Cell(values, folderColumn),
                CustomFields = ReadCustomFields(Cell(values, customFieldColumn)),
                Problem = HasValueBeyond(values, columnCount) ? ImportReason.RowHasExtraValues : null,
            });
        }

        return new ParsedImport(ImportFormat.Csv, label, entries, records, unsupported, inTrash: 0, ignoredColumns);
    }

    /// <summary>
    /// Whether a record has nothing in it: every value is empty, or is nothing but the characters
    /// that trimming removes. Such a record is not a row; an empty line is the usual one.
    /// </summary>
    private static bool IsBlankRecord(List<string> values)
    {
        foreach (string value in values)
        {
            if (!ImportText.IsBlank(value))
            {
                return false;
            }
        }

        return true;
    }

    private static int Find(Dictionary<string, int> firstColumnNamed, string[] names, out string? found)
    {
        foreach (string name in names)
        {
            if (firstColumnNamed.TryGetValue(name, out int column))
            {
                found = name;
                return column;
            }
        }

        found = null;
        return -1;
    }

    private static string LabelFor(string[] header)
    {
        foreach ((string label, string[] known) in KnownHeaders)
        {
            if (header.AsSpan().SequenceEqual(known))
            {
                return label;
            }
        }

        return ImportTexts.LabelCsv;
    }

    private static string Cell(List<string> values, int column)
        => column >= 0 && column < values.Count ? values[column] : string.Empty;

    /// <summary>
    /// Whether the record has a value that is not empty to the right of the last column the header
    /// names. That is what a comma inside a value that was not quoted looks like: the values after
    /// it have moved one column to the right, and the password may be half of what it was.
    /// </summary>
    private static bool HasValueBeyond(List<string> values, int columnCount)
    {
        for (int i = columnCount; i < values.Count; i++)
        {
            if (values[i].Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The custom fields of a Bitwarden row: one on each line of the cell, as <c>name: value</c>,
    /// split at the last ": " as Bitwarden's own importer does. A line without one is kept as a
    /// field without a name rather than dropped.
    /// </summary>
    private static List<ImportCustomField> ReadCustomFields(string cell)
    {
        if (cell.Length == 0)
        {
            return [];
        }

        var fields = new List<ImportCustomField>();
        foreach (string line in ImportText.SplitLines(cell))
        {
            if (ImportText.IsBlank(line))
            {
                continue;
            }

            int separator = line.LastIndexOf(": ", StringComparison.Ordinal);
            fields.Add(separator < 0
                ? new ImportCustomField(string.Empty, line)
                : new ImportCustomField(ImportText.Trim(line[..separator]), line[(separator + 2)..]));
        }

        return fields;
    }
}
