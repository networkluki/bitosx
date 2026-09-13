using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Storage.Tests;

/// <summary>
/// End-to-end verification of the local vault milestone using the real cryptographic and
/// file-storage components together (Definition of Done items 2-13).
/// </summary>
public sealed class VaultPersistenceIntegrationTests : IDisposable
{
    // Distinctive per-field markers so a plaintext-leak assertion pinpoints WHICH field leaked.
    private const string MasterPassword = "MasterPass-123!";
    private const string SecretPassword = "PL4INTEXT-SECRET-VALUE-xyz";
    private const string SecretUsername = "very-unique-username-marker";
    private const string SecretItemName = "distinctive-item-name-marker";
    private const string SecretNotes = "confidential-notes-marker-abc";
    private const string SecretUrlHost = "secret-url-marker.example.com";

    private readonly string _tempDir;
    private readonly string _vaultPath;

    public VaultPersistenceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.Integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vaultPath = Path.Combine(_tempDir, "vault.bitosx");
    }

    private VaultService NewService()
        => new(VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    [Fact]
    public async Task FullMilestoneLifecycle_Works_AndFileHasNoPlaintext()
    {
        // 2. Create a vault.
        VaultService create = NewService();
        Assert.Equal(VaultState.NoVault, await create.RefreshStateAsync());
        await create.CreateVaultAsync(MasterPassword);

        // 7. Add a login entry with a distinctive marker in every sensitive field.
        LoginItem login = await create.AddLoginAsync(new LoginItem
        {
            Name = SecretItemName,
            Username = SecretUsername,
            Password = SecretPassword,
            Notes = SecretNotes,
            Urls = [$"https://{SecretUrlHost}/path"],
        });

        // 3. Close the application (drop the service, keep the file).
        create.Lock();

        // 13. The persisted file must not contain any plaintext field value or the master password.
        // NOTE: this is a REGRESSION CHECK against accidental plaintext persistence, NOT a proof of
        // cryptographic security. Absence of these markers only shows the fields were not written
        // verbatim; it says nothing about the strength of the encryption itself.
        string fileContents = await File.ReadAllTextAsync(_vaultPath);
        Assert.DoesNotContain(SecretPassword, fileContents, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretUsername, fileContents, StringComparison.Ordinal);
        Assert.DoesNotContain(MasterPassword, fileContents, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretItemName, fileContents, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretNotes, fileContents, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretUrlHost, fileContents, StringComparison.Ordinal);

        // 4-6. Reopen; correct password unlocks, wrong password is rejected.
        VaultService reopen = NewService();
        Assert.Equal(VaultState.Locked, await reopen.RefreshStateAsync());
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => reopen.UnlockAsync("WrongPassword-000"));
        await reopen.UnlockAsync(MasterPassword);

        LoginItem restored = Assert.IsType<LoginItem>(Assert.Single(reopen.Items));
        Assert.Equal(SecretPassword, restored.Password);
        Assert.Equal(SecretUsername, restored.Username);

        // 8. Edit it.
        await reopen.UpdateItemAsync(restored with { Name = "Example (edited)" });

        // 11-12. Lock, then unlock again.
        reopen.Lock();
        VaultService reopen2 = NewService();
        await reopen2.UnlockAsync(MasterPassword);
        Assert.Equal("Example (edited)", ((LoginItem)Assert.Single(reopen2.Items)).Name);

        // 9. Delete it.
        await reopen2.DeleteItemAsync(login.Id);

        VaultService reopen3 = NewService();
        await reopen3.UnlockAsync(MasterPassword);
        Assert.Empty(reopen3.Items);
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
