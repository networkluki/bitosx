namespace BitosX.Core.ImportExport;

/// <summary>The formats an import file can be in. Detected from the content, never from the file name.</summary>
public enum ImportFormat
{
    /// <summary>Comma-separated values with a header line.</summary>
    Csv = 0,

    /// <summary>Bitwarden's unencrypted JSON export.</summary>
    BitwardenJson = 1,

    /// <summary>KeePass 2 XML, as KeePass 2 and KeePassXC export it.</summary>
    KeePassXml = 2,

    /// <summary>BitosX's own unencrypted JSON export.</summary>
    BitosXJson = 3,
}

/// <summary>Why one entry of a file is not imported. Each reason has a stable code and a fixed text.</summary>
public enum ImportReason
{
    /// <summary>A CSV row with a value beyond the last column of the header.</summary>
    RowHasExtraValues = 0,

    /// <summary>A KeePass entry with one of its five standard fields more than once.</summary>
    FieldTwice = 1,

    /// <summary>The name is longer than the vault allows.</summary>
    NameTooLong = 2,

    /// <summary>The username is longer than the vault allows.</summary>
    UsernameTooLong = 3,

    /// <summary>The password is longer than the vault allows.</summary>
    PasswordTooLong = 4,

    /// <summary>The notes are longer than the vault allows.</summary>
    NotesTooLong = 5,

    /// <summary>More web addresses than an item may have.</summary>
    TooManyUrls = 6,

    /// <summary>A web address is longer than the vault allows.</summary>
    UrlTooLong = 7,

    /// <summary>A web address that the vault's rule for addresses does not accept.</summary>
    UrlInvalid = 8,

    /// <summary>The notes are within the limit, but not with the extras copied into them.</summary>
    NotesTooLongWithExtras = 9,
}

/// <summary>What a file can hold that BitosX has no field for.</summary>
public enum ImportExtraKind
{
    /// <summary>An authenticator (TOTP) secret.</summary>
    Totp = 0,

    /// <summary>The name of the folder or group an entry is in.</summary>
    Folder = 1,

    /// <summary>An address that is not an http or https address.</summary>
    OtherAddress = 2,

    /// <summary>A custom field.</summary>
    CustomField = 3,
}

/// <summary>The kinds of item a file can hold that are not logins. They are counted, not imported.</summary>
public enum ImportUnsupportedKind
{
    SecureNote = 0,
    Card = 1,
    Identity = 2,
    SshKey = 3,
    BankAccount = 4,
    DriversLicense = 5,
    Passport = 6,

    /// <summary>Anything else that is not a login.</summary>
    Other = 7,
}

/// <summary>What becomes of one entry of a file.</summary>
public enum ImportStatus
{
    /// <summary>It will be added to the vault.</summary>
    Add = 0,

    /// <summary>It equals an item of the vault or an earlier entry of the file, and is left out.</summary>
    Duplicate = 1,

    /// <summary>It breaks a limit of the vault and is listed with its reason.</summary>
    NotImported = 2,
}
