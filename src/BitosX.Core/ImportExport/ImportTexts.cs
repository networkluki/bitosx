namespace BitosX.Core.ImportExport;

/// <summary>
/// The stable codes and the fixed texts of an import: what the conformance fixtures spell, and what
/// a person is told. Both clients use the same ones (docs/IMPORT_EXPORT.md). No text quotes anything
/// from a file.
/// </summary>
public static class ImportTexts
{
    /// <summary>The name an entry gets when it has none and none can be derived.</summary>
    public const string UntitledName = "Untitled";

    // ---- What a person is asked and told, in the same words in both clients ----

    /// <summary>The choice to copy the extras of an entry into its notes. Off unless chosen.</summary>
    public const string OptionCopyExtrasToNotes = "Copy what BitosX has no field for into each item's notes";

    /// <summary>The choice to import duplicates as well. Off unless chosen.</summary>
    public const string OptionImportDuplicates = "Import duplicates too";

    /// <summary>What a person is reminded of once an import is done.</summary>
    public const string FileStillHoldsPasswords =
        "The file you imported still holds every password in plain text. Delete it when you no longer need it.";

    // ---- Labels ----

    public const string LabelCsv = "CSV";
    public const string LabelChromeCsv = "Chrome or Edge (CSV)";
    public const string LabelFirefoxCsv = "Firefox (CSV)";
    public const string LabelBitwardenCsv = "Bitwarden (CSV)";
    public const string LabelKeePassXcCsv = "KeePassXC (CSV)";
    public const string LabelKeePass1Csv = "KeePass 1.x (CSV)";
    public const string LabelBitwardenJson = "Bitwarden (JSON)";
    public const string LabelKeePassXml = "KeePass 2 (XML)";
    public const string LabelBitosXJson = "BitosX (JSON)";

    // ---- The lines extras are copied into the notes with ----

    public const string NotesLabelTotp = "Authenticator key (TOTP)";
    public const string NotesLabelFolder = "Folder";
    public const string NotesLabelOtherAddress = "Other address";

    /// <summary>The label of a custom field that has no name.</summary>
    public const string NotesLabelNamelessField = "Field";

