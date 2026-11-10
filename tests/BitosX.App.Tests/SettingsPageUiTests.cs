using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.Security;
using BitosX.Core.Serialization;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Tests of the real Settings view, run without a display (see <see cref="HeadlessUi"/>), on top of
/// the real vault service, cryptography and file storage in a temporary directory. They type into
/// the view's text boxes and press its buttons, and read what its text blocks show.
/// </summary>
public sealed class SettingsPageUiTests : IDisposable
{
    private const string MasterPassword = "correct horse battery";

    // Small enough to keep the tests quick, and weaker than what new vaults get, so that the
    // view has something to say and something to offer.
    private static readonly Argon2Parameters Small = new(8192, 2, 4, 32);

    private readonly string _directory;
    private readonly string _vaultPath;

    public SettingsPageUiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BitosX.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "vault.bitosx");
    }

    private VaultService NewVaultService()
        => new(VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Small, NullLogger<VaultService>.Instance);

    private SettingsPageViewModel NewViewModel(
        IVaultService vault, IKeyDerivationBenchmark? benchmark = null, FakeAppSettingsStore? settingsStore = null)
    {
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        return new SettingsPageViewModel(
            vault,
            new FileLocalVaultStorage(_vaultPath),
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Argon2Parameters.Default,
            settingsStore ?? new FakeAppSettingsStore(),
            new AutoLockService(controller, new NullSystemSessionEvents()),
            new NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            new FakeFilePicker(),
            benchmark ?? new KeyDerivationBenchmark(new Argon2idKeyDerivation()),
            ImportExportCard.NotUsed(vault));
    }

    private static (Window Window, SettingsPageView View) Show(SettingsPageViewModel viewModel)
    {
        var view = new SettingsPageView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);
        return (window, view);
    }

    private static string? Text(Control root, string name) => HeadlessUi.Find<TextBlock>(root, name).Text;

    private static bool Shown(Control root, string name) => HeadlessUi.IsShown(HeadlessUi.Find<Control>(root, name));

    private async Task<VaultService> UnlockedVaultAsync()
    {
        VaultService vault = NewVaultService();
        await vault.CreateVaultAsync(MasterPassword);
        await vault.AddLoginAsync(new LoginItem { Name = "Example", Username = "user", Password = "P@ssw0rd-Strong-123" });
        return vault;
    }

    // ---- What the page says about the open vault ----

    [Fact]
    public Task ThePage_DescribesTheOpenVault_AndOffersStrongerSettings() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        (_, SettingsPageView view) = Show(NewViewModel(vault));

        Assert.Contains("8 MiB", Text(view, "KdfDescription"), StringComparison.Ordinal);
        Assert.Contains("2 iterations", Text(view, "KdfDescription"), StringComparison.Ordinal);

        Assert.True(Shown(view, "KdfWarning"));
        Assert.Contains("Strengthen key derivation", Text(view, "KdfWarning"), StringComparison.Ordinal);

        Assert.True(Shown(view, "KeyDerivationPanel"));
        Assert.False(Shown(view, "KeyDerivationAtStrongest"));
        ComboBox choices = HeadlessUi.Find<ComboBox>(view, "KeyDerivationChoices");
        Assert.Equal(
            ["Standard — 64 MiB of memory, 3 iterations", "Strong — 256 MiB of memory, 3 iterations", "Maximum — 1024 MiB of memory, 3 iterations"],
            choices.Items.Cast<KeyDerivationChoice>().Select(c => c.Display));
        Assert.Equal(0, choices.SelectedIndex);

        Assert.False(Shown(view, "PasswordEncodingNote"));
        Assert.False(Shown(view, "StrengthenStatus"));
        Assert.False(Shown(view, "KeyDerivationTestResult"));
    });

    [Fact]
    public Task AVaultAtTheDefault_ShowsNoWarning() => HeadlessUi.RunAsync(() =>
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        (_, SettingsPageView view) = Show(NewViewModel(vault));

        Assert.Contains("64 MiB", Text(view, "KdfDescription"), StringComparison.Ordinal);
        Assert.False(Shown(view, "KdfWarning"));
        Assert.Equal(2, HeadlessUi.Find<ComboBox>(view, "KeyDerivationChoices").ItemCount);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AVaultAtTheStrongestSettings_IsToldSo_AndOfferedNothing() => HeadlessUi.RunAsync(() =>
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Maximum };
        (_, SettingsPageView view) = Show(NewViewModel(vault));

        Assert.True(Shown(view, "KeyDerivationAtStrongest"));
        Assert.False(Shown(view, "KeyDerivationPanel"));
        Assert.False(Shown(view, "StrengthenKeyDerivationButton"));
        Assert.False(Shown(view, "KdfWarning"));
        return Task.CompletedTask;
    });

    // ---- Strengthening, from the keyboard ----

    [Fact]
    public Task StrengthenButton_IsDisabled_UntilAPasswordIsTyped() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        (Window window, SettingsPageView view) = Show(NewViewModel(vault));
        Button strengthen = HeadlessUi.Find<Button>(view, "StrengthenKeyDerivationButton");

        Assert.False(strengthen.IsEffectivelyEnabled);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "StrengthenPasswordBox"), MasterPassword);

        Assert.True(strengthen.IsEffectivelyEnabled);
    });

    [Fact]
    public Task Strengthening_WithTheWrongPassword_SaysSo_AndWritesNothing() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        SettingsPageViewModel viewModel = NewViewModel(vault);
        (Window window, SettingsPageView view) = Show(viewModel);
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "StrengthenPasswordBox"), "not the password");
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "StrengthenKeyDerivationButton"));
        await viewModel.StrengthenKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.True(Shown(view, "StrengthenStatus"));
        Assert.Contains("incorrect", Text(view, "StrengthenStatus"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8 MiB", Text(view, "KdfDescription"), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
    });

    [Fact]
    public Task Strengthening_MovesTheVault_AndThePageFollows() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        SettingsPageViewModel viewModel = NewViewModel(vault);
        (Window window, SettingsPageView view) = Show(viewModel);
        TextBox password = HeadlessUi.Find<TextBox>(view, "StrengthenPasswordBox");

        HeadlessUi.Type(window, password, MasterPassword);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "StrengthenKeyDerivationButton"));
        await viewModel.StrengthenKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();

        // The page.
        Assert.Contains("strengthened to Standard", Text(view, "StrengthenStatus"), StringComparison.Ordinal);
        Assert.Contains("64 MiB", Text(view, "KdfDescription"), StringComparison.Ordinal);
        Assert.False(Shown(view, "KdfWarning"));
        Assert.True(string.IsNullOrEmpty(password.Text)); // the entered password is not kept
        Assert.Equal(2, HeadlessUi.Find<ComboBox>(view, "KeyDerivationChoices").ItemCount);

        // The file, read back independently of the session that wrote it.
        EncryptedVault stored = EncryptedVaultSerializer.Deserialize(await File.ReadAllBytesAsync(_vaultPath));
        Assert.Equal(Argon2Parameters.Default, stored.KdfParameters);

        using VaultService reopened = NewVaultService();
        await reopened.UnlockAsync(MasterPassword);
        Assert.Equal("Example", Assert.Single(reopened.Items).Name);
    });

    [Fact]
    public Task Measuring_ShowsWhatTheSettingsCost_AndChangesNothing() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        SettingsPageViewModel viewModel = NewViewModel(vault);
        (Window window, SettingsPageView view) = Show(viewModel);
        byte[] before = await File.ReadAllBytesAsync(_vaultPath);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "TestKeyDerivationButton"));
        await viewModel.TestKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.True(Shown(view, "KeyDerivationTestResult"));
        string result = Text(view, "KeyDerivationTestResult")!;
        Assert.Contains("64 MiB", result, StringComparison.Ordinal);
        Assert.Matches("takes about [0-9.,]+ (milliseconds|seconds)", result);
        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
        Assert.Contains("8 MiB", Text(view, "KdfDescription"), StringComparison.Ordinal);
    });

    [Fact]
    public Task Measuring_OnADeviceWithoutTheMemory_SaysSo() => HeadlessUi.RunAsync(async () =>
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        var benchmark = new FakeKeyDerivationBenchmark
        {
            MeasureThrow = new KeyDerivationResourceException(Argon2Parameters.Strong.MemoryKiB, new InsufficientMemoryException()),
        };
        SettingsPageViewModel viewModel = NewViewModel(vault, benchmark);
        (Window window, SettingsPageView view) = Show(viewModel);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "TestKeyDerivationButton"));
        await viewModel.TestKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.Contains("does not have 256 MiB", Text(view, "KeyDerivationTestResult"), StringComparison.Ordinal);
        Assert.Equal([Argon2Parameters.Strong], benchmark.Measured);
    });

    [Fact]
    public Task AMeasurement_IsClearedWhenOtherSettingsAreSelected() => HeadlessUi.RunAsync(async () =>
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        SettingsPageViewModel viewModel = NewViewModel(vault, new FakeKeyDerivationBenchmark());
        (Window window, SettingsPageView view) = Show(viewModel);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "TestKeyDerivationButton"));
        await viewModel.TestKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();
        Assert.True(Shown(view, "KeyDerivationTestResult"));

        HeadlessUi.Find<ComboBox>(view, "KeyDerivationChoices").SelectedIndex = 1;
        HeadlessUi.Settle();

        Assert.False(Shown(view, "KeyDerivationTestResult"));
        Assert.Equal("Maximum", viewModel.SelectedKeyDerivation!.Profile.Name);
    });

    // ---- Master password encoding ----

    [Fact]
    public Task AVaultKeyedTheOldWay_IsPointedOut_UntilItHasBeenMoved() => HeadlessUi.RunAsync(async () =>
    {
        // As a version before 0.6.0 wrote it: keyed with the password exactly as typed.
        const string asTyped = "mitt hemliga lösenord";
        var storage = new FileLocalVaultStorage(_vaultPath);
        await storage.WriteAsync(VaultCryptor.CreateDefault().Create(
            Encoding.UTF8.GetBytes(asTyped), VaultSerializer.SerializeToUtf8Bytes(new Vault()), Small).Encrypted);
        using VaultService vault = NewVaultService();
        await vault.UnlockAsync(asTyped);

        SettingsPageViewModel viewModel = NewViewModel(vault);
        (Window window, SettingsPageView view) = Show(viewModel);

        Assert.True(Shown(view, "PasswordEncodingNote"));
        Assert.Contains("another keyboard", Text(view, "PasswordEncodingNote"), StringComparison.Ordinal);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "StrengthenPasswordBox"), asTyped);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "StrengthenKeyDerivationButton"));
        await viewModel.StrengthenKeyDerivationCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.False(Shown(view, "PasswordEncodingNote"));

        // And now it opens with ordinary spaces too.
        using VaultService reopened = NewVaultService();
        await reopened.UnlockAsync("mitt hemliga lösenord");
        Assert.Equal(VaultState.Unlocked, reopened.State);
    });

    // ---- Changing the master password ----

    [Fact]
    public Task TheNewPassword_IsRatedAsItIsTyped() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        (Window window, SettingsPageView view) = Show(NewViewModel(vault));
        Assert.False(Shown(view, "NewPasswordStrength"));

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "NewPasswordBox"), "Password123!");

        Assert.True(Shown(view, "NewPasswordStrength"));
        Assert.Equal("Strength: Very weak", Text(view, "NewPasswordStrength"));
    });

    [Fact]
    public Task ChangingTheMasterPassword_FromTheView_Works() => HeadlessUi.RunAsync(async () =>
    {
        const string newPassword = "ett helt annat lösenord";
        using VaultService vault = await UnlockedVaultAsync();
        SettingsPageViewModel viewModel = NewViewModel(vault);
        (Window window, SettingsPageView view) = Show(viewModel);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "CurrentPasswordBox"), MasterPassword);
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "NewPasswordBox"), newPassword);
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "ConfirmPasswordBox"), newPassword);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "ChangePasswordButton"));
        await viewModel.ChangeMasterPasswordCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.Equal("Master password changed.", Text(view, "ChangePasswordStatus"));
        Assert.False(Shown(view, "NewPasswordStrength")); // the fields were emptied

        using VaultService reopened = NewVaultService();
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => reopened.UnlockAsync(MasterPassword));
        await reopened.UnlockAsync("ett helt annat lösenord"); // typed decomposed
        Assert.Single(reopened.Items);
    });

    // ---- Settings that cannot be saved ----

    [Fact]
    public Task ASettingThatCannotBeSaved_IsReported_OnThePage() => HeadlessUi.RunAsync(async () =>
    {
        var store = new FakeAppSettingsStore { SaveThrow = new IOException("disk full") };
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, settingsStore: store));
        Assert.False(Shown(view, "AutoLockStatus"));

        CheckBox lockOnMinimize = view.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Content as string == "Lock when the window is minimized");
        HeadlessUi.Toggle(window, lockOnMinimize);

        Assert.True(lockOnMinimize.IsChecked);
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "AutoLockStatus"), "the failed write is reported");
        Assert.Contains("could not be saved", Text(view, "AutoLockStatus"), StringComparison.Ordinal);
    });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
