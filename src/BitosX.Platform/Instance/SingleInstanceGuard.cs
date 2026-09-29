using System.Security.Cryptography;
using System.Text;

namespace BitosX.Platform.Instance;

/// <summary>
/// Ensures only one running instance of the application works on a given vault file. Two instances
/// on the same vault each hold their own copy of its contents, and whichever saves last would
/// overwrite the other's changes; the vault service refuses such a write (it fails closed), but it
/// is better not to get there.
/// </summary>
/// <remarks>
/// Backed by a named mutex that lives exactly as long as a process holds it, so a crashed instance
/// never leaves a stale lock behind. The name is derived from the vault path and is scoped to the
/// current login session. This is a convenience for the honest case, not a security boundary.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Claims the vault at <paramref name="vaultFilePath"/> for this process. Returns the guard to
    /// hold for the lifetime of the application, or <see langword="null"/> if another instance
    /// already holds it.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);

        var mutex = new Mutex(initiallyOwned: false, NameFor(vaultFilePath), out bool createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        // The name already existed, so some process is holding it open.
        mutex.Dispose();
        return null;
    }

    /// <summary>The mutex name for a vault path: stable, and free of characters a name cannot hold.</summary>
    public static string NameFor(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);

        string normalized = Path.GetFullPath(vaultFilePath);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            normalized = normalized.ToUpperInvariant(); // case-insensitive file systems
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "BitosX.Vault." + Convert.ToHexString(hash, 0, 16);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
    }
}
