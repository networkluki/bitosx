using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Core.Tests;

/// <summary>
/// In-memory <see cref="ILocalVaultStorage"/> used by service tests. Mimics the atomic-write
/// guarantee of the real storage: a failed write leaves the previously stored vault intact.
/// </summary>
internal sealed class InMemoryVaultStorage : ILocalVaultStorage
{
    private EncryptedVault? _stored;

    public bool FailNextWrite { get; set; }
    public int WriteCount { get; private set; }

    public EncryptedVault? Stored => _stored;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_stored is not null);

    public Task<EncryptedVault> ReadAsync(CancellationToken cancellationToken = default)
        => _stored is null
            ? throw new FileNotFoundException("No vault.")
            : Task.FromResult(_stored);

    public Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Simulated write failure.");
        }

        _stored = vault; // only assigned on success -> previous value preserved on failure
        WriteCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        _stored = null;
        return Task.CompletedTask;
    }
}
