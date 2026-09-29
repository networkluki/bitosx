using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// Stores the encrypted vault as a single JSON file on the local filesystem. Writes are atomic and
/// durable (written to a temporary file, flushed to disk, then renamed over the target) so that a
/// failed or interrupted write — including a crash or power loss — cannot corrupt or destroy the
/// existing vault. Only the encrypted representation is ever written; plaintext never reaches this
/// layer.
/// </summary>
public sealed class FileLocalVaultStorage : ILocalVaultStorage
{
    /// <summary>Maximum accepted vault file size. Guards against oversized/corrupt input.</summary>
    public const long MaxFileSizeBytes = 16 * 1024 * 1024;

    private readonly string _vaultFilePath;
    private readonly string _directory;

    /// <summary>Creates storage backed by the given absolute vault file path.</summary>
    public FileLocalVaultStorage(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);
        _vaultFilePath = Path.GetFullPath(vaultFilePath);
        _directory = Path.GetDirectoryName(_vaultFilePath)
            ?? throw new ArgumentException("The vault file path must include a directory.", nameof(vaultFilePath));
    }

    /// <summary>The default per-user vault file location (e.g. %APPDATA%\BitosX\vault.bitosx on Windows).</summary>
    public static string DefaultVaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "BitosX",
        "vault.bitosx");

    /// <summary>Creates storage at the default per-user location.</summary>
    public static FileLocalVaultStorage CreateDefault() => new(DefaultVaultFilePath);

    /// <summary>The resolved absolute path of the vault file.</summary>
    public string VaultFilePath => _vaultFilePath;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_vaultFilePath));
    }

    public async Task<EncryptedVault> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_vaultFilePath))
        {
            throw new FileNotFoundException("No vault file was found.", _vaultFilePath);
        }

        byte[] bytes = await StorageFiles
            .ReadBoundedAsync(_vaultFilePath, MaxFileSizeBytes, "vault file", cancellationToken)
            .ConfigureAwait(false);
        return EncryptedVaultSerializer.Deserialize(bytes); // fails closed on malformed content
    }

    public async Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vault);

        byte[] bytes = EncryptedVaultSerializer.SerializeToUtf8Bytes(vault);
        StorageFiles.CreatePrivateDirectory(_directory);
        await StorageFiles.WriteAtomicAsync(_vaultFilePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_vaultFilePath))
        {
            File.Delete(_vaultFilePath);
        }

        return Task.CompletedTask;
    }
}
