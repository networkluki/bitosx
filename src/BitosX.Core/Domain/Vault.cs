using BitosX.Core.Validation;

namespace BitosX.Core.Domain;

/// <summary>
/// The decrypted vault aggregate: the set of items the user manages. Exists in memory only
/// while unlocked. Enforces unique item identifiers and validates items on add/update and on load.
/// </summary>
public sealed class Vault
{
    /// <summary>Current in-memory schema version of the decrypted vault contents.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly List<VaultItem> _items;

    /// <summary>Creates an empty vault at the current schema version.</summary>
    public Vault() : this(CurrentSchemaVersion, []) { }

    /// <summary>
    /// Creates a vault from persisted contents (used by the serializer). Every item is validated
    /// against the same rules as add/update, so the in-memory model is always well formed no matter
    /// what the decrypted document contained. Failures are reported without echoing item content.
    /// </summary>
    internal Vault(int schemaVersion, IEnumerable<VaultItem?> items)
    {
        SchemaVersion = schemaVersion;
        _items = [];

        var seen = new HashSet<Guid>();
        foreach (VaultItem? item in items)
        {
            if (item is null)
            {
                throw new VaultValidationException("The vault contains an empty item entry.");
            }

            VaultItem loaded = VaultValidation.NormalizeLoaded(item);
            try
            {
                VaultValidation.Validate(loaded);
            }
            catch (VaultValidationException)
            {
                // Validation messages can quote field values; decrypted content must not leak
                // into an error shown before the vault is open.
                throw new VaultValidationException("The vault contains an invalid item.");
            }

            // Reject duplicate ids coming from a (possibly corrupted) source.
            if (!seen.Add(loaded.Id))
            {
                throw new VaultValidationException("The vault contains duplicate item identifiers.");
            }

            _items.Add(loaded);
        }
    }

    private Vault(Vault source)
    {
        SchemaVersion = source.SchemaVersion;
        _items = [.. source._items];
    }

    /// <summary>Schema version the contents were written with.</summary>
    public int SchemaVersion { get; }

    /// <summary>All items, in insertion order.</summary>
    public IReadOnlyList<VaultItem> Items => _items;

    /// <summary>
    /// Returns an independent copy holding the same (immutable) items. Changes are applied to a copy
    /// and only published once persisted, so a vault that is visible to readers is never mutated.
    /// </summary>
    internal Vault Clone() => new(this);

    /// <summary>Finds an item by id, or returns <see langword="null"/>.</summary>
    public VaultItem? Find(Guid id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>Adds a new item. Throws if the id already exists or validation fails.</summary>
    public void Add(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        VaultValidation.Validate(item);

        if (_items.Any(i => i.Id == item.Id))
        {
            throw new DuplicateItemException(item.Id);
        }

        _items.Add(item);
    }

    /// <summary>
    /// Adds several new items, or none of them: every item is validated and every id is checked
    /// before the first one is added, so a failure leaves the vault as it was. Throws if an id
    /// already exists (in the vault or earlier in <paramref name="items"/>) or validation fails.
    /// </summary>
    public void AddRange(IReadOnlyList<VaultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // One pass over the ids instead of one search of the vault for every new item.
        var ids = new HashSet<Guid>(_items.Count + items.Count);
        foreach (VaultItem existing in _items)
        {
            ids.Add(existing.Id);
        }

        foreach (VaultItem item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("The items to add must not contain an empty entry.", nameof(items));
            }

            VaultValidation.Validate(item);

            if (!ids.Add(item.Id))
            {
                throw new DuplicateItemException(item.Id);
            }
        }

        _items.AddRange(items);
    }

    /// <summary>
    /// Replaces the existing item with the same id, stamping <see cref="VaultItem.UpdatedAt"/>.
    /// Returns the stored (timestamped) item. Throws if the item does not exist or is invalid.
    /// </summary>
    public VaultItem Update(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Stamp before validating, and never earlier than CreatedAt: if the system clock was moved
        // back since the item was created, "now" would otherwise produce an item that fails its own
        // validation and could never be edited, or restored from a backup, again.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        VaultItem stamped = item with { UpdatedAt = now < item.CreatedAt ? item.CreatedAt : now };
        VaultValidation.Validate(stamped);

        int index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0)
        {
            throw new ItemNotFoundException(item.Id);
        }

        _items[index] = stamped;
        return stamped;
    }

    /// <summary>Removes the item with the given id. Returns <see langword="false"/> if it did not exist.</summary>
    public bool Remove(Guid id)
    {
        int index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        return true;
    }
}
