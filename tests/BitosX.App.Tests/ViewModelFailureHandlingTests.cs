using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Regression tests (0.5.1): a command must report a failure, not let it escape. An exception that
/// leaves an asynchronous command is unhandled and takes the application — and with it the unlocked
/// session the user was working in — down.
/// </summary>
public sealed class ViewModelFailureHandlingTests : IDisposable
{
    private readonly string _tempDir;

    public ViewModelFailureHandlingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static (VaultPageViewModel Vm, FakeVaultService Vault, FakeClipboardService Clipboard) NewVaultPage()
    {
        var vault = new FakeVaultService();
        vault.Stored.Add(new LoginItem { Name = "Example", Username = "user", Password = "P@ssw0rd-Strong-123" });
        var clipboard = new FakeClipboardService();
        var vm = new VaultPageViewModel(vault, clipboard, new PasswordGenerator());
        vm.RefreshAsync();
        return (vm, vault, clipboard);
    }

    private (SettingsPageViewModel Vm, FakeVaultService Vault, FakeAppSettingsStore Store, AutoLockController Controller) NewSettingsPage(
        Argon2Parameters? vaultParameters = null)
    {
        var vault = new FakeVaultService { KdfParameters = vaultParameters };
        var storage = new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx"));
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        var autoLock = new AutoLockService(controller, new NullSystemSessionEvents());
        var store = new FakeAppSettingsStore();

        var vm = new SettingsPageViewModel(
            vault,
            storage,
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Argon2Parameters.Default,
            store,
            autoLock,
            new NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            new FakeFilePicker(),
            new FakeKeyDerivationBenchmark());

        return (vm, vault, store, controller);
    }

    public static TheoryData<Exception> ServiceFailures =>
    [
        new VaultChangedExternallyException(),
        new VaultInconsistentStateException(),
        new IOException("disk full"),
        new UnauthorizedAccessException("read-only"),
        new ItemNotFoundException(Guid.NewGuid()),
        new InvalidOperationException("The vault is not unlocked."),
        new OperationCanceledException(),
    ];

    [Theory]
    [MemberData(nameof(ServiceFailures))]
    public async Task Delete_WhenTheServiceFails_ReportsIt_AndKeepsTheItemListed(Exception failure)
    {
        (VaultPageViewModel vm, FakeVaultService vault, _) = NewVaultPage();
        vault.OperationThrow = failure;

        vm.RequestDeleteCommand.Execute(vm.Items[0]);
        await vm.DeleteLoginCommand.ExecuteAsync(vm.Items[0]);

        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.NotEqual("Item deleted.", vm.StatusMessage);
        Assert.Single(vm.Items);
    }

    [Theory]
    [MemberData(nameof(ServiceFailures))]
    public async Task ToggleFavorite_WhenTheServiceFails_ReportsIt(Exception failure)
    {
        (VaultPageViewModel vm, FakeVaultService vault, _) = NewVaultPage();
        vault.OperationThrow = failure;

        await vm.ToggleFavoriteCommand.ExecuteAsync(vm.Items[0]);

        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.False(vm.Items[0].Favorite);
    }

