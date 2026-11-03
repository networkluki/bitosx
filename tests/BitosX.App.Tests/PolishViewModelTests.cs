using System.Globalization;
using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Platform.Session;
using BitosX.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// The smaller fixes of 0.11.0, at the view-model layer: URLs one per line, the editor's Generate
/// button with the Generator page's settings, search and order of the vault list, and the Settings
/// page's empty fields, progress and file dialogs.
/// </summary>
public sealed class PolishViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "BitosX.PolishTests", Guid.NewGuid().ToString("N"));

    public PolishViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the login editor ----

    private static async Task<LoginItem> SaveAsync(LoginEditorViewModel editor, Func<LoginItem?> saved)
    {
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        return saved() ?? throw new InvalidOperationException("nothing was saved");
    }

    [Fact]
    public async Task UrlsAreOnePerLine_AndACommaASemicolonOrASpaceInsideOneIsKept()
    {
        LoginItem? saved = null;
        var editor = new LoginEditorViewModel(null, new PasswordGenerator(), item => { saved = item; return Task.CompletedTask; }, () => { })
        {
            Name = "Site",
            UrlsText = "https://example.com/a,b\r\n https://host/app;jsessionid=x \nhttps://sso.example.com/?next=https://a.com,https://b.com\r\rhttps://example.com/with%20space",
        };

        LoginItem item = await SaveAsync(editor, () => saved);

        Assert.Equal(
            ["https://example.com/a,b", "https://host/app;jsessionid=x", "https://sso.example.com/?next=https://a.com,https://b.com", "https://example.com/with%20space"],
            item.Urls);
    }

    [Fact]
    public async Task AnExistingItem_CanBeSaved_WithoutItsUrlsChanging()
    {
        var existing = new LoginItem { Name = "Saved on the web", Password = "old", Urls = ["https://example.com/a,b", "https://host/app;jsessionid=x"] };
        LoginItem? saved = null;
        var editor = new LoginEditorViewModel(existing, new PasswordGenerator(), item => { saved = item; return Task.CompletedTask; }, () => { })
        {
            Password = "a new password",
        };

        LoginItem item = await SaveAsync(editor, () => saved);

        Assert.Equal(existing.Urls, item.Urls);
    }

    [Fact]
    public void TheEditorsGenerateButton_UsesTheGeneratorPagesSettings()
    {
        var settings = new GeneratorSettings();
        var page = new GeneratorPageViewModel(new PasswordGenerator(), new FakeClipboardService(), settings)
        {
            Length = 32,
        };
        page.IncludeSymbols = false;

        var editor = new LoginEditorViewModel(null, new PasswordGenerator(), _ => Task.CompletedTask, () => { }, settings);
        editor.GeneratePasswordCommand.Execute(null);

        Assert.Equal(32, editor.Password.Length);
        Assert.All(editor.Password, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void TheGeneratorPage_OpensWithTheSettingsLastUsed()
    {
        var settings = new GeneratorSettings();
        _ = new GeneratorPageViewModel(new PasswordGenerator(), new FakeClipboardService(), settings) { Length = 40, ExcludeAmbiguous = true };

        var again = new GeneratorPageViewModel(new PasswordGenerator(), new FakeClipboardService(), settings);

        Assert.Equal(40, again.Length);
        Assert.True(again.ExcludeAmbiguous);
        Assert.Equal(40, again.GeneratedPassword.Length);
    }

    [Fact]
    public void TheGenerator_ReachesTheLongestPasswordTheCoreAllows()
    {
        var page = new GeneratorPageViewModel(new PasswordGenerator(), new FakeClipboardService())
        {
            Length = PasswordGenerationOptions.MaxLength,
        };

        Assert.Equal(256, page.GeneratedPassword.Length);
        Assert.Null(page.ErrorMessage);
    }

    // ---- the vault list ----

    private static VaultPageViewModel VaultPage(params LoginItem[] items)
    {
        var vault = new FakeVaultService();
        vault.Stored.AddRange(items);
        var page = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        page.RefreshAsync();
        return page;
    }

    [Fact]
    public void Search_MatchesEveryUrlOfAnItem_NotOnlyTheFirst()
    {
        VaultPageViewModel page = VaultPage(new LoginItem { Name = "Example", Urls = ["https://accounts.example.com", "https://mail.example.com"] });

        page.SearchText = "mail";

        Assert.Equal("Example", Assert.Single(page.Items).Name);
    }

    [Fact]
    public void ASearchWithoutMatches_SaysSo_RatherThanThatTheVaultIsEmpty()
    {
        VaultPageViewModel page = VaultPage(new LoginItem { Name = "GitHub" });
        Assert.False(page.IsVaultEmpty);

        page.SearchText = "gitlab";

        Assert.True(page.HasNoMatches);
        Assert.False(page.IsVaultEmpty);
        Assert.Equal("No logins match “gitlab”.", page.NoMatchesText);

        VaultPageViewModel empty = VaultPage();
        Assert.True(empty.IsVaultEmpty);
        Assert.False(empty.HasNoMatches);
    }

    [Fact]
    public void TheList_IsInTheOrderOfTheUsersLanguage()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            VaultPageViewModel page = VaultPage(
                new LoginItem { Name = "Ärende" }, new LoginItem { Name = "Åhléns" }, new LoginItem { Name = "Zoom" }, new LoginItem { Name = "Öppet" }, new LoginItem { Name = "éclair" });

            Assert.Equal(["éclair", "Zoom", "Åhléns", "Ärende", "Öppet"], page.Items.Select(i => i.Name));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void ActionNames_SayWhichItemTheyActOn()
    {
        var item = new LoginItemViewModel(new LoginItem { Name = "GitHub", Username = "octocat", Password = "pw" });

        Assert.Equal("Edit GitHub", item.EditLabel);
        Assert.Equal("Delete GitHub", item.DeleteLabel);
        Assert.Equal("Copy the password of GitHub", item.CopyPasswordLabel);
        Assert.Equal("Show the password of GitHub", item.PasswordToggleLabel);
        item.IsPasswordVisible = true;
        Assert.Equal("Hide the password of GitHub", item.PasswordToggleLabel);
        Assert.Equal("Add GitHub to favorites", item.FavoriteDescription);
    }

    // ---- the Settings page ----

    private (SettingsPageViewModel Vm, FakeVaultService Vault, FakeFilePicker Files) Settings()
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        var files = new FakeFilePicker();
        var vm = new SettingsPageViewModel(
            vault,
            new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx")),
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Argon2Parameters.Default,
            new FakeAppSettingsStore(),
            new AutoLockService(controller, new NullSystemSessionEvents()),
            new NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            files,
            new FakeKeyDerivationBenchmark());
        return (vm, vault, files);
    }

    [Fact]
    public async Task AnEmptyPasswordField_AsksForThePassword_AndTriesNothing()
    {
        (SettingsPageViewModel vm, FakeVaultService vault, FakeFilePicker files) = Settings();

        await vm.RotateVaultKeyCommand.ExecuteAsync(null);
        Assert.Equal("Enter your current master password to rotate the vault key.", vm.RotateStatus);
        Assert.Equal(0, vault.RotateCount);

        vm.NewPassword = vm.ConfirmPassword = "a new master password";
        await vm.ChangeMasterPasswordCommand.ExecuteAsync(null);
        Assert.Equal("Enter your current master password.", vm.ChangePasswordStatus);
        Assert.Equal(0, vault.ChangePasswordCount);

        files.OpenPath = Path.Combine(_tempDir, "backup.bitosx-backup");
        await vm.ChooseRestoreFileCommand.ExecuteAsync(null);
        await vm.ValidateRestoreCommand.ExecuteAsync(null);
        Assert.Equal("Enter the master password of the backup.", vm.RestoreStatus);
    }

    [Fact]
    public async Task ALocationThatIsNotOnThisComputer_IsSaid_NotSilentlyIgnored()
    {
        (SettingsPageViewModel vm, _, FakeFilePicker files) = Settings();
        files.ChoosesANonLocalLocation = true;

        await vm.ExportBackupCommand.ExecuteAsync(null);
        Assert.Contains("not a folder on this computer", vm.BackupStatus, StringComparison.Ordinal);

        await vm.ChooseRestoreFileCommand.ExecuteAsync(null);
        Assert.Contains("not a folder on this computer", vm.RestoreStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutText_SaysWhatSyncSends()
    {
        var vault = new FakeVaultService { KdfParameters = Argon2Parameters.Default };
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        var server = new ScriptedServer();
        var vm = new SettingsPageViewModel(
            vault, new FileLocalVaultStorage(Path.Combine(_tempDir, "vault.bitosx")), new FakeClipboardService(), server,
            Argon2Parameters.Default, new FakeAppSettingsStore(), new AutoLockService(controller, new NullSystemSessionEvents()),
            new NullSystemSessionEvents(), new FakeVaultBackupService(), new FakeFilePicker(), new FakeKeyDerivationBenchmark());

        Assert.Contains("encrypted file", vm.ServerIntegrationDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("your vault never", vm.ServerIntegrationDescription, StringComparison.Ordinal);
    }

    // ---- the Unlock screen ----

    private sealed class VanishingVault : IVaultService
    {
        public VaultState State { get; private set; } = VaultState.Locked;
        public bool FileIsBack { get; set; }
        public event EventHandler<VaultState>? StateChanged;
        public IReadOnlyList<VaultItem> Items => [];
        public Argon2Parameters? KdfParameters => null;
        public bool UsesLegacyPasswordEncoding => false;

        public Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default)
            => Task.FromException(new FileNotFoundException("No vault file was found."));

        public Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default)
        {
            State = FileIsBack ? VaultState.Locked : VaultState.NoVault;
            StateChanged?.Invoke(this, State);
            return Task.FromResult(State);
        }

        public void Lock() => throw new NotSupportedException();
        public Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StrengthenKeyDerivationAsync(string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AVaultFileThatDisappeared_IsSaidWithItsPath_AndCheckingAgainLeadsOn()
    {
        var vault = new VanishingVault();
        AppLocation location = AppLocation.In(_tempDir);
        var vm = new UnlockViewModel(vault, location) { MasterPassword = "whatever" };

        await vm.UnlockCommand.ExecuteAsync(null);

        Assert.True(vm.IsVaultMissing);
        Assert.Contains(location.VaultFilePath, vm.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Incorrect", vm.ErrorMessage, StringComparison.Ordinal);

        vault.FileIsBack = true; // the drive was connected again
        await vm.CheckAgainCommand.ExecuteAsync(null);
        Assert.False(vm.IsVaultMissing);
        Assert.Equal(VaultState.Locked, vault.State);

        vault.FileIsBack = false;
        await vm.UnlockCommand.ExecuteAsync(null);
        await vm.CheckAgainCommand.ExecuteAsync(null);
        Assert.Equal(VaultState.NoVault, vault.State); // the main window now shows the screen for creating a vault
    }
}
