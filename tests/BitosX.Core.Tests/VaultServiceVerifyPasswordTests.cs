using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.Serialization;
using BitosX.Core.Services;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

/// <summary>
/// Checking the master password of a vault that is already unlocked, which is what an unencrypted
/// export asks for: the unlocked session alone does not authorize it.
/// </summary>
public sealed class VaultServiceVerifyPasswordTests
{
    private const string MasterPassword = "correct horse battery";

    // The Swedish for "password-shrimp sandwich", composed and decomposed: the same password typed on two keyboards.
    private const string Composed = "l\u00F6senord-r\u00E4ksm\u00F6rg\u00E5s";
    private const string Decomposed = "lo\u0308senord-ra\u0308ksmo\u0308rga\u030As";

    private static VaultService CreateService(InstrumentedVaultStorage storage) =>
        new(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);

    private static LoginItem Login(string name) =>
        new() { Name = name, Username = "user", Password = "FAKE-P@ssw0rd-123" };

    private static async Task<(VaultService Service, InstrumentedVaultStorage Storage)> UnlockedAsync(
        string password = MasterPassword)
    {
        var storage = new InstrumentedVaultStorage();
        VaultService service = CreateService(storage);
        await service.CreateVaultAsync(password);
        await service.AddLoginAsync(Login("First"));
        await service.AddLoginAsync(Login("Second"));
        return (service, storage);
    }

    [Fact]
    public async Task TheRightPassword_IsAccepted_AndTheItemsOfThatMomentAreReturned()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        EncryptedVault before = storage.Stored!;
        int writesBefore = storage.WriteCount;

        IReadOnlyList<VaultItem> items = await service.VerifyMasterPasswordAsync(MasterPassword);

        Assert.Equal(["First", "Second"], items.Select(i => i.Name));
        Assert.Same(service.Items, items);

        // Checking changes nothing: no write, the same stored vault, the session as it was.
        Assert.Equal(writesBefore, storage.WriteCount);
        Assert.Same(before, storage.Stored);
        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.True(service.HoldsKeyMaterial);
    }

    [Theory]
    [InlineData("correct horse batterx")]
    [InlineData("Correct horse battery")]
    [InlineData("correct horse battery ")]
    [InlineData("")]
    public async Task AWrongPassword_IsRefused_AndTheVaultStaysOpen(string wrong)
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        int writesBefore = storage.WriteCount;

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.VerifyMasterPasswordAsync(wrong));

        Assert.Equal(VaultState.Unlocked, service.State);
        Assert.Equal(2, service.Items.Count);
        Assert.Equal(writesBefore, storage.WriteCount);
    }

    [Fact]
    public async Task ThePassword_IsAcceptedHoweverItIsTyped()
    {
        (VaultService service, _) = await UnlockedAsync(Composed);

        Assert.Equal(2, (await service.VerifyMasterPasswordAsync(Decomposed)).Count);
        Assert.Equal(2, (await service.VerifyMasterPasswordAsync(Composed)).Count);
    }

    [Fact]
    public async Task AVaultKeyedByAnEarlierVersion_IsVerifiedWithThePasswordAsItWasTyped()
    {
        // As a version before 0.6.0 wrote it: keyed with the decomposed form exactly as typed.
        var vault = new Vault();
        vault.Add(Login("Only"));
        var storage = new InstrumentedVaultStorage();
        storage.ReplaceExternally(VaultCryptor.CreateDefault().Create(
            Encoding.UTF8.GetBytes(Decomposed),
            VaultSerializer.SerializeToUtf8Bytes(vault),
            Argon2Parameters.ForTesting).Encrypted);
        VaultService service = CreateService(storage);
        await service.UnlockAsync(Decomposed);
        Assert.True(service.UsesLegacyPasswordEncoding);

        Assert.Single(await service.VerifyMasterPasswordAsync(Decomposed));
        Assert.True(service.UsesLegacyPasswordEncoding); // verifying does not re-key the vault
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => service.VerifyMasterPasswordAsync("not the password"));
    }

    [Fact]
    public async Task WhileLocked_ItIsRefused()
    {
        (VaultService service, _) = await UnlockedAsync();
        service.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyMasterPasswordAsync(MasterPassword));
    }

    [Fact]
    public async Task WhenTheVaultOnDiskIsNotTheSessionsVault_ItIsRefused_EvenWithTheRightPassword()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        VaultService other = CreateService(storage);
        await other.UnlockAsync(MasterPassword);
        await other.AddLoginAsync(Login("Added elsewhere"));

        // The password opens the file, but the file is not what this session is showing.
        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => service.VerifyMasterPasswordAsync(MasterPassword));
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task WhenTheVaultIsLockedWhileThePasswordIsChecked_TheLockWins()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        storage.OnRead = service.Lock;

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.VerifyMasterPasswordAsync(MasterPassword));

        Assert.Equal(VaultState.Locked, service.State);
        Assert.False(service.HoldsKeyMaterial);
    }

    [Fact]
    public async Task ACancelledCheck_IsCancelled()
    {
        (VaultService service, _) = await UnlockedAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.VerifyMasterPasswordAsync(MasterPassword, cancelled.Token));
        Assert.Equal(VaultState.Unlocked, service.State);
    }

    [Fact]
    public async Task ItWaitsForAChangeThatIsBeingSaved_AndReturnsTheItemsAfterIt()
    {
        (VaultService service, InstrumentedVaultStorage storage) = await UnlockedAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeWriteAsync = () => release.Task;
        Task<LoginItem> saving = service.AddLoginAsync(Login("Third"));

        Task<IReadOnlyList<VaultItem>> verifying = service.VerifyMasterPasswordAsync(MasterPassword);
        Assert.False(verifying.IsCompleted);

        storage.BeforeWriteAsync = null;
        release.SetResult();
        await saving;

        Assert.Equal(["First", "Second", "Third"], (await verifying).Select(i => i.Name));
    }
}