    [Fact]
    public async Task StaleVault_TellsTheUserWhatToDo()
    {
        (VaultPageViewModel vm, FakeVaultService vault, _) = NewVaultPage();
        vault.OperationThrow = new VaultChangedExternallyException();

        vm.RequestDeleteCommand.Execute(vm.Items[0]);
        await vm.DeleteLoginCommand.ExecuteAsync(vm.Items[0]);

        Assert.Contains("changed on disk", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reopen", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedFailure_IsReportedWithoutItsDetails()
    {
        (VaultPageViewModel vm, FakeVaultService vault, _) = NewVaultPage();
        vault.OperationThrow = new NotSupportedException("internal detail: C:\\secret\\path");

        vm.RequestDeleteCommand.Execute(vm.Items[0]);
        await vm.DeleteLoginCommand.ExecuteAsync(vm.Items[0]);

        Assert.DoesNotContain("internal detail", vm.StatusMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_WhenItSucceeds_RemovesTheItem()
    {
        (VaultPageViewModel vm, _, _) = NewVaultPage();

        vm.RequestDeleteCommand.Execute(vm.Items[0]);
        await vm.DeleteLoginCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("Item deleted.", vm.StatusMessage);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Copy_WhenTheClipboardIsBusy_ReportsIt_WithoutClaimingSuccess()
    {
        (VaultPageViewModel vm, _, FakeClipboardService clipboard) = NewVaultPage();
        clipboard.CopyThrow = new IOException("The clipboard is in use by another application.");

        await vm.CopyPasswordCommand.ExecuteAsync(vm.Items[0]);
        string? afterPassword = vm.StatusMessage;
        await vm.CopyUsernameCommand.ExecuteAsync(vm.Items[0]);

        Assert.Contains("could not be copied", afterPassword!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be copied", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(clipboard.LastCopied);
    }

    [Fact]
    public async Task Copy_WhenItSucceeds_CopiesTheValue_AndNeverShowsIt()
    {
        (VaultPageViewModel vm, _, FakeClipboardService clipboard) = NewVaultPage();

        await vm.CopyPasswordCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("P@ssw0rd-Strong-123", clipboard.LastCopied);
        Assert.Contains("copied", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("P@ssw0rd-Strong-123", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratorCopy_WhenTheClipboardIsBusy_ReportsIt()
    {
        var clipboard = new FakeClipboardService { CopyThrow = new IOException("busy") };
        var vm = new GeneratorPageViewModel(new PasswordGenerator(), clipboard);

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Contains("could not be copied", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ServiceFailures))]
    public async Task ChangeMasterPassword_WhenTheServiceFails_ReportsIt(Exception failure)
    {
        (SettingsPageViewModel vm, FakeVaultService vault, _, _) = NewSettingsPage();
        vault.OperationThrow = failure;
        vm.CurrentPassword = "current-master-pw";
        vm.NewPassword = "a-new-master-pw";
        vm.ConfirmPassword = "a-new-master-pw";

        // ItemNotFoundException is not something this operation can raise; everything else is.
        if (failure is ItemNotFoundException)
        {
            return;
        }

        await vm.ChangeMasterPasswordCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.ChangePasswordCount);
        Assert.False(string.IsNullOrWhiteSpace(vm.ChangePasswordStatus));
        Assert.NotEqual("Master password changed.", vm.ChangePasswordStatus);
    }

    [Fact]
    public async Task AutoLockSetting_WhenItCannotBeSaved_StillApplies_AndSaysSo()
    {
        (SettingsPageViewModel vm, _, FakeAppSettingsStore store, AutoLockController controller) = NewSettingsPage();
        store.SaveThrow = new IOException("disk full");

        vm.LockOnMinimize = true;
        Assert.True(controller.Settings.LockOnMinimize); // at once, before the write
        await vm.PendingSave;

        Assert.True(controller.Settings.LockOnMinimize);           // in effect for this session
        Assert.False(string.IsNullOrWhiteSpace(vm.AutoLockStatus)); // and the user is told it was not saved
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task AutoLockSetting_WhenSaved_ClearsAnEarlierWarning()
    {
        (SettingsPageViewModel vm, _, FakeAppSettingsStore store, _) = NewSettingsPage();
        store.SaveThrow = new UnauthorizedAccessException("read-only");
        vm.LockOnMinimize = true;
        await vm.PendingSave;
        store.SaveThrow = null;

        vm.InactivityMinutes = 3;
        await vm.PendingSave;

        Assert.Null(vm.AutoLockStatus);
        Assert.Equal(TimeSpan.FromMinutes(3), store.Saved!.InactivityTimeout);
        Assert.True(store.Saved.LockOnMinimize);
    }

    // ---- The Settings page describes the vault that is open ----

    [Fact]
    public void KeyDerivation_DescribesTheOpenVault_NotTheDefaults()
    {
        (SettingsPageViewModel vm, _, _, _) = NewSettingsPage(new Argon2Parameters(131072, 4, 2, 32));

        Assert.Contains("128 MiB", vm.KdfDescription, StringComparison.Ordinal);
        Assert.Contains("4 iterations", vm.KdfDescription, StringComparison.Ordinal);
        Assert.Contains("parallelism 2", vm.KdfDescription, StringComparison.Ordinal);
        Assert.Null(vm.KdfWarning); // stronger than the default
    }

    [Fact]
    public void KeyDerivation_AtTheDefault_ShowsNoWarning()
    {
        (SettingsPageViewModel vm, _, _, _) = NewSettingsPage(Argon2Parameters.Default);

        Assert.Contains("64 MiB", vm.KdfDescription, StringComparison.Ordinal);
        Assert.Null(vm.KdfWarning);
    }

    [Theory]
    [InlineData(1024, 3)]   // less memory
    [InlineData(65536, 1)]  // fewer iterations
    public void KeyDerivation_WeakerThanTheDefault_IsPointedOut(int memoryKiB, int iterations)
    {
        (SettingsPageViewModel vm, _, _, _) = NewSettingsPage(new Argon2Parameters(memoryKiB, iterations, 4, 32));

        Assert.False(string.IsNullOrWhiteSpace(vm.KdfWarning));
        Assert.Contains($"{iterations} iterations", vm.KdfDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMasterPassword_IsRated_AsItIsTyped()
    {
        (SettingsPageViewModel vm, _, _, _) = NewSettingsPage();
        Assert.Equal(string.Empty, vm.NewPasswordStrengthText);

        vm.NewPassword = "Password123!";
        Assert.Equal("Strength: Very weak", vm.NewPasswordStrengthText);

        vm.NewPassword = "hV7#mQ2$xL9@cT4&nB6!";
        Assert.Equal("Strength: Very strong", vm.NewPasswordStrengthText);

        vm.NewPassword = string.Empty;
        Assert.Equal(string.Empty, vm.NewPasswordStrengthText);
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
