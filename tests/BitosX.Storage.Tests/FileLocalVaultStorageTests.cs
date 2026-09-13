using System.Text;
using BitosX.Crypto;
using BitosX.Storage;

namespace BitosX.Storage.Tests;

public sealed class FileLocalVaultStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _vaultPath;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    public FileLocalVaultStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vaultPath = Path.Combine(_tempDir, "vault.bitosx");
    }

    private EncryptedVault MakeVault(string plaintext = "{\"schemaVersion\":1,\"items\":[]}")
        => _cryptor.Create(Encoding.UTF8.GetBytes("pw-master"), Encoding.UTF8.GetBytes(plaintext), Argon2Parameters.ForTesting).Encrypted;

    [Fact]
    public async Task Exists_IsFalse_WhenNoFile()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        Assert.False(await storage.ExistsAsync());
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        EncryptedVault vault = MakeVault();

        await storage.WriteAsync(vault);
        Assert.True(await storage.ExistsAsync());

        EncryptedVault read = await storage.ReadAsync();
        Assert.Equal(vault.Salt, read.Salt);
        Assert.Equal(vault.Ciphertext, read.Ciphertext);
        Assert.Equal(vault.EncryptedVaultKey, read.EncryptedVaultKey);
    }

    [Fact]
    public async Task Write_OverwritesExistingVault()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        await storage.WriteAsync(MakeVault("{\"schemaVersion\":1,\"items\":[\"a\"]}"));
        EncryptedVault second = MakeVault("{\"schemaVersion\":1,\"items\":[\"b\"]}");

        await storage.WriteAsync(second);
        EncryptedVault read = await storage.ReadAsync();
        Assert.Equal(second.Ciphertext, read.Ciphertext);
    }

    [Fact]
    public async Task FailedWrite_DoesNotDestroyExistingVault()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        EncryptedVault original = MakeVault();
        await storage.WriteAsync(original);

        // Force the temp-file write to fail by occupying its path with a directory.
        string tempPath = _vaultPath + ".tmp";
        Directory.CreateDirectory(tempPath);

        // The write must fail (IOException or UnauthorizedAccessException depending on OS).
        Exception? failure = await Record.ExceptionAsync(
            () => storage.WriteAsync(MakeVault("{\"schemaVersion\":1,\"items\":[\"new\"]}")));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or UnauthorizedAccessException);

        // The original vault must still be intact and readable.
        Directory.Delete(tempPath);
        EncryptedVault read = await storage.ReadAsync();
        Assert.Equal(original.Ciphertext, read.Ciphertext);
    }

    [Fact]
    public async Task Read_MissingFile_Throws()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.ReadAsync());
    }

    [Fact]
    public async Task Read_MalformedFile_FailsClosed()
    {
        await File.WriteAllTextAsync(_vaultPath, "this is not a vault");
        var storage = new FileLocalVaultStorage(_vaultPath);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => storage.ReadAsync());
    }

    [Fact]
    public async Task Read_OversizedFile_FailsClosed()
    {
        // Write a file larger than the allowed maximum.
        await using (var fs = new FileStream(_vaultPath, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(FileLocalVaultStorage.MaxFileSizeBytes + 1);
        }

        var storage = new FileLocalVaultStorage(_vaultPath);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => storage.ReadAsync());
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        await storage.WriteAsync(MakeVault());
        await storage.DeleteAsync();
        Assert.False(await storage.ExistsAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
