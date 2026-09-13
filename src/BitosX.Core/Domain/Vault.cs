using BitosX.Core.Validation;

namespace BitosX.Core.Domain;

/// <summary>
/// The decrypted vault aggregate: the set of items the user manages. Exists in memory only
/// while unlocked. Enforces unique item identifiers and validates items on add/update.
/// </summary>
public sealed class Vault
{
    /// <summary>Current in-memory schema version of the decrypted vault contents.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly List<VaultItem> _items;

    /// <summary>Creates an empty vault at the current schema version.</summary>
    public Vault() : this(CurrentSchemaVersion, []) { }

    /// <summary>Creates a vault from persisted contents (used by the serializer).</summary>
    internal Vault(int schemaVersion, IEnumerable<VaultItem> items)
    {
        SchemaVersion = schemaVersion;
        _items = [.. items];

        // Reject duplicate ids coming from a (possibly corrupted) source.
        if (_items.Select(i => i.Id).Distinct().Count() != _items.Count)
        {
            throw new VaultValidationException("The vault contains duplicate item identifiers.");
        }
    }

    /// <summary>Schema version the contents were written with.</summary>
    public int SchemaVersion { get; }

    /// <summary>All items, in insertion order.</summary>
    public IReadOnlyList<VaultItem> Items => _items;

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
    /// Replaces the existing item with the same id, stamping <see cref="VaultItem.UpdatedAt"/>.
    /// Returns the stored (timestamped) item. Throws if the item does not exist or is invalid.
    /// </summary>
    public VaultItem Update(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        VaultValidation.Validate(item);

        int index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0)
        {
            throw new ItemNotFoundException(item.Id);
        }

        VaultItem stamped = item with { UpdatedAt = DateTimeOffset.UtcNow };
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
