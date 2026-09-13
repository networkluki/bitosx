using BitosX.Crypto;

namespace BitosX.Core.Storage;

/// <summary>
/// Persists the encrypted vault to local storage. Implementations MUST only ever read and
/// write the encrypted representation; plaintext must never reach this layer.
/// </summary>
public interface ILocalVaultStorage
{
    /// <summary>Returns whether a stored vault currently exists.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads and parses the stored encrypted vault. Throws if it is missing or malformed.</summary>
    Task<EncryptedVault> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the encrypted vault, replacing any existing one. Implementations should perform
    /// the replacement atomically so that a failure does not corrupt or destroy the previous vault.
    /// </summary>
    Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default);

    /// <summary>Deletes the stored vault if present.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
