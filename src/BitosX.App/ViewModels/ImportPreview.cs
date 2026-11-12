using System.Globalization;
using System.Text;
using BitosX.Core.ImportExport;

namespace BitosX.App.ViewModels;

/// <summary>
/// What the preview of an import shows: sentences with counts, and for each entry that cannot be
/// imported its position, its name and the reason.
/// </summary>
/// <remarks>
/// It is made from a plan and keeps nothing of it. No password, no authenticator key, no notes, no
/// address and no value of a custom field is ever copied here: only counts, the names of entries
/// that are not imported, the names of columns that are not read, and fixed texts. So whatever is
/// bound to the page can show a secret of the file to no one, whoever looks at the screen or reads
/// it through the accessibility interface.
/// <para>
/// A name comes from the file, which anyone may have written: it is shown on one line, with its
/// control characters replaced by spaces, and cut to a length that fits the page. Each name is a
/// text block of its own, so nothing in it can rearrange the words next to it.
/// </para>
/// </remarks>
public sealed class ImportPreview
{
    /// <summary>How many entries that cannot be imported are listed by name; the rest are counted.</summary>
    public const int MaxListedEntries = 200;

    /// <summary>How many characters of a name from the file are shown.</summary>
    public const int MaxNameLength = 80;

    /// <summary>How many ignored columns are named, and how many characters of each name are shown.</summary>
    public const int MaxListedColumns = 10;

    /// <summary>How many characters of the name of an ignored column are shown.</summary>
    public const int MaxColumnNameLength = 40;

