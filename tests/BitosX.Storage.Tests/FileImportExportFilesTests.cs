using System.Runtime.Versioning;
using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Storage.Tests;

/// <summary>
/// The files of an import and of an unencrypted export, on a real file system: the bound on what is
/// read, how an export is written, and the whole way from a file to the vault and back.
/// </summary>
public sealed class FileImportExportFilesTests : IDisposable
{
    private const string MasterPassword = "MasterPass-123!";
    private const string Rows =
        "name,url,username,password,note\n" +
        "One,https://one.example,fake.one,FAKE-secret-one,note one\n" +
        "Two,two.example,fake.two,\"FAKE,\"\"two\"\"\",\"line one\nline two\"\n";

    private readonly string _tempDir;
    private readonly string _vaultPath;
    private readonly FileImportExportFiles _files;

    public FileImportExportFilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.ImportExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vaultPath = Path.Combine(_tempDir, "data", "vault.bitosx");
        _files = new FileImportExportFiles(_vaultPath);
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

    private string InTemp(string name) => Path.Combine(_tempDir, name);

    private (VaultService Vault, VaultImportService Import, VaultExportService Export) NewServices()
    {
        var vault = new VaultService(
            VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        return (
            vault,
            new VaultImportService(vault, _files, TimeProvider.System, NullLogger<VaultImportService>.Instance),
            new VaultExportService(vault, _files, TimeProvider.System, NullLogger<VaultExportService>.Instance));
    }

    // ---- Reading a file to import ----

    [Fact]
    public async Task AFile_IsReadWhole()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Rows)];
        await File.WriteAllBytesAsync(InTemp("passwords.csv"), content);

        Assert.Equal(content, await _files.ReadImportFileAsync(InTemp("passwords.csv")));
    }

    [Fact]
    public async Task AFileThatIsNotThere_IsSaidToBeMissing()
    {
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => _files.ReadImportFileAsync(InTemp("nowhere.csv")));

        Assert.Equal("The file to import was not found.", missing.Message);
    }

    [Fact]
    public async Task AFileOfExactlyTheLimit_IsRead_AndOneByteMoreIsRefused()
    {
        string atLimit = InTemp("at-limit.csv");
        await using (var stream = new FileStream(atLimit, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ImportLimits.MaxFileBytes);
        }

        Assert.Equal(ImportLimits.MaxFileBytes, (await _files.ReadImportFileAsync(atLimit)).Length);

        string overLimit = InTemp("over-limit.csv");
        await using (var stream = new FileStream(overLimit, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ImportLimits.MaxFileBytes + 1);
        }

        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => _files.ReadImportFileAsync(overLimit));
        Assert.Equal(ImportRefusal.TooLarge, refused.Kind);
    }

    [LinuxFact]
    public async Task AFileWithoutAnEnd_IsRefusedOnceItPassesTheLimit()
    {
        // Its reported size is zero; only counting what is actually read finds the limit.
        // (Linux only: how the other systems answer for a device was not tried.)
        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => _files.ReadImportFileAsync("/dev/zero"));

        Assert.Equal(ImportRefusal.TooLarge, refused.Kind);
    }

    // ---- Writing an export ----

    [Fact]
    public async Task AnExport_IsWritten_AndNothingElseIsLeftBehind()
    {
        string destination = InTemp("export.csv");
        byte[] content = Encoding.UTF8.GetBytes("name,url,username,password,note\r\n");

        await _files.WriteExportFileAsync(destination, content);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.Equal(["export.csv"], Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnExport_ReplacesAFileThatIsThere_Whole()
    {
        string destination = InTemp("export.json");
        await File.WriteAllTextAsync(destination, new string('x', 100_000));

        await _files.WriteExportFileAsync(destination, "{}\n"u8.ToArray());

        Assert.Equal("{}\n", await File.ReadAllTextAsync(destination));
        Assert.Equal(["export.json"], Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnExport_IntoADirectoryThatIsNotThereYet_CreatesIt()
    {
        string destination = Path.Combine(_tempDir, "new", "deeper", "export.csv");

        await _files.WriteExportFileAsync(destination, "x"u8.ToArray());

        Assert.Equal("x", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task TheVaultFileItself_IsNeverWrittenOver()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_vaultPath)!);
        await File.WriteAllTextAsync(_vaultPath, "the vault");

        await Assert.ThrowsAsync<ArgumentException>(() => _files.WriteExportFileAsync(_vaultPath, "x"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _files.WriteExportFileAsync(Path.Combine(_tempDir, "data", "..", "data", "vault.bitosx"), "x"u8.ToArray()));

        Assert.Equal("the vault", await File.ReadAllTextAsync(_vaultPath));
    }

    [Fact]
    public async Task ACancelledExport_WritesNothing_AndLeavesWhatWasThere()
    {
        string destination = InTemp("export.csv");
        await File.WriteAllTextAsync(destination, "what was there");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _files.WriteExportFileAsync(destination, "new content"u8.ToArray(), cancelled.Token));

        Assert.Equal("what was there", await File.ReadAllTextAsync(destination));
        Assert.Equal(["export.csv"], Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task OnUnix_AnExportIsForItsOwnerOnly_AlsoWhenItReplacesAFileThatWasNot()
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // The check below would pass for the wrong reason if new files were owner-only anyway.
        string ordinary = InTemp("ordinary.txt");
        await File.WriteAllTextAsync(ordinary, "x");
        Assert.NotEqual(ownerOnly, File.GetUnixFileMode(ordinary));

        string fresh = InTemp("fresh.csv");
        await _files.WriteExportFileAsync(fresh, "x"u8.ToArray());
        Assert.Equal(ownerOnly, File.GetUnixFileMode(fresh));

        // A file everyone could read, replaced by an export: the export's mode, not the old file's.
        string replaced = InTemp("replaced.json");
        await File.WriteAllTextAsync(replaced, "old");
        File.SetUnixFileMode(replaced, ownerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await _files.WriteExportFileAsync(replaced, "new"u8.ToArray());
        Assert.Equal(ownerOnly, File.GetUnixFileMode(replaced));
    }

    // ---- The whole way, on real files ----

    [Fact]
    public async Task FromAFile_IntoTheVault_AndThereAfterTheVaultIsOpenedAgain()
    {
        (VaultService vault, VaultImportService import, _) = NewServices();
        await vault.CreateVaultAsync(MasterPassword);
        await File.WriteAllTextAsync(InTemp("passwords.csv"), Rows);
        byte[] vaultBefore = await File.ReadAllBytesAsync(_vaultPath);

        ParsedImport file = await import.ReadAsync(InTemp("passwords.csv"));
        ImportPlan plan = await import.PreviewAsync(file, ImportOptions.Default);
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_vaultPath)); // a preview writes nothing

        ImportResult result = await import.ApplyAsync(plan);
        Assert.Equal(2, result.Added);

        // The file that was imported is as it was: the application never writes to it.
        Assert.Equal(Rows, await File.ReadAllTextAsync(InTemp("passwords.csv")));

        (VaultService reopened, _, _) = NewServices();
        await reopened.UnlockAsync(MasterPassword);
        Assert.Equal(["One", "Two"], reopened.Items.Select(i => i.Name));
        var two = (LoginItem)reopened.Items[1];
        Assert.Equal("FAKE,\"two\"", two.Password);
        Assert.Equal(["https://two.example"], two.Urls);
        Assert.Equal("line one\nline two", two.Notes);

        // And nothing of it is in the vault file as text.
        string vaultFile = await File.ReadAllTextAsync(_vaultPath);
        Assert.DoesNotContain("FAKE", vaultFile, StringComparison.Ordinal);
        Assert.DoesNotContain("fake.one", vaultFile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Csv, "export.csv")]
    [InlineData(ExportFormat.BitosXJson, "export.json")]
    public async Task AnExport_NeedsTheMasterPassword_AndImportingItBackAddsNothing(ExportFormat format, string name)
    {
        (VaultService vault, VaultImportService import, VaultExportService export) = NewServices();
        await vault.CreateVaultAsync(MasterPassword);
        await File.WriteAllTextAsync(InTemp("passwords.csv"), Rows);
        await import.ApplyAsync(await import.PreviewAsync(await import.ReadAsync(InTemp("passwords.csv")), ImportOptions.Default));
        string destination = InTemp(name);

        // A wrong password: nothing is written, not even an empty file.
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => export.ExportAsync(destination, format, "not the master password"));
        Assert.False(File.Exists(destination));

        ExportResult result = await export.ExportAsync(destination, format, MasterPassword);
        Assert.Equal(2, result.ItemCount);
        string written = await File.ReadAllTextAsync(destination);
        Assert.Contains("FAKE-secret-one", written, StringComparison.Ordinal); // it is what it says: not encrypted

        byte[] vaultBefore = await File.ReadAllBytesAsync(_vaultPath);
        ImportPlan back = await import.PreviewAsync(await import.ReadAsync(destination), ImportOptions.Default);
        ImportResult applied = await import.ApplyAsync(back);

        Assert.Equal(2, back.Duplicates);
        Assert.Equal(0, back.ToAdd);
        Assert.Equal(0, applied.Added);
        Assert.Equal(2, vault.Items.Count);
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_vaultPath)); // the vault was not written again
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task OnUnix_AnExportedVault_IsForItsOwnerOnly()
    {
        (VaultService vault, _, VaultExportService export) = NewServices();
        await vault.CreateVaultAsync(MasterPassword);
        await vault.AddLoginAsync(new LoginItem { Name = "One", Password = "FAKE-secret-one" });

        foreach ((ExportFormat format, string name) in new[] { (ExportFormat.Csv, "export.csv"), (ExportFormat.BitosXJson, "export.json") })
        {
            string destination = InTemp(name);
            await export.ExportAsync(destination, format, MasterPassword);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
        }
    }

    [Fact]
    public async Task AnExportOverTheVaultFile_IsRefused_AndTheVaultStillOpens()
    {
        (VaultService vault, _, VaultExportService export) = NewServices();
        await vault.CreateVaultAsync(MasterPassword);
        await vault.AddLoginAsync(new LoginItem { Name = "One", Password = "FAKE-secret-one" });
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        await Assert.ThrowsAsync<ArgumentException>(() => export.ExportAsync(_vaultPath, ExportFormat.BitosXJson, MasterPassword));

        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
        (VaultService reopened, _, _) = NewServices();
        await reopened.UnlockAsync(MasterPassword);
        Assert.Single(reopened.Items);
    }

    [Fact]
    public async Task AFileOverTheLimit_IsRefusedAsTooLarge_ThroughTheService()
    {
        (VaultService vault, VaultImportService import, _) = NewServices();
        await vault.CreateVaultAsync(MasterPassword);
        string overLimit = InTemp("over-limit.csv");
        await using (var stream = new FileStream(overLimit, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ImportLimits.MaxFileBytes + 1);
        }

        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => import.ReadAsync(overLimit));

        Assert.Equal(ImportRefusal.TooLarge, refused.Kind);
        Assert.Equal("too_large", refused.Code);
    }
}
