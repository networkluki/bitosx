namespace BitosX.Core.ImportExport;

/// <summary>A custom field of an entry: something BitosX has no field for.</summary>
/// <remarks>
/// A class rather than a record on purpose, like everything here that holds a value from a file: a
/// record prints all of its members when it is written to a log or shown in a debugger.
/// </remarks>
public sealed class ImportCustomField
{
    public ImportCustomField(string name, string value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The field's name, trimmed. May be empty.</summary>
    public string Name { get; }

    /// <summary>The field's value, as it is in the file.</summary>
    public string Value { get; }
}

/// <summary>
/// One login as a file holds it, before anything is normalised: every value is as it was read.
/// Holds secrets; it exists only in memory, between reading a file and adding its items.
/// </summary>
public sealed class ImportEntry
{
    /// <summary>
    /// Which record of the file this is, counted from 1 over every record the file holds, whatever
    /// its kind: the rows of a CSV file below the header, the elements of <c>items</c>, the entries
    /// of a KeePass file in document order.
    /// </summary>
    public required int Position { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public bool Favorite { get; init; }

    /// <summary>
    /// The address values of the entry, already split the way its format splits a cell that holds
    /// several. Not yet trimmed, and not yet told apart into web addresses and others.
    /// </summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The authenticator (TOTP) secret, or empty.</summary>
    public string Totp { get; init; } = string.Empty;

    /// <summary>The folder or group the entry is in, or empty.</summary>
    public string Folder { get; init; } = string.Empty;

    public IReadOnlyList<ImportCustomField> CustomFields { get; init; } = [];

    /// <summary>
    /// What is wrong with how the entry is written in the file, if anything: such an entry is not
    /// imported, whatever its values are.
    /// </summary>
    public ImportReason? Problem { get; init; }
}

/// <summary>
/// A file that was read: its format, the logins it holds, and what else was in it. Nothing has been
/// compared with a vault yet (<see cref="ImportPlanner"/> does that).
/// </summary>
public sealed class ParsedImport
{
    internal ParsedImport(
        ImportFormat format,
        string label,
        IReadOnlyList<ImportEntry> entries,
        int recordCount,
        IReadOnlyDictionary<ImportUnsupportedKind, int> unsupported,
        int inTrash,
        IReadOnlyList<string> ignoredColumns)
    {
        Format = format;
        Label = label;
        Entries = entries;
        RecordCount = recordCount;
        Unsupported = unsupported;
        InTrash = inTrash;
        IgnoredColumns = ignoredColumns;
    }

    /// <summary>The format, as detected from the content.</summary>
    public ImportFormat Format { get; }

    /// <summary>What the file is called in the preview, for example "Firefox (CSV)". Informational.</summary>
    public string Label { get; }

    /// <summary>The logins of the file, in the order they are in it.</summary>
    public IReadOnlyList<ImportEntry> Entries { get; }

    /// <summary>How many records the file holds in all: logins, items of other kinds, and deleted ones.</summary>
    public int RecordCount { get; }

    /// <summary>How many items of each kind that is not a login the file holds. Kinds with none are absent.</summary>
    public IReadOnlyDictionary<ImportUnsupportedKind, int> Unsupported { get; }

    /// <summary>How many records are in the trash of a Bitwarden export or the recycle bin of a KeePass file.</summary>
    public int InTrash { get; }

    /// <summary>
    /// The columns of a CSV file that are not read, by the name the header gives them, in the order
    /// of the header. Empty for the other formats.
    /// </summary>
    public IReadOnlyList<string> IgnoredColumns { get; }
}
