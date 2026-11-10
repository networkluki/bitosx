using BitosX.Core.Domain;
using BitosX.Crypto;

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

    /// <summary>
    /// The key-derivation parameters protecting the unlocked vault — the ones stored in its header,
    /// which may differ from the parameters this build would use for a new vault — or
    /// <see langword="null"/> when locked.
    /// </summary>
    Argon2Parameters? KdfParameters { get; }

    /// <summary>
    /// Whether the unlocked vault is keyed with an encoding of the master password that versions
    /// before 0.6.0 used, rather than the canonical one. Such a vault opens as long as the password is
    /// typed the way it was when the vault was created; it moves to the canonical encoding the next
    /// time the master password is changed, the vault key is rotated, or the key-derivation settings
    /// are strengthened. <see langword="false"/> when locked.
    /// </summary>
    bool UsesLegacyPasswordEncoding { get; }

    /// <summary>Refreshes <see cref="State"/> from storage (NoVault vs Locked). Call at startup.</summary>
    Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a new vault protected by the given master password and leaves it unlocked.</summary>
    Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default);

    /// <summary>Attempts to unlock the existing vault. Throws on wrong password or corrupt/unsupported file.</summary>
    Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default);

    /// <summary>Locks the vault, clearing decrypted contents and key material from memory.</summary>
    void Lock();

    /// <summary>
    /// Changes the master password by re-wrapping the existing vault key under a new key derived
    /// from <paramref name="newPassword"/>. The vault key and encrypted content are unchanged.
    /// Requires the vault to be unlocked and the correct <paramref name="currentPassword"/>; a wrong
    /// current password throws and leaves the stored vault untouched.
    /// </summary>
    Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the vault key: generates a fresh random vault key and re-encrypts all content under it,
    /// then re-wraps the new key. Fresh salt and nonces are produced; the master password, on-disk
    /// format, and Argon2 parameters are unchanged. Requires the vault to be unlocked <b>and</b> the
    /// correct <paramref name="currentPassword"/> (the unlocked session alone is not sufficient
    /// authorization). A wrong current password throws and leaves the stored vault byte-for-byte
    /// unchanged. On success the in-memory session adopts the new key material and stays unlocked.
    /// </summary>
    Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the vault to stronger key-derivation settings by re-wrapping the existing vault key.
    /// The vault key and the encrypted content are unchanged, and so is the master password.
    /// Requires the vault to be unlocked <b>and</b> the correct <paramref name="currentPassword"/>;
    /// a wrong password throws and leaves the stored vault untouched. Settings can only be
    /// strengthened: weaker or equal ones are refused. From then on every unlock costs what the new
    /// settings ask for, on every device the vault is opened on.
    /// </summary>
    Task StrengthenKeyDerivationAsync(
        string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that <paramref name="currentPassword"/> is the master password of the unlocked vault,
    /// the way changing the master password does: the stored vault must be the one this session
    /// holds, and the password must open it. Changes nothing. For an action that the unlocked session
    /// alone must not authorize, such as writing the vault out unencrypted.
    /// Requires the vault to be unlocked; a wrong password throws.
    /// </summary>
    /// <returns>
    /// The items the vault held when the password was verified, so that the caller acts on exactly
    /// what was authorized, whatever happens to the session afterwards.
    /// </returns>
    Task<IReadOnlyList<VaultItem>> VerifyMasterPasswordAsync(string currentPassword, CancellationToken cancellationToken = default);

    /// <summary>Adds a login item and persists the vault.</summary>
    Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds several login items and persists the vault <b>once</b>: either all of them are stored, or
    /// none is. <paramref name="chooseItems"/> is given the items the vault holds at that moment —
    /// nothing else can change them before the write is done — and returns the items to add. If it
    /// returns none, nothing is written. If it throws, nothing is written and its exception is what
    /// the caller sees. Returns the items that were added.
    /// </summary>
    Task<IReadOnlyList<LoginItem>> AddLoginsAsync(
        Func<IReadOnlyList<VaultItem>, IReadOnlyList<LoginItem>> chooseItems,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing item and persists the vault. Returns the stored (timestamped) item.</summary>
    Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item by id and persists the vault.</summary>
    Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Toggles the favorite flag of an item and persists the vault.</summary>
    Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default);
}