    private ImportPreview(ImportPlan plan)
    {
        ParsedImport file = plan.Source;
        bool overLimit = plan.ExceedsVaultItemLimit;

        Source = string.Create(Culture, $"{file.Label}: {Count(file.RecordCount, "entry", "entries")} in the file.");

        if (plan.ToAdd == 0)
        {
            ToAdd = "Nothing in this file would be added to the vault.";
        }
        else if (overLimit)
        {
            ToAdd = string.Create(
                Culture,
                $"{Count(plan.ToAdd, "login is", "logins are")} in this file to add. The vault holds {Count(plan.VaultItemsBefore, "item", "items")} now and would hold {Number(plan.VaultItemsAfter)}.");
        }
        else
        {
            ToAdd = string.Create(
                Culture,
                $"{Count(plan.ToAdd, "login", "logins")} will be added. The vault holds {Count(plan.VaultItemsBefore, "item", "items")} now and will hold {Number(plan.VaultItemsAfter)}.");
        }

        OverLimit = overLimit ? ImportTexts.Text(ImportRefusal.VaultItemLimit) : null;

        if (plan.Duplicates > 0)
        {
            string what = Count(plan.Duplicates, "login is", "logins are") + " already in the vault, or in the file more than once";
            Duplicates = plan.Options.ImportDuplicates
                ? what + (plan.Duplicates == 1 ? ". It is added as well." : ". They are added as well.")
                : what + (plan.Duplicates == 1 ? ", and is skipped." : ", and are skipped.");
        }

        if (plan.NotImported.Count > 0)
        {
            NotImported = Count(plan.NotImported.Count, "entry", "entries") + " cannot be imported.";
            var listed = new List<ImportPreviewEntry>(Math.Min(plan.NotImported.Count, MaxListedEntries));
            foreach (ImportCandidate candidate in plan.NotImported)
            {
                if (listed.Count == MaxListedEntries)
                {
                    break;
                }

                // A row whose values moved by a column cannot say which of them is its name: any of
                // them, the password included, may stand where the name should. It is listed by its
                // position alone.
                string name = candidate.Reason == ImportReason.RowHasExtraValues
                    ? string.Empty
                    : OnOneLine(candidate.Name, MaxNameLength);

                listed.Add(new ImportPreviewEntry(
                    string.Create(Culture, $"Entry {Number(candidate.Position)}"),
                    name,
                    candidate.Reason is { } reason ? ImportTexts.Text(reason) : string.Empty));
            }

            NotImportedEntries = listed;
            int more = plan.NotImported.Count - listed.Count;
            NotImportedMore = more > 0 ? string.Create(Culture, $"… and {Number(more)} more.") : null;
        }

        if (file.Unsupported.Count > 0)
        {
            Unsupported = "BitosX stores logins only. Not imported — "
                + string.Join("; ", Enum.GetValues<ImportUnsupportedKind>()
                    .Where(file.Unsupported.ContainsKey)
                    .Select(kind => string.Create(Culture, $"{ImportTexts.Text(kind)}: {Number(file.Unsupported[kind])}")))
                + ".";
        }

        if (file.InTrash > 0)
        {
            InTrash = file.InTrash == 1
                ? "1 entry is in the trash or recycle bin of the file and is not imported."
                : string.Create(Culture, $"{Number(file.InTrash)} entries are in the trash or recycle bin of the file and are not imported.");
        }

        if (plan.EmptyEntries > 0)
        {
            Empty = plan.EmptyEntries == 1
                ? "1 entry has nothing in it and is passed over."
                : string.Create(Culture, $"{Number(plan.EmptyEntries)} entries have nothing in them and are passed over.");
        }

        if (plan.TotpKeys > 0)
        {
            AuthenticatorKeys = plan.TotpKeys == 1
                ? "1 login brings an authenticator key, which is imported with it."
                : string.Create(Culture, $"{Number(plan.TotpKeys)} logins bring an authenticator key, which is imported with them.");
        }

        if (plan.TotpUnreadable > 0)
        {
            // Said here rather than among the extras below: from a KeePass file such a key is in
            // custom fields, which the extras count as such.
            bool one = plan.TotpUnreadable == 1;
            UnreadableKeys = (one
                    ? "1 authenticator key could not be read."
                    : string.Create(Culture, $"{Number(plan.TotpUnreadable)} authenticator keys could not be read."))
                + (plan.Options.CopyExtrasToNotes
                    ? (one ? " Its text is copied into the notes." : " Their text is copied into the notes.")
                    : (one ? " It is" : " They are") + " left out, unless you choose below to copy what BitosX has no field for into the notes.");
        }

        if (plan.Extras.Keys.Any(kind => kind != ImportExtraKind.Totp))
        {
            Extras = "BitosX has no field for some of what these logins hold — "
                + string.Join("; ", Enum.GetValues<ImportExtraKind>()
                    .Where(kind => kind != ImportExtraKind.Totp && plan.Extras.ContainsKey(kind))
                    .Select(kind => string.Create(Culture, $"{ImportTexts.Text(kind)}: {Number(plan.Extras[kind])}")))
                + (plan.Options.CopyExtrasToNotes
                    ? ". It is copied into each item's notes."
                    : ". It is left out, unless you choose below to copy it into the notes.");
        }

        if (plan.HttpsAdded > 0)
        {
            HttpsAdded = plan.HttpsAdded == 1
                ? "https:// is put in front of 1 address that has no scheme."
                : string.Create(Culture, $"https:// is put in front of {Number(plan.HttpsAdded)} addresses that have no scheme.");
        }

        if (plan.NamesDerived > 0)
        {
            NamesDerived = plan.NamesDerived == 1
                ? "1 login has no name in the file. It is named after its address or its username."
                : string.Create(Culture, $"{Number(plan.NamesDerived)} logins have no name in the file. They are named after their address or their username.");
        }

        if (file.IgnoredColumns.Count > 0)
        {
            IEnumerable<string> names = file.IgnoredColumns.Take(MaxListedColumns).Select(name => OnOneLine(name, MaxColumnNameLength));
            int more = file.IgnoredColumns.Count - MaxListedColumns;
            IgnoredColumns = "Columns of the file that are not read: " + string.Join(", ", names)
                + (more > 0 ? string.Create(Culture, $", and {Number(more)} more.") : ".");
        }

        CanBeImported = plan.ToAdd > 0 && !overLimit;
        ConfirmText = CanBeImported ? "Import " + Count(plan.ToAdd, "login", "logins") : "Import";
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The preview of <paramref name="plan"/>.</summary>
    public static ImportPreview From(ImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ImportPreview(plan);
    }

    /// <summary>What kind of file it is taken to be, and how many entries it holds.</summary>
    public string Source { get; }

    /// <summary>How many logins would be added, and how many items the vault holds before and after.</summary>
    public string ToAdd { get; }

    /// <summary>Set when the vault would hold more items than a vault may: nothing can be imported.</summary>
    public string? OverLimit { get; }

    /// <summary>How many logins are duplicates, and what becomes of them.</summary>
    public string? Duplicates { get; }

    /// <summary>How many entries cannot be imported.</summary>
    public string? NotImported { get; }

    /// <summary>The first of the entries that cannot be imported: position, name and reason.</summary>
    public IReadOnlyList<ImportPreviewEntry> NotImportedEntries { get; } = [];

    /// <summary>How many entries that cannot be imported are not in <see cref="NotImportedEntries"/>.</summary>
    public string? NotImportedMore { get; }

    /// <summary>What the file holds that is not a login, by kind.</summary>
    public string? Unsupported { get; }

    /// <summary>How many entries are in the trash or recycle bin of the file.</summary>
    public string? InTrash { get; }

    /// <summary>How many entries have nothing in them.</summary>
    public string? Empty { get; }

    /// <summary>What the logins hold that BitosX has no field for, by kind, and what becomes of it.</summary>
    public string? Extras { get; }

    /// <summary>How many logins bring an authenticator key.</summary>
    public string? AuthenticatorKeys { get; }

    /// <summary>How many authenticator keys could not be read, and what becomes of their text.</summary>
    public string? UnreadableKeys { get; }

    /// <summary>How many addresses are given <c>https://</c>.</summary>
    public string? HttpsAdded { get; }

    /// <summary>How many logins are given a name.</summary>
    public string? NamesDerived { get; }

    /// <summary>The columns of a CSV file that are not read.</summary>
    public string? IgnoredColumns { get; }

    /// <summary>Whether there is anything to import, and the vault can take it.</summary>
    public bool CanBeImported { get; }

    /// <summary>What the button that imports says.</summary>
    public string ConfirmText { get; }

    /// <summary>"1 login" or "12 logins", with the number as this user's language writes it.</summary>
    internal static string Count(int count, string one, string many)
        => count == 1 ? "1 " + one : Number(count) + " " + many;

    private static string Number(int value) => value.ToString("N0", Culture);

    /// <summary>
    /// <paramref name="text"/> as it is shown on one line: every control character and line
    /// separator as a space, and at most <paramref name="maxLength"/> characters of it, followed by
    /// an ellipsis if there were more. A character outside the Basic Multilingual Plane is kept
    /// whole or left out.
    /// </summary>
    internal static string OnOneLine(string text, int maxLength)
    {
        const char LineSeparator = (char)0x2028;
        const char ParagraphSeparator = (char)0x2029;
        const char Ellipsis = (char)0x2026;

        int length = Math.Min(text.Length, maxLength);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        var shown = new StringBuilder(length + 1);
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            shown.Append(char.IsControl(c) || c is LineSeparator or ParagraphSeparator ? ' ' : c);
        }

        if (length < text.Length)
        {
            shown.Append(Ellipsis);
        }

        return shown.ToString();
    }
}

/// <summary>An entry of the file that cannot be imported, as the preview lists it. Nothing here is a secret.</summary>
/// <param name="Position">Where the entry is in the file: "Entry 17".</param>
/// <param name="Name">
/// Its name, on one line and cut to a length that fits. Empty for a row whose values moved, which
/// has no value that can be trusted to be its name.
/// </param>
/// <param name="Reason">Why it cannot be imported: one of the fixed texts.</param>
public sealed record ImportPreviewEntry(string Position, string Name, string Reason);
