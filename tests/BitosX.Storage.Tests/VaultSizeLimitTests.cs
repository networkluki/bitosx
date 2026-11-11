using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Storage.Tests;

/// <summary>
/// A change that would make the vault file larger than can be read back is refused before anything
/// is written. Until 0.14.0 it was written, and the vault could then not be opened again: adding
/// many items at once, which an import does, is what makes that reachable.
/// </summary>
public sealed class VaultSizeLimitTests : IDisposable
{
    private const string MasterPassword = "MasterPass-123!";

    private readonly string _tempDir;
    private readonly string _vaultPath;

    public VaultSizeLimitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.SizeLimit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vaultPath = Path.Combine(_tempDir, "vault.bitosx");
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

    private VaultService NewService()
        => new(VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    /// <summary>Items whose notes are as long as an item's notes may be: 16 KiB each.</summary>
    private static List<LoginItem> ItemsWithFullNotes(int count)
    {
        string notes = new('n', 16384);
        return [.. Enumerable.Range(1, count).Select(i => new LoginItem { Name = "Item " + i, Password = "FAKE-password", Notes = notes })];
    }

    [Fact]
    public async Task ManyItemsAtOnce_ThatWouldMakeTheVaultTooLarge_AreRefused_AndTheVaultStillOpens()
    {
        VaultService service = NewService();
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginAsync(new LoginItem { Name = "Kept", Password = "FAKE-kept" });
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        // 900 items of 16 KiB are 14 MiB of content, which is more than 16 MiB once encrypted and encoded
        // (the file holds the content as base64, with every '+' written as an escape: 44 % more).
        await Assert.ThrowsAsync<VaultTooLargeException>(() => service.AddLoginsAsync(_ => ItemsWithFullNotes(900)));

        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath)); // not one of them was written
        Assert.Equal(["Kept"], service.Items.Select(i => i.Name));
        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Equal(["vault.bitosx"], Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName));

        // The session is intact: a smaller addition is stored, and the vault opens again.
        await service.AddLoginsAsync(_ => ItemsWithFullNotes(100));
        Assert.Equal(101, service.Items.Count);

        VaultService reopen = NewService();
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(101, reopen.Items.Count);
    }

    [Fact]
    public async Task OneItemTooMany_IsRefusedTheSameWay()
    {
        VaultService service = NewService();
        await service.CreateVaultAsync(MasterPassword);
        await service.AddLoginsAsync(_ => ItemsWithFullNotes(690)); // just under 16 MiB as a file
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);
        Assert.InRange(before.Length, 15 * 1024 * 1024, FileLocalVaultStorage.MaxFileSizeBytes);

        // Adding them one at a time reaches the limit as well.
        VaultTooLargeException? refused = null;
        int added = 0;
        foreach (LoginItem item in ItemsWithFullNotes(100))
        {
            try
            {
                await service.AddLoginAsync(item);
                added++;
            }
            catch (VaultTooLargeException ex)
            {
                refused = ex;
                break;
            }
        }

        Assert.NotNull(refused);
        Assert.InRange(added, 1, 99);
        Assert.Equal(690 + added, service.Items.Count);
        Assert.True(new FileInfo(_vaultPath).Length <= FileLocalVaultStorage.MaxFileSizeBytes);

        VaultService reopen = NewService();
        await reopen.UnlockAsync(MasterPassword);
        Assert.Equal(690 + added, reopen.Items.Count);
    }
}
