using System.Text;
using BitosX.Core.Domain;

namespace BitosX.Core.ImportExport;

/// <summary>
/// What importing a file into a vault would do: every login of the file with what becomes of it, and
/// the counts a preview shows. Nothing has been written. Holds the secrets of the file.
/// </summary>
public sealed class ImportPlan
{
    internal ImportPlan(
        ParsedImport source,
        ImportOptions options,
        IReadOnlyList<ImportCandidate> candidates,
        int emptyEntries,
        int vaultItemsBefore)
    {
        Source = source;
        Options = options;
        Candidates = candidates;
        EmptyEntries = emptyEntries;
        VaultItemsBefore = vaultItemsBefore;

        var notImported = new List<ImportCandidate>();
        var extras = new Dictionary<ImportExtraKind, int>();
        foreach (ImportCandidate candidate in candidates)
        {
            if (candidate.IsDuplicate)
            {
                Duplicates++;
            }

            switch (candidate.Status)
            {
                case ImportStatus.NotImported:
                    notImported.Add(candidate);
                    break;
                case ImportStatus.Add:
                    ToAdd++;
                    HttpsAdded += candidate.HttpsAdded;
                    NamesDerived += candidate.NameWasDerived ? 1 : 0;
                    foreach (ImportExtraKind kind in Enum.GetValues<ImportExtraKind>())
                    {
                        int count = candidate.Extras.Count(kind);
                        if (count > 0)
                        {
                            extras[kind] = extras.GetValueOrDefault(kind) + count;
                        }
                    }

                    break;
            }
        }

        NotImported = notImported;
        Extras = extras;
    }

    /// <summary>The file this is the plan for.</summary>
    public ParsedImport Source { get; }

    /// <summary>The choices the plan was made with.</summary>
    public ImportOptions Options { get; }

    /// <summary>Every login of the file that has anything in it, in the order of the file, with what becomes of it.</summary>
    public IReadOnlyList<ImportCandidate> Candidates { get; }

    /// <summary>How many items would be added.</summary>
    public int ToAdd { get; }

    /// <summary>
    /// How many logins equal an item of the vault or an earlier entry of the file. They are skipped,
    /// unless <see cref="ImportOptions.ImportDuplicates"/> is chosen, in which case they are among
    /// <see cref="ToAdd"/>.
    /// </summary>
    public int Duplicates { get; }

    /// <summary>The logins that break a limit of the vault, each with its <see cref="ImportCandidate.Reason"/>.</summary>
    public IReadOnlyList<ImportCandidate> NotImported { get; }

    /// <summary>How many logins of the file have nothing in them. They are passed over.</summary>
    public int EmptyEntries { get; }

    /// <summary>
    /// How many extras of each kind the logins that would be added hold. They are left out, or, with
    /// <see cref="ImportOptions.CopyExtrasToNotes"/>, copied into the notes. Kinds with none are absent.
    /// </summary>
    public IReadOnlyDictionary<ImportExtraKind, int> Extras { get; }

    /// <summary>How many addresses of the logins that would be added were given <c>https://</c> in front.</summary>
    public int HttpsAdded { get; }

    /// <summary>How many of the logins that would be added had no name, and were given one.</summary>
    public int NamesDerived { get; }

    /// <summary>How many items the vault held when the plan was made.</summary>
    public int VaultItemsBefore { get; }

    /// <summary>How many items the vault would hold after the import.</summary>
    public int VaultItemsAfter => VaultItemsBefore + ToAdd;

    /// <summary>
    /// Whether the vault would hold more items than a vault may (<see cref="ImportLimits.MaxVaultItems"/>).
    /// Such an import is refused as a whole.
    /// </summary>
    public bool ExceedsVaultItemLimit => VaultItemsAfter > ImportLimits.MaxVaultItems;

    /// <summary>
    /// The items to add, in the order of the file: each with a new identifier, and created and last
    /// changed at <paramref name="importedAt"/>. An empty username, password or notes is stored the
    /// way the editor stores it, as none.
    /// </summary>
    public IReadOnlyList<LoginItem> CreateItems(DateTimeOffset importedAt)
    {
        var items = new List<LoginItem>(ToAdd);
        foreach (ImportCandidate candidate in Candidates)
        {
            if (candidate.Status != ImportStatus.Add)
            {
                continue;
            }

            items.Add(new LoginItem
            {
                Id = Guid.NewGuid(),
                Name = candidate.Name,
                Username = candidate.Username.Length > 0 ? candidate.Username : null,
                Password = candidate.Password.Length > 0 ? candidate.Password : null,
                Urls = [.. candidate.Urls],
                Notes = candidate.Notes.Length > 0 ? candidate.Notes : null,
                Favorite = candidate.Favorite,
                CreatedAt = importedAt,
                UpdatedAt = importedAt,
            });
        }

        return items;
    }
}

/// <summary>Compares the logins of a file with the items of a vault, and decides what becomes of each.</summary>
public static class ImportPlanner
{
    /// <summary>
    /// Makes the plan for importing <paramref name="source"/> into a vault that holds
    /// <paramref name="existing"/>. Changes nothing.
    /// </summary>
    /// <remarks>
    /// An entry is a duplicate if an item of the vault, or an earlier entry of the file that is
    /// itself importable, has the same name, username, password and web addresses: compared
    /// character by character, an absent value being equal to an empty one, the addresses in the
    /// same order. Names and addresses of the vault's items are trimmed for the comparison, as
    /// those of the file are. Notes and the favorite mark are not compared.
    /// </remarks>
    public static ImportPlan Plan(ParsedImport source, IReadOnlyList<VaultItem> existing, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var key = new StringBuilder();
        foreach (VaultItem item in existing)
        {
            if (item is LoginItem login)
            {
                seen.Add(KeyOf(key, ImportText.Trim(login.Name), login.Username ?? string.Empty, login.Password ?? string.Empty, login.Urls, trimUrls: true));
            }
        }

        var candidates = new List<ImportCandidate>(source.Entries.Count);
        int empty = 0;
        foreach (ImportEntry entry in source.Entries)
        {
            ImportCandidate? candidate = ImportNormalizer.Normalize(entry, options.CopyExtrasToNotes);
            if (candidate is null)
            {
                empty++;
                continue;
            }

            if (candidate.Status == ImportStatus.Add
                && !seen.Add(KeyOf(key, candidate.Name, candidate.Username, candidate.Password, candidate.Urls, trimUrls: false)))
            {
                candidate.IsDuplicate = true;
                if (!options.ImportDuplicates)
                {
                    candidate.Status = ImportStatus.Duplicate;
                }
            }

            candidates.Add(candidate);
        }

        return new ImportPlan(source, options, candidates, empty, existing.Count);
    }

    /// <summary>
    /// The four values as one string that no other four values give: each one preceded by its
    /// length, so that no character has to be kept out of the values to serve as a separator.
    /// </summary>
    private static string KeyOf(
        StringBuilder key, string name, string username, string password, IReadOnlyList<string> urls, bool trimUrls)
    {
        key.Clear();
        Append(key, name);
        Append(key, username);
        Append(key, password);
        key.Append(urls.Count).Append(':');
        foreach (string url in urls)
        {
            Append(key, trimUrls ? ImportText.Trim(url) : url);
        }

        return key.ToString();
    }

    private static void Append(StringBuilder key, string value)
        => key.Append(value.Length).Append(':').Append(value);
}
