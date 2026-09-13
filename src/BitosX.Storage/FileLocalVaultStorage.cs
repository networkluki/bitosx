using BitosX.Core.Storage;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// Stores the encrypted vault as a single JSON file on the local filesystem. Writes are
/// performed atomically (write to a temporary file, then replace) so that a failed or
/// interrupted write cannot corrupt or destroy the existing vault. Only the encrypted
/// representation is ever written; plaintext never reaches this layer.
/// </summary>
public sealed class FileLocalVaultStorage : ILocalVaultStorage
{
    /// <summary>Maximum accepted vault file size. Guards against oversized/corrupt input.</summary>
    public const long MaxFileSizeBytes = 16 * 1024 * 1024;

    private readonly string _vaultFilePath;
    private readonly string _tempFilePath;
    private readonly string _directory;

    /// <summary>Creates storage backed by the given absolute vault file path.</summary>
    public FileLocalVaultStorage(string vaultFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);
        _vaultFilePath = Path.GetFullPath(vaultFilePath);
        _directory = Path.GetDirectoryName(_vaultFilePath)
            ?? throw new ArgumentException("The vault file path must include a directory.", nameof(vaultFilePath));
        _tempFilePath = _vaultFilePath + ".tmp";
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

        var info = new FileInfo(_vaultFilePath);
        if (info.Length > MaxFileSizeBytes)
        {
            throw new UnsupportedVaultFormatException(
                $"The vault file is too large ({info.Length} bytes; limit {MaxFileSizeBytes}).");
        }

        string json = await File.ReadAllTextAsync(_vaultFilePath, cancellationToken).ConfigureAwait(false);
        return EncryptedVaultSerializer.Deserialize(json); // fails closed on malformed content
    }

    public async Task WriteAsync(EncryptedVault vault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vault);

        string json = EncryptedVaultSerializer.Serialize(vault);
        Directory.CreateDirectory(_directory);

        try
        {
            // 1) Write the full new content to a temporary file in the same directory.
            await File.WriteAllTextAsync(_tempFilePath, json, cancellationToken).ConfigureAwait(false);

            // 2) Atomically move it into place. If the target exists, File.Replace performs an
            //    atomic swap; if the replace fails, the original vault file is left untouched.
            if (File.Exists(_vaultFilePath))
            {
                File.Replace(_tempFilePath, _vaultFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(_tempFilePath, _vaultFilePath);
            }
        }
        finally
        {
            // Best-effort cleanup of the temporary file if it still exists (e.g. after a failure).
            TryDeleteTempFile();
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_vaultFilePath))
        {
            File.Delete(_vaultFilePath);
        }

        TryDeleteTempFile();
        return Task.CompletedTask;
    }

    private void TryDeleteTempFile()
    {
        try
        {
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }
        catch (IOException)
        {
            // Ignored: a leftover temp file is harmless and will be overwritten on the next write.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored for the same reason.
        }
    }
}
