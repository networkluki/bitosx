using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Core.Tests;

/// <summary>
/// An <see cref="ILocalVaultStorage"/> that (a) records the cancellation token passed to writes and
/// (b) can simulate the vault file being changed by another process. Used to verify the rotation
/// on-disk/in-memory consistency gate and its non-cancellable commit boundary. Like the real storage,
/// a failed write leaves the previously stored vault intact.
/// </summary>
internal sealed class InstrumentedVaultStorage : ILocalVaultStorage
{
    private EncryptedVault? _stored;

    /// <summary>If set, is cancelled during the next <see cref="ReadAsync"/> (simulates a race).</summary>
    public CancellationTokenSource? CancelOnRead { get; set; }

    /// <summary>The cancellation token most recently passed to <see cref="WriteAsync"/>.</summary>
    public CancellationToken LastWriteToken { get; private set; }

    public int WriteCount { get; private set; }

    /// <summary>Next write throws before any replacement (disk keeps the old vault).</summary>
    public bool FailNextWrite { get; set; }

    /// <summary>Next write stores the written vault (replacement takes effect) and THEN throws.</summary>
    public bool ReplaceThenThrowNextWrite { get; set; }

    /// <summary>If set, the next write stores THIS vault (an indeterminate/other state) and then throws.</summary>
    public EncryptedVault? StoreThenThrowNextWrite { get; set; }

    /// <summary>If set, every read throws this (simulates a stored file this build cannot parse).</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <summary>Invoked at the start of every read (simulates something happening mid-operation).</summary>
    public Action? OnRead { get; set; }

    /// <summary>Invoked at the start of every write, before anything is stored.</summary>
    public Action? OnWrite { get; set; }

    /// <summary>If set, every write awaits this before storing (holds an operation in flight).</summary>
    public Func<Task>? BeforeWriteAsync { get; set; }

    public EncryptedVault? Stored => _stored;

    /// <summary>Replaces the stored vault out-of-band, i.e. without going through the service.</summary>
    public void ReplaceExternally(EncryptedVault vault) => _stored = vault;

    /// <summary>Removes the stored vault out-of-band.</summary>
    public void DeleteExternally() => _stored = null;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_stored is not null);

    public Task<EncryptedVault> ReadAsync(CancellationToken cancellationToken = default)
    {
        CancelOnRead?.Cancel();
        OnRead?.Invoke();

        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return _stored is null
            ? throw new FileNotFoundException("No vault.")
            : Task.FromResult(_stored);
    }

    public async Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default)
    {
        LastWriteToken = cancellationToken;
        OnWrite?.Invoke();

        if (BeforeWriteAsync is not null)
        {
            await BeforeWriteAsync().ConfigureAwait(false);
        }

        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Simulated write failure (before replacement)."); // disk unchanged
        }

        if (ReplaceThenThrowNextWrite)
        {
            ReplaceThenThrowNextWrite = false;
            _stored = vault;   // replacement took effect...
            WriteCount++;
            throw new IOException("Simulated failure after replacement."); // ...then the call threw
        }

        if (StoreThenThrowNextWrite is not null)
        {
            _stored = StoreThenThrowNextWrite; // disk left in an indeterminate/other state
            StoreThenThrowNextWrite = null;
            WriteCount++;
            throw new IOException("Simulated failure leaving indeterminate disk state.");
        }

        _stored = vault; // only assigned on success -> previous value preserved on failure
        WriteCount++;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        _stored = null;
        return Task.CompletedTask;
    }
}
