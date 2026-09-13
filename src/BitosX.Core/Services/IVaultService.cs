using BitosX.Core.Domain;

namespace BitosX.Core.Services;

/// <summary>Lifecycle state of the vault as seen by the UI.</summary>
public enum VaultState
{
    /// <summary>No vault file exists yet; the user must create one.</summary>
    NoVault = 0,

    /// <summary>A vault exists but is locked.</summary>
    Locked = 1,

    /// <summary>The vault is unlocked and its contents are available in memory.</summary>
    Unlocked = 2,
}

/// <summary>
/// Orchestrates the vault lifecycle (create / unlock / lock) and item CRUD, tying together
/// the cryptographic layer and local storage. UI never talks to crypto or storage directly.
/// </summary>
public interface IVaultService
{
    /// <summary>Current lifecycle state.</summary>
    VaultState State { get; }

    /// <summary>Raised whenever <see cref="State"/> changes (enables future automatic locking).</summary>
    event EventHandler<VaultState>? StateChanged;

    /// <summary>The unlocked vault's items, or an empty list when locked.</summary>
    IReadOnlyList<VaultItem> Items { get; }

    /// <summary>Refreshes <see cref="State"/> from storage (NoVault vs Locked). Call at startup.</summary>
    Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a new vault protected by the given master password and leaves it unlocked.</summary>
    Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default);

    /// <summary>Attempts to unlock the existing vault. Throws on wrong password or corrupt/unsupported file.</summary>
    Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default);

    /// <summary>Locks the vault, clearing decrypted contents and key material from memory.</summary>
    void Lock();

    /// <summary>Adds a login item and persists the vault.</summary>
    Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing item and persists the vault. Returns the stored (timestamped) item.</summary>
    Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item by id and persists the vault.</summary>
    Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Toggles the favorite flag of an item and persists the vault.</summary>
    Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default);
}
