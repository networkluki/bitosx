using System.Runtime.Versioning;
using System.Text;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Storage;

namespace BitosX.Storage.Tests;

/// <summary>
/// What is specific to Unix in how the files are written: who may read them, and that a directory
/// is flushed after a file in it was replaced. These run on Linux and macOS; on Windows they are
/// reported as skipped.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixFilesTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _tempDir;
    private readonly string _directory;
    private readonly string _vaultPath;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    public UnixFilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // A directory the storage creates itself, as it does on first use.
        _directory = Path.Combine(_tempDir, "BitosX");
        _vaultPath = Path.Combine(_directory, "vault.bitosx");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private EncryptedVault MakeVault(string plaintext = "{\"schemaVersion\":1,\"items\":[]}")
        => _cryptor.Create(Encoding.UTF8.GetBytes("pw-master"), Encoding.UTF8.GetBytes(plaintext), Argon2Parameters.ForTesting).Encrypted;

    [UnixFact]
    public void Mode_OfTheTests_IsNotWhatIsBeingTested()
    {
        // The checks below would pass for the wrong reason if new files were owner-only anyway.
        string ordinary = Path.Combine(_tempDir, "ordinary.txt");
        File.WriteAllText(ordinary, "x");

        Assert.NotEqual(OwnerOnlyFile, File.GetUnixFileMode(ordinary));
    }

    [UnixFact]
    public async Task TheVault_AndItsDirectory_AreForTheOwnerOnly()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);

        await storage.WriteAsync(MakeVault());
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(_vaultPath));
        Assert.Equal(OwnerOnlyDirectory, File.GetUnixFileMode(_directory));

        // And it stays that way when the file is replaced.
        await storage.WriteAsync(MakeVault("{\"schemaVersion\":1,\"items\":[ ]}"));
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(_vaultPath));
    }

    [UnixFact]
    public void TheSettings_AreForTheOwnerOnly()
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new FileAppSettingsStore(path);

        store.Save(AppSettings.Default);
        store.Save(AppSettings.Default with { LockOnMinimize = true });

        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(path));
        Assert.Equal(OwnerOnlyDirectory, File.GetUnixFileMode(_directory));
    }

    [UnixFact]
    public async Task ABackup_AndTheRecoveryCopy_AreForTheOwnerOnly()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);
        var backup = new FileVaultBackup(_vaultPath);
        await storage.WriteAsync(MakeVault());

        string exported = Path.Combine(_tempDir, "exported", "vault.bitosx-backup");
        await backup.ExportAsync(exported);
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(exported));

        await backup.RestoreAsync(MakeVault("{\"schemaVersion\":1,\"items\":[ ]}"));
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(_vaultPath));
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(_vaultPath + FileVaultBackup.RecoverySuffix));
    }

    [UnixFact]
    public async Task NoTemporaryFile_IsLeftBehind()
    {
        var storage = new FileLocalVaultStorage(_vaultPath);

        await storage.WriteAsync(MakeVault());
        await storage.WriteAsync(MakeVault());

        Assert.Equal(["vault.bitosx"], Directory.EnumerateFiles(_directory).Select(Path.GetFileName));
    }

    // ---- Flushing the directory ----

    [UnixFact]
    public void ADirectory_IsFlushed()
    {
        Assert.True(UnixDirectory.TryFlush(_tempDir));
    }

    [UnixFact]
    public void ADirectoryThatDoesNotExist_IsNotFlushed_AndNothingIsThrown()
    {
        Assert.False(UnixDirectory.TryFlush(Path.Combine(_tempDir, "missing")));
    }

    [UnixFact]
    public void ADirectoryWithANameThatIsNotAscii_IsFlushed()
    {
        string directory = Path.Combine(_tempDir, "räksmörgås-日本語");
        Directory.CreateDirectory(directory);

        Assert.True(UnixDirectory.TryFlush(directory));
    }
}

/// <summary>Flushing a directory where there is nothing to flush.</summary>
public sealed class DirectoryFlushTests
{
    [WindowsFact]
    public void OnWindows_ThereIsNothingToFlush()
    {
        Assert.False(UnixDirectory.TryFlush(Path.GetTempPath()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoDirectory_IsNotFlushed_AndNothingIsThrown(string? directory)
    {
        Assert.False(UnixDirectory.TryFlush(directory));
    }
}
