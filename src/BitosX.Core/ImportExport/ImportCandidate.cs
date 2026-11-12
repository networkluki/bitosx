namespace BitosX.Core.ImportExport;

/// <summary>What an entry holds that BitosX has no field for.</summary>
public sealed class ImportExtras
{
    internal static readonly ImportExtras None = new(string.Empty, string.Empty, [], []);

    internal ImportExtras(
        string totp, string folder, IReadOnlyList<string> otherAddresses, IReadOnlyList<ImportCustomField> customFields)
    {
        Totp = totp;
        Folder = folder;
        OtherAddresses = otherAddresses;
        CustomFields = customFields;
    }

    /// <summary>The authenticator (TOTP) secret, trimmed, or empty.</summary>
    public string Totp { get; }

    /// <summary>The folder or group the entry is in, trimmed, or empty.</summary>
    public string Folder { get; }

    /// <summary>The addresses that are not http or https addresses, trimmed, in the order of the file.</summary>
    public IReadOnlyList<string> OtherAddresses { get; }

    /// <summary>The custom fields that have a value, in the order of the file.</summary>
    public IReadOnlyList<ImportCustomField> CustomFields { get; }

    /// <summary>How many extras of <paramref name="kind"/> there are.</summary>
    public int Count(ImportExtraKind kind) => kind switch
    {
        ImportExtraKind.Totp => Totp.Length > 0 ? 1 : 0,
        ImportExtraKind.Folder => Folder.Length > 0 ? 1 : 0,
        ImportExtraKind.OtherAddress => OtherAddresses.Count,
        ImportExtraKind.CustomField => CustomFields.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Whether there is anything besides the name of a folder.</summary>
    internal bool HasContent => Totp.Length > 0 || OtherAddresses.Count > 0 || CustomFields.Count > 0;
}

/// <summary>
/// One login of a file, as it would be stored: normalised, checked against the limits of the vault,
/// and compared with what the vault holds. Holds secrets. A preview shows the name and the reason,
/// and never <see cref="Password"/>, the values of <see cref="Extras"/> or <see cref="Notes"/>.
/// </summary>
public sealed class ImportCandidate
{
    internal ImportCandidate(
        int position,
        string name,
        string username,
        string password,
        IReadOnlyList<string> urls,
        string notes,
        bool favorite,
        ImportExtras extras,
        bool nameWasDerived,
        int httpsAdded,
        ImportReason? reason,
        Domain.TotpKey? totp = null,
        Authenticator.TotpRefusal? totpRefusal = null)
    {
        Totp = totp;
        TotpRefusal = totpRefusal;
        Position = position;
        Name = name;
        Username = username;
        Password = password;
        Urls = urls;
        Notes = notes;
        Favorite = favorite;
        Extras = extras;
        NameWasDerived = nameWasDerived;
        HttpsAdded = httpsAdded;
        Reason = reason;
        Status = reason is null ? ImportStatus.Add : ImportStatus.NotImported;
    }

    /// <summary>Which record of the file this is, counted from 1 (see <see cref="ImportEntry.Position"/>).</summary>
    public int Position { get; }

    /// <summary>The name: trimmed, or derived if the file gave none.</summary>
    public string Name { get; }

    /// <summary>The username, exactly as it is in the file.</summary>
    public string Username { get; }

    /// <summary>The password, exactly as it is in the file.</summary>
    public string Password { get; }

    /// <summary>The web addresses, in the order of the file.</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>
    /// The notes as they would be stored: those of the file, and after them the extras if they are
    /// copied into the notes.
    /// </summary>
    public string Notes { get; }

    public bool Favorite { get; }

    /// <summary>What the entry holds that BitosX has no field for.</summary>
    public ImportExtras Extras { get; }

    /// <summary>The authenticator key the item gets, if the entry has one that could be read.</summary>
    public Domain.TotpKey? Totp { get; }

    /// <summary>
    /// Why the entry's authenticator key could not be read, if it has one that could not. The text
    /// is then an extra (<see cref="ImportExtras.Totp"/>), or, from a KeePass file, custom fields.
    /// </summary>
    public Authenticator.TotpRefusal? TotpRefusal { get; }

    /// <summary>Whether the file gave no name, so that one was taken from the address or the username.</summary>
    public bool NameWasDerived { get; }

    /// <summary>How many of <see cref="Urls"/> were given <c>https://</c> in front.</summary>
    public int HttpsAdded { get; }

    /// <summary>What becomes of the entry.</summary>
    public ImportStatus Status { get; internal set; }

    /// <summary>
    /// Whether the entry equals an item of the vault or an earlier entry of the file. Such an entry
    /// has <see cref="ImportStatus.Duplicate"/>, unless duplicates are imported too.
    /// </summary>
    public bool IsDuplicate { get; internal set; }

    /// <summary>Why the entry is not imported, if it is not.</summary>
    public ImportReason? Reason { get; }
}

/// <summary>The two choices a person has when importing. Both are off unless chosen.</summary>
/// <param name="CopyExtrasToNotes">Copy what BitosX has no field for into each item's notes.</param>
/// <param name="ImportDuplicates">Import duplicates too.</param>
public sealed record ImportOptions(bool CopyExtrasToNotes = false, bool ImportDuplicates = false)
{
    /// <summary>Extras are left out, and duplicates are skipped.</summary>
    public static ImportOptions Default { get; } = new();
}
