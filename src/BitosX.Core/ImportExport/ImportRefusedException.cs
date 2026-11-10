namespace BitosX.Core.ImportExport;

/// <summary>Why a file was refused as a whole. Each kind has a stable code and a fixed text.</summary>
public enum ImportRefusal
{
    /// <summary>The file is larger than <see cref="ImportLimits.MaxFileBytes"/>.</summary>
    TooLarge = 0,

    /// <summary>The file has no content.</summary>
    Empty = 1,

    /// <summary>The file starts with a UTF-16 or UTF-32 byte order mark.</summary>
    UnsupportedEncoding = 2,

    /// <summary>The file is not valid UTF-8.</summary>
    InvalidUtf8 = 3,

    /// <summary>The content is none of the formats that are read.</summary>
    NotRecognised = 4,

    /// <summary>A quoted value is not closed, or is followed by something other than a comma or a line end.</summary>
    InvalidCsv = 5,

    /// <summary>The header has no password column, or none of name, url and username.</summary>
    NoRecognisedColumns = 6,

    /// <summary>Not JSON, nested too deeply, a name twice in one object, or an unpaired surrogate.</summary>
    InvalidJson = 7,

    /// <summary>A Bitwarden export that is encrypted or protected by a password.</summary>
    EncryptedExport = 8,

    /// <summary>A BitosX export of a version this build does not know.</summary>
    UnsupportedVersion = 9,

    /// <summary>Not well-formed XML.</summary>
    InvalidXml = 10,

    /// <summary>The XML has a document type declaration.</summary>
    Doctype = 11,

    /// <summary>A KeePass file whose values are still encrypted.</summary>
    ProtectedValues = 12,

    /// <summary>Groups or elements are nested deeper than the limits allow.</summary>
    NestedTooDeeply = 13,

    /// <summary>The file holds more than <see cref="ImportLimits.MaxEntries"/> entries.</summary>
    TooManyEntries = 14,

    /// <summary>The vault would hold more than <see cref="ImportLimits.MaxVaultItems"/> items after the import.</summary>
    VaultItemLimit = 15,
}

/// <summary>
/// A file that is not imported at all, with the reason as one of a fixed set of kinds. The message is
/// one of the fixed texts of docs/IMPORT_EXPORT.md: it never quotes anything from the file, so it can
/// be shown and logged.
/// </summary>
public sealed class ImportRefusedException : Exception
{
    public ImportRefusedException(ImportRefusal kind)
        : this(kind, line: null)
    {
    }

    /// <param name="kind">Why the file is refused.</param>
    /// <param name="line">For <see cref="ImportRefusal.InvalidCsv"/>: the line the fault is on, counted from 1.</param>
    public ImportRefusedException(ImportRefusal kind, int? line)
        : base(ImportTexts.Text(kind))
    {
        Kind = kind;
        Line = line;
    }

    /// <summary>Why the file is refused.</summary>
    public ImportRefusal Kind { get; }

    /// <summary>The stable code of <see cref="Kind"/>, as the conformance fixtures spell it.</summary>
    public string Code => ImportTexts.Code(Kind);

    /// <summary>For a damaged CSV file: the line the fault is on, counted from 1. Otherwise <see langword="null"/>.</summary>
    public int? Line { get; }
}
