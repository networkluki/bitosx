using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Tests for the Rotate Vault Key command on <see cref="SettingsPageViewModel"/>: it forwards the
/// current password to the service and surfaces success/failure without retaining the entered secret.
/// </summary>
public sealed class SettingsPageViewModelRotationTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsPageViewModelRotationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (SettingsPageViewModel Vm, FakeVaultService Vault) NewVm()
    {
        var vault = new FakeVaultService();
        var storage = new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx"));
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
            new FakeVaultBackupService(),
            new FakeFilePicker(),
            new FakeKeyDerivationBenchmark());

        return (vm, vault);
    }

    [Fact]
    public async Task Rotate_InvokesService_ClearsPassword_AndReportsSuccess()
    {
        (SettingsPageViewModel vm, FakeVaultService vault) = NewVm();
        vm.RotatePassword = "current-master-pw";

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.RotateCount);
        Assert.Equal(string.Empty, vm.RotatePassword);      // entered secret not retained
        Assert.NotNull(vm.RotateStatus);
        Assert.Contains("rotated", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotate_WrongPassword_ShowsError_AndKeepsPasswordFieldForRetry()
    {
        (SettingsPageViewModel vm, FakeVaultService vault) = NewVm();
        vault.RotateThrow = new VaultAuthenticationException();
        vm.RotatePassword = "wrong-pw";

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.RotateCount);
        Assert.Contains("incorrect", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
        // On failure the field is left as-is (not cleared), so the message reflects a real attempt.
        Assert.Equal("wrong-pw", vm.RotatePassword);
    }

    [Fact]
    public async Task Rotate_VaultChangedOnDisk_ShowsSafeLockAndReopenMessage()
    {
        (SettingsPageViewModel vm, FakeVaultService vault) = NewVm();
        vault.RotateThrow = new VaultChangedExternallyException();
        vm.RotatePassword = "current-master-pw";

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.RotateCount);
        // Safe, non-secret guidance; no keys/hashes/plaintext.
        Assert.Contains("changed on disk", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lock and reopen", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotate_IndeterminateState_ShowsSafeLockedReopenMessage()
    {
        (SettingsPageViewModel vm, FakeVaultService vault) = NewVm();
        vault.RotateThrow = new VaultInconsistentStateException();
        vm.RotatePassword = "current-master-pw";

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.RotateCount);
        Assert.Contains("has been locked", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reopen", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotate_SaveFailure_ShowsNotRotatedError()
    {
        (SettingsPageViewModel vm, FakeVaultService vault) = NewVm();
        vault.RotateThrow = new IOException("disk full");
        vm.RotatePassword = "current-master-pw";

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.RotateCount);
        Assert.Contains("not rotated", vm.RotateStatus!, StringComparison.OrdinalIgnoreCase);
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
