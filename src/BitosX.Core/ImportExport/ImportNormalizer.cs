using System.Text;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Validation;

namespace BitosX.Core.ImportExport;

/// <summary>
/// Turns an entry as a file holds it into the item it would be stored as, and says why it cannot be
/// stored if it cannot. Nothing is ever cut short to make it fit: an entry that breaks a limit of
/// the vault is not imported.
/// </summary>
internal static class ImportNormalizer
{
    /// <summary>
    /// Normalises <paramref name="entry"/>. Returns <see langword="null"/> for an entry with nothing
    /// in it, which is counted and otherwise passed over.
    /// </summary>
    public static ImportCandidate? Normalize(ImportEntry entry, bool copyExtrasToNotes)
    {
        string name = ImportText.Trim(entry.Name);

        // Addresses: one value may hold several, one on each line. Each is trimmed, empty ones are
        // dropped, and each is then a web address, a host to put https:// in front of, or neither.
        var urls = new List<string>();
        var otherAddresses = new List<string>();
        int httpsAdded = 0;
        foreach (string value in entry.Addresses)
        {
            foreach (string line in ImportText.SplitLines(value))
            {
                string address = ImportText.Trim(line);
                if (address.Length == 0)
                {
                    continue;
                }

                switch (ImportAddresses.Classify(address))
                {
                    case AddressKind.Web:
                        urls.Add(address);
                        break;
                    case AddressKind.HostWithoutScheme:
                        urls.Add(ImportAddresses.WithHttps(address));
                        httpsAdded++;
                        break;
                    default:
                        otherAddresses.Add(address);
                        break;
                }
            }
        }

        // A custom field without a value has nothing that could be lost.
        List<ImportCustomField>? customFields = null;
        foreach (ImportCustomField field in entry.CustomFields)
        {
            if (!ImportText.IsBlank(field.Value))
            {
                (customFields ??= []).Add(field);
            }
        }

        // The authenticator key: read already, or read now from its text (docs/TOTP.md §3). Text that
        // is not a key stays an extra, as it was before keys were stored.
        TotpKey? totp = entry.TotpKey;
        TotpRefusal? totpRefusal = entry.TotpRefusal;
        string totpText = string.Empty;
        if (totp is null && entry.Totp.Length > 0)
        {
            TotpParseResult parsed = TotpKeyParser.Parse(entry.Totp);
            totp = parsed.Key;
            if (parsed.Refusal is { } refusal)
            {
                totpRefusal = refusal;
                totpText = ImportText.Trim(entry.Totp);
            }
        }

        var extras = new ImportExtras(totpText, ImportText.Trim(entry.Folder), otherAddresses, customFields ?? []);

        if (name.Length == 0
            && urls.Count == 0
            && totp is null
            && !extras.HasContent
            && ImportText.IsBlank(entry.Username)
            && ImportText.IsBlank(entry.Password)
            && ImportText.IsBlank(entry.Notes))
        {
            return null; // nothing but, at most, the name of a folder
        }

        bool nameWasDerived = false;
        if (name.Length == 0)
        {
            name = DeriveName(urls, entry.Username);
            nameWasDerived = true;
        }

        ImportReason? reason = entry.Problem ?? CheckLimits(name, entry, urls);

        string notes = entry.Notes;
        if (copyExtrasToNotes)
        {
            notes = WithExtras(notes, extras);
            if (reason is null && notes.Length > VaultValidation.MaxNotesLength)
            {
                reason = ImportReason.NotesTooLongWithExtras;
            }
        }

        if (reason is null && entry.TotpInvalid)
        {
            reason = ImportReason.TotpInvalid;
        }

        return new ImportCandidate(
            entry.Position, name, entry.Username, entry.Password, urls, notes, entry.Favorite, extras, nameWasDerived, httpsAdded, reason,
            totp, totpRefusal);
    }

    /// <summary>
    /// The name of an entry that has none: the host of its first web address, else its username,
    /// else "Untitled". A candidate is passed over if it is empty or longer than a name may be.
    /// </summary>
    private static string DeriveName(List<string> urls, string username)
    {
        if (urls.Count > 0)
        {
            string host = ImportText.Trim(ImportAddresses.HostOf(urls[0]));
            if (host.Length is > 0 and <= VaultValidation.MaxNameLength)
            {
                return host;
            }
        }

        string fromUsername = ImportText.Trim(username);
        return fromUsername.Length is > 0 and <= VaultValidation.MaxNameLength ? fromUsername : ImportTexts.UntitledName;
    }

    /// <summary>The first limit of the vault the entry breaks, in this order, or none.</summary>
    private static ImportReason? CheckLimits(string name, ImportEntry entry, List<string> urls)
    {
        if (name.Length > VaultValidation.MaxNameLength)
        {
            return ImportReason.NameTooLong;
        }

        if (entry.Username.Length > VaultValidation.MaxUsernameLength)
        {
            return ImportReason.UsernameTooLong;
        }

        if (entry.Password.Length > VaultValidation.MaxPasswordLength)
        {
            return ImportReason.PasswordTooLong;
        }

        if (entry.Notes.Length > VaultValidation.MaxNotesLength)
        {
            return ImportReason.NotesTooLong;
        }

        if (urls.Count > VaultValidation.MaxUrlCount)
        {
            return ImportReason.TooManyUrls;
        }

        foreach (string url in urls)
        {
            if (url.Length > VaultValidation.MaxUrlLength)
            {
                return ImportReason.UrlTooLong;
            }
        }

        foreach (string url in urls)
        {
            // First the rule both clients share, then the vault's own.
            if (!ImportAddresses.HasPlausibleHost(url) || !VaultValidation.IsValidUrl(url))
            {
                return ImportReason.UrlInvalid;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="notes"/> with the extras after them, one on each line with a label: the
    /// authenticator key, the folder, the other addresses, the custom fields. An empty line stands
    /// between the notes of the file and the first of them.
    /// </summary>
    private static string WithExtras(string notes, ImportExtras extras)
    {
        if (!extras.HasContent && extras.Folder.Length == 0)
        {
            return notes;
        }

        var text = new StringBuilder(notes);
        if (notes.Length > 0)
        {
            text.Append("\n\n");
        }

        int start = text.Length;
        if (extras.Totp.Length > 0)
        {
            AppendLine(text, start, ImportTexts.NotesLabelTotp, extras.Totp);
        }

        if (extras.Folder.Length > 0)
        {
            AppendLine(text, start, ImportTexts.NotesLabelFolder, extras.Folder);
        }

        foreach (string address in extras.OtherAddresses)
        {
            AppendLine(text, start, ImportTexts.NotesLabelOtherAddress, address);
        }

        foreach (ImportCustomField field in extras.CustomFields)
        {
            AppendLine(text, start, field.Name.Length > 0 ? field.Name : ImportTexts.NotesLabelNamelessField, field.Value);
        }

        return text.ToString();
    }

    private static void AppendLine(StringBuilder text, int start, string label, string value)
    {
        if (text.Length > start)
        {
            text.Append('\n');
        }

        text.Append(label).Append(": ").Append(value);
    }
}
