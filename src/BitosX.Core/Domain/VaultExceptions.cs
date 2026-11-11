namespace BitosX.Core.Domain;

/// <summary>Base type for domain-level vault errors.</summary>
public class VaultException : Exception
{
    public VaultException(string message) : base(message) { }
    public VaultException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when an item fails validation.</summary>
public sealed class VaultValidationException : VaultException
{
    public VaultValidationException(string message) : base(message) { }
}

/// <summary>Thrown when adding an item whose identifier already exists in the vault.</summary>
public sealed class DuplicateItemException : VaultException
{
    public DuplicateItemException(Guid id) : base($"An item with id '{id}' already exists in the vault.") { }
}

/// <summary>Thrown when an operation references an item that does not exist.</summary>
public sealed class ItemNotFoundException : VaultException
{
    public ItemNotFoundException(Guid id) : base($"No item with id '{id}' exists in the vault.") { }
}

/// <summary>
/// Thrown when the on-disk vault no longer matches the state the current session loaded or last
/// persisted — i.e. the vault file was replaced or modified by another process/instance while it was
/// open. Every operation that replaces the stored vault (saving a change, changing the master
/// password, rotating the vault key) fails closed in this state: nothing is written, the external
/// file is left byte-for-byte intact, and the current session keeps its state.
/// The message is deliberately non-secret (no keys, hashes, or plaintext).
/// </summary>
public sealed class VaultChangedExternallyException : VaultException
{
    public VaultChangedExternallyException()
        : base("The vault changed on disk while it was open. Nothing was saved. Lock and reopen the vault, then try again.") { }
}

/// <summary>
/// Thrown when a destructive write failed and the resulting on-disk state could not be reconciled with
/// either the previous or the intended vault — i.e. the file may be in an indeterminate state after a
/// disk error. The session is locked as a precaution and the user must reopen it (restoring from a
/// backup if the file is unreadable). The message is deliberately non-secret.
/// </summary>
public sealed class VaultInconsistentStateException : VaultException
{
    public VaultInconsistentStateException()
        : base("The vault could not be verified after a disk error and has been locked. Reopen it and check your data — restore from a backup if it will not open.") { }
}

/// <summary>
/// Thrown when the encrypted vault would be larger than the store can read back. It is raised before
/// anything is written: a file this build refuses to open must never take the place of one it can
/// open. The change that was being saved is not applied, and the stored vault is untouched.
/// The message is deliberately non-secret.
/// </summary>
public sealed class VaultTooLargeException : VaultException
{
    public VaultTooLargeException(long limitBytes)
        : base($"The vault would be larger than {limitBytes / (1024 * 1024)} MiB, which is the most BitosX can store. Nothing was saved.")
    {
        LimitBytes = limitBytes;
    }

    /// <summary>The largest vault file the store accepts, in bytes.</summary>
    public long LimitBytes { get; }
}