    /// <summary>The stable code of a format.</summary>
    public static string Code(ImportFormat format) => format switch
    {
        ImportFormat.Csv => "csv",
        ImportFormat.BitwardenJson => "bitwarden_json",
        ImportFormat.KeePassXml => "keepass_xml",
        ImportFormat.BitosXJson => "bitosx_json",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The stable code of a refusal.</summary>
    public static string Code(ImportRefusal refusal) => refusal switch
    {
        ImportRefusal.TooLarge => "too_large",
        ImportRefusal.Empty => "empty",
        ImportRefusal.UnsupportedEncoding => "unsupported_encoding",
        ImportRefusal.InvalidUtf8 => "invalid_utf8",
        ImportRefusal.NotRecognised => "not_recognised",
        ImportRefusal.InvalidCsv => "invalid_csv",
        ImportRefusal.NoRecognisedColumns => "no_recognised_columns",
        ImportRefusal.InvalidJson => "invalid_json",
        ImportRefusal.EncryptedExport => "encrypted_export",
        ImportRefusal.UnsupportedVersion => "unsupported_version",
        ImportRefusal.InvalidXml => "invalid_xml",
        ImportRefusal.Doctype => "doctype",
        ImportRefusal.ProtectedValues => "protected_values",
        ImportRefusal.NestedTooDeeply => "nested_too_deeply",
        ImportRefusal.TooManyEntries => "too_many_entries",
        ImportRefusal.VaultItemLimit => "vault_item_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>What a person is told when a file is refused.</summary>
    public static string Text(ImportRefusal refusal) => refusal switch
    {
        ImportRefusal.TooLarge => "The file is larger than 16 MiB. BitosX does not import files that large.",
        ImportRefusal.Empty => "The file is empty.",
        ImportRefusal.UnsupportedEncoding =>
            "The file is saved as UTF-16 or UTF-32 text. BitosX reads UTF-8: export the file again, or save it as UTF-8, and import that.",
        ImportRefusal.InvalidUtf8 =>
            "The file is not UTF-8 text. BitosX reads exports saved as CSV, JSON or XML text in UTF-8; it cannot read a database file, or a file that is encrypted or compressed.",
        ImportRefusal.NotRecognised =>
            "BitosX does not recognise this file. It reads CSV files, Bitwarden's unencrypted JSON export, KeePass 2 XML and its own JSON export.",
        ImportRefusal.InvalidCsv =>
            "The CSV file is damaged: a value in quotes is not closed, or is followed by something other than a comma or the end of the line.",
        ImportRefusal.NoRecognisedColumns =>
            "The file has no columns BitosX recognises. Its first line has to name a password column and at least one of name, url and username.",
        ImportRefusal.InvalidJson =>
            "The file is not JSON that BitosX can read: it is damaged, nested too deeply, uses a name twice in one object, or holds an incomplete character.",
        ImportRefusal.EncryptedExport =>
            "This Bitwarden export is encrypted. Export the vault from Bitwarden again as an unencrypted .json file, and import that.",
        ImportRefusal.UnsupportedVersion =>
            "This BitosX export was written by a newer version of BitosX. Update BitosX, and import the file again.",
        ImportRefusal.InvalidXml => "The file is not XML that BitosX can read: it is damaged or incomplete.",
        ImportRefusal.Doctype =>
            "The file contains a document type declaration (DOCTYPE). BitosX does not read XML files that have one.",
        ImportRefusal.ProtectedValues =>
            "The file holds values that are still encrypted (marked as protected). Export the database from KeePass as plain XML, and import that.",
        ImportRefusal.NestedTooDeeply => "The file nests its groups or elements more deeply than BitosX reads.",
        ImportRefusal.TooManyEntries =>
            "The file holds more than 10,000 entries. BitosX imports at most 10,000 at a time: split the file, and import the parts.",
        ImportRefusal.VaultItemLimit =>
            "The vault would hold more than 10,000 items after this import, which is the most a vault can hold. Nothing was imported.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>The stable code of the reason an entry is not imported.</summary>
    public static string Code(ImportReason reason) => reason switch
    {
        ImportReason.RowHasExtraValues => "row_has_extra_values",
        ImportReason.FieldTwice => "field_twice",
        ImportReason.NameTooLong => "name_too_long",
        ImportReason.UsernameTooLong => "username_too_long",
        ImportReason.PasswordTooLong => "password_too_long",
        ImportReason.NotesTooLong => "notes_too_long",
        ImportReason.TooManyUrls => "too_many_urls",
        ImportReason.UrlTooLong => "url_too_long",
        ImportReason.UrlInvalid => "url_invalid",
        ImportReason.NotesTooLongWithExtras => "notes_too_long_with_extras",
        ImportReason.TotpInvalid => "totp_invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>What a person is told about an entry that is not imported.</summary>
    public static string Text(ImportReason reason) => reason switch
    {
        ImportReason.RowHasExtraValues => "The row has more values than the file has columns.",
        ImportReason.FieldTwice => "The entry has the same field more than once.",
        ImportReason.NameTooLong => "The name is longer than 200 characters.",
        ImportReason.UsernameTooLong => "The username is longer than 320 characters.",
        ImportReason.PasswordTooLong => "The password is longer than 4096 characters.",
        ImportReason.NotesTooLong => "The notes are longer than 16384 characters.",
        ImportReason.TooManyUrls => "The entry has more than 64 web addresses.",
        ImportReason.UrlTooLong => "A web address is longer than 2048 characters.",
        ImportReason.UrlInvalid => "A web address is not a valid http or https address.",
        ImportReason.NotesTooLongWithExtras =>
            "With the fields BitosX has no place for copied into them, the notes would be longer than 16384 characters.",
        ImportReason.TotpInvalid => "The authenticator key in the file is not valid.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The stable code of a kind of extra.</summary>
    public static string Code(ImportExtraKind kind) => kind switch
    {
        ImportExtraKind.Totp => "totp",
        ImportExtraKind.Folder => "folder",
        ImportExtraKind.OtherAddress => "other_address",
        ImportExtraKind.CustomField => "custom_field",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>What a kind of extra is called, in the plural, in a sentence.</summary>
    public static string Text(ImportExtraKind kind) => kind switch
    {
        ImportExtraKind.Totp => "authenticator keys that could not be read",
        ImportExtraKind.Folder => "folder or group names",
        ImportExtraKind.OtherAddress => "addresses that are not web addresses",
        ImportExtraKind.CustomField => "custom fields",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The stable code of a kind of item that is not imported.</summary>
    public static string Code(ImportUnsupportedKind kind) => kind switch
    {
        ImportUnsupportedKind.SecureNote => "secure_note",
        ImportUnsupportedKind.Card => "card",
        ImportUnsupportedKind.Identity => "identity",
        ImportUnsupportedKind.SshKey => "ssh_key",
        ImportUnsupportedKind.BankAccount => "bank_account",
        ImportUnsupportedKind.DriversLicense => "drivers_license",
        ImportUnsupportedKind.Passport => "passport",
        ImportUnsupportedKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>What a kind of item that is not imported is called, in the plural, in a sentence.</summary>
    public static string Text(ImportUnsupportedKind kind) => kind switch
    {
        ImportUnsupportedKind.SecureNote => "secure notes",
        ImportUnsupportedKind.Card => "cards",
        ImportUnsupportedKind.Identity => "identities",
        ImportUnsupportedKind.SshKey => "SSH keys",
        ImportUnsupportedKind.BankAccount => "bank accounts",
        ImportUnsupportedKind.DriversLicense => "driver's licences",
        ImportUnsupportedKind.Passport => "passports",
        ImportUnsupportedKind.Other => "other items",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The stable code of what becomes of an entry.</summary>
    public static string Code(ImportStatus status) => status switch
    {
        ImportStatus.Add => "add",
        ImportStatus.Duplicate => "duplicate",
        ImportStatus.NotImported => "not_imported",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
