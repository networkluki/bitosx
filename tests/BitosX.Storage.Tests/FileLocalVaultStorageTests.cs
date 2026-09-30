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

        byte[] originalBytes = await File.ReadAllBytesAsync(_vaultPath);

        // The write must fail (IOException or UnauthorizedAccessException depending on OS).
        Exception? failure;
        using (BlockReplacement())
        {
            failure = await Record.ExceptionAsync(
                () => storage.WriteAsync(MakeVault("{\"schemaVersion\":1,\"items\":[\"new\"]}")));
        }

        Assert.NotNull(failure);
        Assert.True(failure is IOException or UnauthorizedAccessException);

        // The original vault must still be intact and readable, byte for byte, with nothing left behind.
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_vaultPath));
        EncryptedVault read = await storage.ReadAsync();
        Assert.Equal(original.Ciphertext, read.Ciphertext);
        Assert.Equal(["vault.bitosx"], FileNames());
    }

    [Fact]
    public async Task Write_LeavesOnlyTheVaultFileBehind()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);

        await storage.WriteAsync(MakeVault());
        await storage.WriteAsync(MakeVault());

        Assert.Equal(["vault.bitosx"], FileNames());
    }

    [Fact]
    public async Task Write_DoesNotTouchAFilePlantedAtThePredictableTempName()
    {
        // Versions before 0.5.1 wrote through "<vault>.tmp". A file (or link) placed there by someone
        // else must be neither overwritten nor followed.
        var storage = new FileLocalVaultStorage(_vaultPath);
        string planted = _vaultPath + ".tmp";
        await File.WriteAllTextAsync(planted, "not ours");

        await storage.WriteAsync(MakeVault());

        Assert.Equal("not ours", await File.ReadAllTextAsync(planted));
        Assert.NotNull(await storage.ReadAsync());
    }

    [Fact]
    public async Task Write_RemovesTempFilesLeftByAnInterruptedWrite_ButOnlyItsOwnAndOnlyOldOnes()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        await storage.WriteAsync(MakeVault());
        DateTime old = DateTime.UtcNow.AddHours(-1);

        string staleOwn = _vaultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string staleLegacy = _vaultPath + ".tmp";
        string recentOwn = _vaultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string unrelatedOld = _vaultPath + ".notes.tmp";
        string recoveryCopy = _vaultPath + ".pre-restore";
        foreach (string path in new[] { staleOwn, staleLegacy, recentOwn, unrelatedOld, recoveryCopy })
        {
            await File.WriteAllTextAsync(path, "x");
        }

        foreach (string path in new[] { staleOwn, staleLegacy, unrelatedOld, recoveryCopy })
        {
            File.SetLastWriteTimeUtc(path, old);
        }

        await storage.WriteAsync(MakeVault());

        Assert.False(File.Exists(staleOwn));
        Assert.False(File.Exists(staleLegacy));
        Assert.True(File.Exists(recentOwn));     // could belong to a write still in progress
        Assert.True(File.Exists(unrelatedOld));  // not this class's naming pattern
        Assert.True(File.Exists(recoveryCopy));
    }

    [Fact]
    public async Task Read_FileWithByteOrderMark_IsAccepted()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        EncryptedVault vault = MakeVault();
        byte[] json = EncryptedVaultSerializer.SerializeToUtf8Bytes(vault);
        await File.WriteAllBytesAsync(_vaultPath, [0xEF, 0xBB, 0xBF, .. json]);

        EncryptedVault read = await storage.ReadAsync();

        Assert.Equal(vault.Ciphertext, read.Ciphertext);
    }

    [Fact]
    public async Task Read_InvalidUtf8_FailsClosed()
    {
        byte[] json = EncryptedVaultSerializer.SerializeToUtf8Bytes(MakeVault());
        int index = json.AsSpan().IndexOf("argon2id"u8);
        json[index] = 0xFF; // not valid anywhere in UTF-8; must be rejected, not replaced
        await File.WriteAllBytesAsync(_vaultPath, json);

        var storage = new FileLocalVaultStorage(_vaultPath);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => storage.ReadAsync());
    }

    [Fact]
    public async Task Read_EmptyFile_FailsClosed()
    {
        await File.WriteAllBytesAsync(_vaultPath, []);
        var storage = new FileLocalVaultStorage(_vaultPath);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(() => storage.ReadAsync());
    }

    // Who may read the files on Unix is tested in UnixFilesTests. (Windows protects the per-user
    // profile directory with its ACL instead.)

    /// <summary>Makes replacing the vault file fail until disposed, without touching its content.</summary>
    private IDisposable BlockReplacement()
    {
        if (OperatingSystem.IsWindows())
        {
            // An open handle without sharing blocks the rename over the file.
            return new FileStream(_vaultPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        // A directory that cannot be written to blocks creating the temporary file next to it.
        UnixFileMode before = File.GetUnixFileMode(_tempDir);
        File.SetUnixFileMode(_tempDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_tempDir, before);
            }
        });
    }

    private string[] FileNames() =>
        [.. Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public async Task FailedWrite_BeforeAnyVaultExists_LeavesNothingBehind()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync(MakeVault(), cts.Token));

        Assert.Empty(FileNames());
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
