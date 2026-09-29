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
/// Tests for strengthening the key derivation from <see cref="SettingsPageViewModel"/> (0.6.0): what
/// is offered, what is reported, and that the entered password is not kept.
/// </summary>
public sealed class SettingsPageViewModelKeyDerivationTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsPageViewModelKeyDerivationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (SettingsPageViewModel Vm, FakeVaultService Vault, FakeKeyDerivationBenchmark Benchmark) NewVm(
        Argon2Parameters? vaultParameters = null, bool legacyEncoding = false)
    {
        var vault = new FakeVaultService
        {
            KdfParameters = vaultParameters ?? Argon2Parameters.Default,
            UsesLegacyPasswordEncoding = legacyEncoding,
        };
        var benchmark = new FakeKeyDerivationBenchmark();
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);

        var vm = new SettingsPageViewModel(
            vault,
            new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx")),
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Argon2Parameters.Default,
            AppSettings.Default,
            new FakeAppSettingsStore(),
            new AutoLockService(controller, new NullSystemSessionEvents()),
            new NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            new FakeFilePicker(),
            benchmark);

        return (vm, vault, benchmark);
    }

    // ---- What is offered ----

    [Fact]
    public void Offers_OnlySettingsStrongerThanTheVaultHas()
    {
        (SettingsPageViewModel standard, _, _) = NewVm(Argon2Parameters.Default);
        (SettingsPageViewModel strong, _, _) = NewVm(Argon2Parameters.Strong);
        (SettingsPageViewModel maximum, _, _) = NewVm(Argon2Parameters.Maximum);

        Assert.Equal(["Strong", "Maximum"], standard.KeyDerivationChoices.Select(c => c.Profile.Name));
        Assert.Equal(["Maximum"], strong.KeyDerivationChoices.Select(c => c.Profile.Name));
        Assert.Empty(maximum.KeyDerivationChoices);

        Assert.True(standard.CanStrengthenKeyDerivation);
        Assert.False(maximum.CanStrengthenKeyDerivation);
        Assert.Equal("Strong", standard.SelectedKeyDerivation!.Profile.Name); // the smallest step
        Assert.Null(maximum.SelectedKeyDerivation);
    }

    [Fact]
    public void Commands_NeedASelection_AndAPassword()
    {
        (SettingsPageViewModel vm, _, _) = NewVm();

        Assert.True(vm.TestKeyDerivationCommand.CanExecute(null));       // measuring needs no password
        Assert.False(vm.StrengthenKeyDerivationCommand.CanExecute(null));

        vm.StrengthenPassword = "current-master-pw";
        Assert.True(vm.StrengthenKeyDerivationCommand.CanExecute(null));

        vm.SelectedKeyDerivation = null;
        Assert.False(vm.TestKeyDerivationCommand.CanExecute(null));
        Assert.False(vm.StrengthenKeyDerivationCommand.CanExecute(null));
    }

    [Fact]
    public void AtTheStrongestSettings_NothingCanBeRun()
    {
        (SettingsPageViewModel vm, _, _) = NewVm(Argon2Parameters.Maximum);
        vm.StrengthenPassword = "current-master-pw";

        Assert.False(vm.TestKeyDerivationCommand.CanExecute(null));
        Assert.False(vm.StrengthenKeyDerivationCommand.CanExecute(null));
    }

    // ---- Measuring ----

    [Theory]
    [InlineData(210, "210 milliseconds")]
    [InlineData(999, "999 milliseconds")]
    [InlineData(0.2, "1 milliseconds")]       // never reported as nothing
    [InlineData(1500, "seconds")]
    public async Task Measure_ReportsTimeAndMemory_AndChangesNothing(double milliseconds, string expected)
    {
        (SettingsPageViewModel vm, FakeVaultService vault, FakeKeyDerivationBenchmark benchmark) = NewVm();
        benchmark.Elapsed = TimeSpan.FromMilliseconds(milliseconds);

        await vm.TestKeyDerivationCommand.ExecuteAsync(null);

        Assert.Contains(expected, vm.KeyDerivationTestResult!, StringComparison.Ordinal);
        Assert.Contains("256 MiB", vm.KeyDerivationTestResult!, StringComparison.Ordinal);
        Assert.Equal([Argon2Parameters.Strong], benchmark.Measured);
        Assert.Equal(0, vault.StrengthenCount);
        Assert.False(vm.IsKeyDerivationBusy);
    }

    [Fact]
    public async Task Measure_OnADeviceWithoutTheMemory_SaysSo()
    {
        (SettingsPageViewModel vm, _, FakeKeyDerivationBenchmark benchmark) = NewVm();
        benchmark.MeasureThrow = new KeyDerivationResourceException(
            Argon2Parameters.Strong.MemoryKiB, new InsufficientMemoryException());

        await vm.TestKeyDerivationCommand.ExecuteAsync(null);

        Assert.Contains("does not have 256 MiB", vm.KeyDerivationTestResult!, StringComparison.Ordinal);
        Assert.False(vm.IsKeyDerivationBusy);
    }

    // ---- Strengthening ----

    [Fact]
    public async Task Strengthen_Succeeds_ClearsThePassword_AndDescribesTheNewState()
    {
        (SettingsPageViewModel vm, FakeVaultService vault, _) = NewVm(legacyEncoding: true);
        Assert.NotNull(vm.PasswordEncodingNote);
        vm.StrengthenPassword = "current-master-pw";

        await vm.StrengthenKeyDerivationCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.StrengthenCount);
        Assert.Equal(Argon2Parameters.Strong, vault.KdfParameters);
        Assert.Equal(string.Empty, vm.StrengthenPassword);              // entered secret not retained
        Assert.Contains("strengthened to Strong", vm.StrengthenStatus!, StringComparison.Ordinal);
        Assert.Contains("256 MiB", vm.KdfDescription, StringComparison.Ordinal);
        Assert.Equal(["Maximum"], vm.KeyDerivationChoices.Select(c => c.Profile.Name));
        Assert.Null(vm.PasswordEncodingNote);                            // moved to the canonical encoding
        Assert.Null(vm.KeyDerivationTestResult);
        Assert.False(vm.IsKeyDerivationBusy);
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new VaultAuthenticationException(), "incorrect" },
        { new KeyDerivationResourceException(Argon2Parameters.Strong.MemoryKiB, new InsufficientMemoryException()), "256 MiB" },
        { new WeakerKeyDerivationException(), "stronger" },
        { new VaultChangedExternallyException(), "changed on disk" },
        { new VaultInconsistentStateException(), "locked" },
        { new UnsupportedVaultFormatException("bad"), "could not be processed" },
        { new InvalidOperationException("The vault must be unlocked to change its key-derivation settings."), "unlocked" },
        { new UnauthorizedAccessException("read-only"), "permission" },
        { new IOException("disk full"), "Could not save" },
        { new OperationCanceledException(), "locked before" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Strengthen_WhenItFails_SaysWhy_AndClaimsNothing(Exception failure, string expected)
    {
        (SettingsPageViewModel vm, FakeVaultService vault, _) = NewVm();
        vault.OperationThrow = failure;
        vm.StrengthenPassword = "current-master-pw";
        string before = vm.KdfDescription;

        await vm.StrengthenKeyDerivationCommand.ExecuteAsync(null);

        Assert.Equal(1, vault.StrengthenCount);
        Assert.Contains(expected, vm.StrengthenStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strengthened to", vm.StrengthenStatus!, StringComparison.Ordinal);
        Assert.Equal(before, vm.KdfDescription);
        Assert.Equal("current-master-pw", vm.StrengthenPassword);        // kept for a retry
        Assert.False(vm.IsKeyDerivationBusy);
    }

    [Fact]
    public async Task WhileBusy_NothingElseCanBeStarted()
    {
        (SettingsPageViewModel vm, _, _) = NewVm();
        vm.StrengthenPassword = "current-master-pw";
        var observed = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.IsKeyDerivationBusy) && vm.IsKeyDerivationBusy)
            {
                observed.Add(vm.TestKeyDerivationCommand.CanExecute(null));
                observed.Add(vm.StrengthenKeyDerivationCommand.CanExecute(null));
            }
        };

        await vm.StrengthenKeyDerivationCommand.ExecuteAsync(null);

        Assert.Equal([false, false], observed);
    }

    // ---- Notes on the vault's protection ----

    [Fact]
    public void LegacyPasswordEncoding_IsExplained_WithWhatFixesIt()
    {
        (SettingsPageViewModel legacy, _, _) = NewVm(legacyEncoding: true);
        (SettingsPageViewModel canonical, _, _) = NewVm(legacyEncoding: false);

        Assert.Contains("another keyboard", legacy.PasswordEncodingNote!, StringComparison.Ordinal);
        Assert.Contains("strengthening the key derivation", legacy.PasswordEncodingNote!, StringComparison.Ordinal);
        Assert.Null(canonical.PasswordEncodingNote);
    }

    [Fact]
    public void WeakerSettings_PointToTheWayToStrengthenThem()
    {
        (SettingsPageViewModel vm, _, _) = NewVm(new Argon2Parameters(1024, 1, 1, 32));

        Assert.Contains("Strengthen key derivation", vm.KdfWarning!, StringComparison.Ordinal);
        Assert.Equal(3, vm.KeyDerivationChoices.Count);
    }

    [Theory]
    [InlineData(65536, 8)]    // more passes than any profile offers
    [InlineData(131072, 3)]   // between two profiles
    public void UnusualSettings_AreNeverOfferedAStepThatLowersAnything(int memoryKiB, int iterations)
    {
        var current = new Argon2Parameters(memoryKiB, iterations, 4, 32);

        (SettingsPageViewModel vm, _, _) = NewVm(current);

        Assert.All(vm.KeyDerivationChoices, choice => Assert.True(choice.Profile.Parameters.IsStrongerThan(current)));
        Assert.Null(vm.KdfWarning);
    }

    [Fact]
    public async Task AfterChangingThePasswordOrRotating_TheNotesAreRefreshed()
    {
        (SettingsPageViewModel vm, FakeVaultService vault, _) = NewVm(legacyEncoding: true);
        vault.UsesLegacyPasswordEncoding = true;
        vm.RotatePassword = "current-master-pw";
        vault.UsesLegacyPasswordEncoding = false; // what a successful rotation leaves behind

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);

        Assert.Null(vm.PasswordEncodingNote);
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
