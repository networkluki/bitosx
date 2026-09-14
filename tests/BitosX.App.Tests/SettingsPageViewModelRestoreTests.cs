using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Regression tests for the REAL destructive commit boundary in <see cref="SettingsPageViewModel"/>.
/// They drive the actual restore commands and assert the destructive backup service is invoked only
/// when the commit-side invariants hold (pending preview AND acknowledgement).
/// </summary>
public sealed class SettingsPageViewModelRestoreTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsPageViewModelRestoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (SettingsPageViewModel Vm, FakeVaultBackupService Backup, FakeFilePicker Picker, FakeVaultService Vault) NewVm()
    {
        var vault = new FakeVaultService();
        var storage = new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx"));
        var backup = new FakeVaultBackupService();
        var picker = new FakeFilePicker();
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        var autoLock = new AutoLockService(controller, new NullSystemSessionEvents());

        var vm = new SettingsPageViewModel(
            vault,
            storage,
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Argon2Parameters.ForTesting,
            AppSettings.Default,
            new FakeAppSettingsStore(),
            autoLock,
            new NullSystemSessionEvents(),
            backup,
            picker);

        return (vm, backup, picker, vault);
    }

    // Drives Choose -> Validate so a valid preview is pending (fake prepare succeeds).
    private static async Task ChooseAndValidateAsync(SettingsPageViewModel vm, FakeFilePicker picker)
    {
        picker.OpenPath = "some/backup.bitosx-backup";
        await vm.ChooseRestoreFileCommand.ExecuteAsync(null);
        vm.RestoreBackupPassword = "backup-pw";
        await vm.ValidateRestoreCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Confirm_WithoutPendingRestore_DoesNotCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, _, _) = NewVm();

        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));
        await vm.ConfirmRestoreCommand.ExecuteAsync(null); // invoke handler directly

        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task Confirm_WithPendingButNotAcknowledged_DoesNotCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);

        Assert.True(vm.IsRestorePending);
        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null); // commit-side guard must block
        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task Confirm_WithPendingAndAcknowledged_Commits_ThenResetsState()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);
        vm.RestoreAcknowledged = true;

        Assert.True(vm.ConfirmRestoreCommand.CanExecute(null));
        await vm.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.Equal(1, backup.CommitCount);
        Assert.False(vm.IsRestorePending);      // state reset after success
        Assert.False(vm.RestoreAcknowledged);
    }

    [Fact]
    public async Task Acknowledge_WithoutPreview_CannotEnable_AndDoesNotCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, _, _) = NewVm();

        vm.RestoreAcknowledged = true; // no preview pending
        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task PasswordChange_ClearsStalePreview_AndBlocksCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);
        vm.RestoreAcknowledged = true;
        Assert.True(vm.ConfirmRestoreCommand.CanExecute(null));

        vm.RestoreBackupPassword = "changed-after-validate"; // invalidates the prepared restore

        Assert.False(vm.IsRestorePending);
        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task FileChange_ClearsPending_AndBlocksCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);
        vm.RestoreAcknowledged = true;

        picker.OpenPath = "some/other.bitosx-backup";
        await vm.ChooseRestoreFileCommand.ExecuteAsync(null); // choosing a new file resets confirmation

        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task FailedCommit_InvalidatesState_SoASecondConfirmDoesNothing()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);
        vm.RestoreAcknowledged = true;
        backup.CommitThrow = new IOException("disk full");

        await vm.ConfirmRestoreCommand.ExecuteAsync(null); // commit attempted (throws), then state reset
        Assert.Equal(1, backup.CommitCount);
        Assert.False(vm.IsRestorePending);
        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null); // must not re-commit stale state
        Assert.Equal(1, backup.CommitCount);
    }

    [Fact]
    public async Task FailedValidation_LeavesNoEnabledDestructiveState()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        backup.PrepareThrow = new VaultAuthenticationException();

        picker.OpenPath = "some/backup.bitosx-backup";
        await vm.ChooseRestoreFileCommand.ExecuteAsync(null);
        vm.RestoreBackupPassword = "wrong";
        await vm.ValidateRestoreCommand.ExecuteAsync(null); // fails closed

        Assert.False(vm.IsRestorePending);
        Assert.False(vm.RestoreAcknowledged);
        Assert.False(vm.ConfirmRestoreCommand.CanExecute(null));

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, backup.CommitCount);
    }

    [Fact]
    public async Task Cancel_ClearsPendingRestore_AndBlocksCommit()
    {
        (SettingsPageViewModel vm, FakeVaultBackupService backup, FakeFilePicker picker, _) = NewVm();
        await ChooseAndValidateAsync(vm, picker);
        vm.RestoreAcknowledged = true;

        vm.CancelRestoreCommand.Execute(null);

        Assert.False(vm.IsRestorePending);
        Assert.False(vm.RestoreAcknowledged);
        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, backup.CommitCount);
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
