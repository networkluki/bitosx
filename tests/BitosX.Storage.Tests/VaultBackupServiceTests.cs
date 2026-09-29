using System.Text.Json.Nodes;
using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Storage.Tests;

/// <summary>
/// End-to-end backup/restore tests using the real file storage, backup store, cryptor, and vault
/// service (Approach A: byte-for-byte encrypted envelope copy).
/// </summary>
public sealed class VaultBackupServiceTests : IDisposable
{
    private const string MasterPassword = "Master-Pass-123!";
    private const string SecretPassword = "PLAINTEXT-SECRET-pw-xyz";
    private const string SecretUsername = "unique-username-marker";
    private const string SecretNotes = "confidential-notes-marker";

    private readonly string _tempDir;
    private readonly string _vaultPath;
    private readonly string _backupPath;
    private readonly VaultCryptor _cryptor = VaultCryptor.CreateDefault();

    public VaultBackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vaultPath = Path.Combine(_tempDir, "vault.bitosx");
        _backupPath = Path.Combine(_tempDir, "backup.bitosx-backup");
    }

    private (VaultService Service, VaultBackupService Backup) NewServices(string? vaultPath = null)
    {
        string path = vaultPath ?? _vaultPath;
        var storage = new FileLocalVaultStorage(path);
        var store = new FileVaultBackup(path);
        var svc = new VaultService(_cryptor, storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        var backup = new VaultBackupService(svc, store, _cryptor, NullLogger<VaultBackupService>.Instance);
        return (svc, backup);
    }

    private static LoginItem SampleLogin() => new()
    {
        Name = "Example",
        Username = SecretUsername,
        Password = SecretPassword,
        Notes = SecretNotes,
    };

    private static void TamperField(string path, string field, JsonNode? value)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        node[field] = value;
        File.WriteAllText(path, node.ToJsonString());
    }

    // ---- Export ----

    [Fact]
    public async Task Export_CreatesByteForByteCopy_WithoutModifyingVault()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());

        byte[] vaultBefore = await File.ReadAllBytesAsync(_vaultPath);
        await backup.ExportAsync(_backupPath);
        byte[] vaultAfter = await File.ReadAllBytesAsync(_vaultPath);
        byte[] backupBytes = await File.ReadAllBytesAsync(_backupPath);

        Assert.Equal(vaultBefore, vaultAfter);  // active vault untouched
        Assert.Equal(vaultBefore, backupBytes); // byte-for-byte copy
    }

    [Fact]
    public async Task Export_ContainsNoPlaintextMarkers()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath);

        string text = await File.ReadAllTextAsync(_backupPath);
        Assert.DoesNotContain(SecretPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretUsername, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretNotes, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MasterPassword, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WhileLocked_Throws_AndWritesNothing()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        svc.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => backup.ExportAsync(_backupPath));
        Assert.False(File.Exists(_backupPath));
    }

    // ---- Restore (happy path + replace semantics) ----

    [Fact]
    public async Task Restore_ReplacesVault_AndUnlocksWithBackupPassword()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath); // backup captures the 1-item state

        // Change the live vault after the backup was made.
        await svc.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });
        Assert.Equal(2, svc.Items.Count);

        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, MasterPassword);
        Assert.Equal(1, preview.ItemCount);
        Assert.Equal(2, svc.Items.Count); // prepare did NOT modify the live vault

        await backup.CommitRestoreAsync(preview);
        Assert.NotEqual(VaultState.Unlocked, svc.State); // locked after restore

        await svc.UnlockAsync(MasterPassword);
        Assert.Single(svc.Items); // post-backup change is gone
    }

    [Fact]
    public async Task Restore_CreatesRecoveryCopyOfPreviousVault()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath);
        await svc.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });

        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, MasterPassword);
        await backup.CommitRestoreAsync(preview);

        string recovery = _vaultPath + FileVaultBackup.RecoverySuffix;
        Assert.True(File.Exists(recovery)); // previous (2-item) vault preserved

        // The recovery copy is a valid vault that still unlocks and holds the pre-restore state.
        var recoveryStorage = new FileLocalVaultStorage(recovery);
        var recoverySvc = new VaultService(_cryptor, recoveryStorage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await recoverySvc.UnlockAsync(MasterPassword);
        Assert.Equal(2, recoverySvc.Items.Count);
    }

    // ---- Restore (attacker-controlled input; live vault must survive) ----

    [Fact]
    public async Task Restore_WrongBackupPassword_LeavesVaultUnchanged()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath);

        byte[] before = await File.ReadAllBytesAsync(_vaultPath);
        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => backup.PrepareRestoreAsync(_backupPath, "wrong-password"));
        byte[] after = await File.ReadAllBytesAsync(_vaultPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Restore_CorruptedCiphertext_FailsClosed_VaultUnchanged()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath);

        // Flip one base64 char of the ciphertext (stays valid base64, changes bytes -> tag mismatch).
        var node = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(_backupPath))!;
        string ct = node["ciphertext"]!.GetValue<string>();
        char first = ct[0] == 'A' ? 'B' : 'A';
        node["ciphertext"] = first + ct[1..];
        await File.WriteAllTextAsync(_backupPath, node.ToJsonString());

        byte[] before = await File.ReadAllBytesAsync(_vaultPath);
        await Assert.ThrowsAnyAsync<BitosXCryptographicException>(
            () => backup.PrepareRestoreAsync(_backupPath, MasterPassword));
        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
    }

    [Fact]
    public async Task Restore_UnsupportedVersion_FailsClosed_VaultUnchanged()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await backup.ExportAsync(_backupPath);
        TamperField(_backupPath, "version", JsonValue.Create(999));

        byte[] before = await File.ReadAllBytesAsync(_vaultPath);
        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => backup.PrepareRestoreAsync(_backupPath, MasterPassword));
        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
    }

    [Fact]
    public async Task Restore_MaliciousKdfParameters_RejectedBeforeWork()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await backup.ExportAsync(_backupPath);
        var kdf = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(_backupPath))!["kdfParameters"]!.DeepClone();
        kdf["memoryKiB"] = 2_000_000_000; // ~2 TiB
        TamperField(_backupPath, "kdfParameters", kdf);

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => backup.PrepareRestoreAsync(_backupPath, MasterPassword));
    }

    [Fact]
    public async Task Restore_OversizedBackup_Rejected()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);

        await using (var fs = new FileStream(_backupPath, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(FileLocalVaultStorage.MaxFileSizeBytes + 1);
        }

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => backup.PrepareRestoreAsync(_backupPath, MasterPassword));
    }

    [Fact]
    public async Task Restore_MalformedJson_Rejected()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await File.WriteAllTextAsync(_backupPath, "this is not a backup");

        await Assert.ThrowsAsync<UnsupportedVaultFormatException>(
            () => backup.PrepareRestoreAsync(_backupPath, MasterPassword));
    }

    // ---- Master-password history ----

    [Fact]
    public async Task Restore_PreChangeBackup_RequiresOldPassword()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync("PasswordAAA-1");
        await svc.AddLoginAsync(SampleLogin());
        await backup.ExportAsync(_backupPath);              // backup encrypted under the OLD password

        await svc.ChangeMasterPasswordAsync("PasswordAAA-1", "PasswordBBB-2"); // live vault now uses NEW password

        // The backup still requires the OLD password; the new one fails.
        await Assert.ThrowsAsync<VaultAuthenticationException>(
            () => backup.PrepareRestoreAsync(_backupPath, "PasswordBBB-2"));
        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, "PasswordAAA-1");
        Assert.Equal(1, preview.ItemCount);
    }

    // ---- Foreign vault: replace-only, no merge ----

    [Fact]
    public async Task Restore_ForeignVault_ReplacesWithoutMerging()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await svc.AddLoginAsync(SampleLogin());
        await svc.AddLoginAsync(new LoginItem { Name = "Second", Username = "u2", Password = "p2xxxxxx" });
        Assert.Equal(2, svc.Items.Count);

        // A completely separate vault with its own password and a single item.
        string foreignPath = Path.Combine(_tempDir, "foreign.bitosx");
        (VaultService foreignSvc, VaultBackupService foreignBackup) = NewServices(foreignPath);
        await foreignSvc.CreateVaultAsync("ForeignPass-9");
        await foreignSvc.AddLoginAsync(new LoginItem { Name = "Foreign", Username = "fu", Password = "fp-xxxxxx" });
        await foreignBackup.ExportAsync(_backupPath);

        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, "ForeignPass-9");
        Assert.Equal(1, preview.ItemCount); // no merge with the current 2 items
        await backup.CommitRestoreAsync(preview);

        await svc.UnlockAsync("ForeignPass-9"); // active password is now the foreign one
        LoginItem only = Assert.IsType<LoginItem>(Assert.Single(svc.Items));
        Assert.Equal("Foreign", only.Name); // replace-only
    }

    // ---- Hardening (0.5.1) ----

    /// <summary>Writes a backup whose decrypted content is exactly <paramref name="plaintextJson"/>.</summary>
    private async Task WriteBackupWithContentAsync(string plaintextJson)
    {
        EncryptedVault envelope = _cryptor.Create(
            System.Text.Encoding.UTF8.GetBytes(MasterPassword),
            System.Text.Encoding.UTF8.GetBytes(plaintextJson),
            Argon2Parameters.ForTesting).Encrypted;
        await File.WriteAllBytesAsync(_backupPath, EncryptedVaultSerializer.SerializeToUtf8Bytes(envelope));
    }

    private static string ItemJson(string fields) =>
        "{\"schemaVersion\":1,\"items\":[{\"type\":\"login\",\"Id\":\"5b2f0c0e-4f7e-4a53-9d0c-6f1f0a5a7c11\",\"Name\":\"Example\"," + fields + "}]}";

    [Fact]
    public async Task Restore_BackupMadeAfterTheClockWasMovedBack_IsAccepted()
    {
        // Earlier versions could store an item whose UpdatedAt was before its CreatedAt, and then
        // rejected their own backup as invalid. Such a backup must restore.
        await WriteBackupWithContentAsync(ItemJson(
            "\"CreatedAt\":\"2026-06-01T00:00:00+00:00\",\"UpdatedAt\":\"2026-01-01T00:00:00+00:00\""));
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);

        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, MasterPassword);
        await backup.CommitRestoreAsync(preview);
        await svc.UnlockAsync(MasterPassword);

        VaultItem item = Assert.Single(svc.Items);
        Assert.True(item.UpdatedAt >= item.CreatedAt);
        await svc.ToggleFavoriteAsync(item.Id); // and the item is editable again
    }

    [Theory]
    [InlineData("\"Urls\":null")]
    [InlineData("\"Urls\":[\"javascript:alert(1)\"]")]
    [InlineData("\"Password\":\"a\",\"Password\":\"b\"")]
    [InlineData("\"Unknown\":true")]
    public async Task Restore_AuthenticBackupWithInvalidContent_FailsClosed_VaultUnchanged(string fields)
    {
        // Correctly encrypted with the right password, but the content is not a valid vault.
        await WriteBackupWithContentAsync(ItemJson(fields));
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        await Assert.ThrowsAsync<VaultValidationException>(() => backup.PrepareRestoreAsync(_backupPath, MasterPassword));

        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
        Assert.False(File.Exists(_vaultPath + FileVaultBackup.RecoverySuffix));
        Assert.Equal(VaultState.Unlocked, svc.State);
    }

    [Fact]
    public async Task Unlock_AuthenticVaultWithInvalidContent_FailsClosed_AndStaysLocked()
    {
        await WriteBackupWithContentAsync(ItemJson("\"Urls\":null"));
        File.Move(_backupPath, _vaultPath);
        (VaultService svc, _) = NewServices();

        await Assert.ThrowsAsync<VaultValidationException>(() => svc.UnlockAsync(MasterPassword));

        Assert.NotEqual(VaultState.Unlocked, svc.State);
        Assert.Empty(svc.Items);
    }

    [Fact]
    public async Task Export_OverTheVaultFileItself_IsRejected_AndTheVaultIsUntouched()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        await Assert.ThrowsAsync<ArgumentException>(() => backup.ExportAsync(_vaultPath));
        await Assert.ThrowsAsync<ArgumentException>(
            () => backup.ExportAsync(Path.Combine(_tempDir, ".", "vault.bitosx")));

        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
    }

    [Fact]
    public async Task Export_LeavesOnlyTheBackupBehind_AndIgnoresAFileAtThePredictableTempName()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        string exportDir = Path.Combine(_tempDir, "shared");
        Directory.CreateDirectory(exportDir);
        string destination = Path.Combine(exportDir, "backup.bitosx-backup");
        string planted = destination + ".tmp"; // the name versions before 0.5.1 wrote through
        await File.WriteAllTextAsync(planted, "not ours");

        await backup.ExportAsync(destination);
        await backup.ExportAsync(destination); // overwriting an existing backup

        Assert.Equal(await File.ReadAllBytesAsync(_vaultPath), await File.ReadAllBytesAsync(destination));
        Assert.Equal("not ours", await File.ReadAllTextAsync(planted));
        Assert.Equal(2, Directory.GetFiles(exportDir).Length);
    }

    [Fact]
    public async Task Restore_LeavesOnlyTheVaultAndItsRecoveryCopy()
    {
        (VaultService svc, VaultBackupService backup) = NewServices();
        await svc.CreateVaultAsync(MasterPassword);
        await backup.ExportAsync(_backupPath);

        BackupPreview preview = await backup.PrepareRestoreAsync(_backupPath, MasterPassword);
        await backup.CommitRestoreAsync(preview);

        string[] names = [.. Directory.GetFiles(_tempDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(["backup.bitosx-backup", "vault.bitosx", "vault.bitosx.pre-restore"], names);
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
