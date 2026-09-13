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
