using Avalonia.Controls;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Tests of the real Create, Unlock and Vault views, run without a display (see
/// <see cref="HeadlessUi"/>), on top of the real vault service, cryptography and file storage in a
/// temporary directory. Non-ASCII text is written with escapes so the form that is typed does not
/// depend on how this file is saved.
/// </summary>
public sealed class VaultFlowUiTests : IDisposable
{
    private const string Composed = "lösenord-räksmörgås";
    private const string Decomposed = "lösenord-räksmörgås";

    private static readonly Argon2Parameters Small = new(8192, 2, 4, 32);

    private readonly string _directory;
    private readonly string _vaultPath;

    public VaultFlowUiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BitosX.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "vault.bitosx");
    }

    private VaultService NewVaultService()
        => new(VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Small, NullLogger<VaultService>.Instance);

    private static string? Text(Control root, string name) => HeadlessUi.Find<TextBlock>(root, name).Text;

    private static bool Shown(Control root, string name) => HeadlessUi.IsShown(HeadlessUi.Find<Control>(root, name));

    // ---- Creating a vault ----

    [Fact]
    public Task CreatingAVault_RatesThePassword_AndCreatesTheFile() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = NewVaultService();
        var viewModel = new CreateVaultViewModel(vault);
        var view = new CreateVaultView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);
        Assert.False(Shown(view, "StrengthText"));

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), "aaaaaaaaaaaaaaaaaaaa");
        Assert.Equal("Strength: Very weak", Text(view, "StrengthText")); // was "Strong" before 0.5.1

        HeadlessUi.Find<TextBox>(view, "MasterPasswordBox").Clear();
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), Decomposed);
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "ConfirmPasswordBox"), Decomposed);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "CreateButton"));
        await viewModel.CreateCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.False(Shown(view, "ErrorMessage"));
        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.True(File.Exists(_vaultPath));
        Assert.False(vault.UsesLegacyPasswordEncoding);
    });

    [Fact]
    public Task CreatingAVault_WithPasswordsThatDoNotMatch_SaysSo_AndCreatesNothing() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = NewVaultService();
        var viewModel = new CreateVaultViewModel(vault);
        var view = new CreateVaultView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), "correct horse battery");
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "ConfirmPasswordBox"), "correct horse batterx");
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "CreateButton"));
        await viewModel.CreateCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.True(Shown(view, "ErrorMessage"));
        Assert.Contains("do not match", Text(view, "ErrorMessage"), StringComparison.Ordinal);
        Assert.False(File.Exists(_vaultPath));
    });

    // ---- Unlocking ----

    [Theory]
    [InlineData(Composed, Decomposed)]
    [InlineData(Decomposed, Composed)]
    [InlineData("mitt hemliga lösenord", "mitt hemliga lösenord")]
    public Task Unlocking_Works_HoweverTheSamePasswordIsTyped(string typedAtCreation, string typedNow)
        => HeadlessUi.RunAsync(async () =>
        {
            using (VaultService creator = NewVaultService())
            {
                await creator.CreateVaultAsync(typedAtCreation);
            }

            using VaultService vault = NewVaultService();
            await vault.RefreshStateAsync();
            var viewModel = new UnlockViewModel(vault);
            var view = new UnlockView { DataContext = viewModel };
            Window window = HeadlessUi.Show(view);

            HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), typedNow);
            HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "UnlockButton"));
            await viewModel.UnlockCommand.ExecutionTask!;
            HeadlessUi.Settle();

            Assert.False(Shown(view, "ErrorMessage"));
            Assert.Equal(VaultState.Unlocked, vault.State);
            Assert.True(string.IsNullOrEmpty(HeadlessUi.Find<TextBox>(view, "MasterPasswordBox").Text));
        });

    [Fact]
    public Task Unlocking_WithTheWrongPassword_ShowsOneGenericError_AndStaysLocked() => HeadlessUi.RunAsync(async () =>
    {
        using (VaultService creator = NewVaultService())
        {
            await creator.CreateVaultAsync(Composed);
        }

        using VaultService vault = NewVaultService();
        await vault.RefreshStateAsync();
        var viewModel = new UnlockViewModel(vault);
        var view = new UnlockView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), "fel-lösenord-här");
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "UnlockButton"));
        await viewModel.UnlockCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.True(Shown(view, "ErrorMessage"));
        Assert.Equal("Incorrect master password, or the vault file has been modified.", Text(view, "ErrorMessage"));
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(string.IsNullOrEmpty(HeadlessUi.Find<TextBox>(view, "MasterPasswordBox").Text)); // the attempt is not kept
    });

    [Fact]
    public Task Unlocking_OnADeviceWithoutTheMemory_SaysSo_RatherThanWrongPassword() => HeadlessUi.RunAsync(async () =>
    {
        var vault = new ThrowingVaultService(
            new KeyDerivationResourceException(Argon2Parameters.Maximum.MemoryKiB, new InsufficientMemoryException()));
        var viewModel = new UnlockViewModel(vault);
        var view = new UnlockView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "MasterPasswordBox"), "correct horse battery");
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "UnlockButton"));
        await viewModel.UnlockCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.Contains("1024 MiB", Text(view, "ErrorMessage"), StringComparison.Ordinal);
        Assert.DoesNotContain("Incorrect", Text(view, "ErrorMessage"), StringComparison.Ordinal);
    });

    // ---- Working in the vault ----

    [Fact]
    public Task AddingALogin_FromTheView_SavesIt() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = NewVaultService();
        await vault.CreateVaultAsync(Composed);
        var viewModel = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        var view = new VaultPageView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "AddLoginButton"));
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "NameBox"), "GitHub");

        // Taken before the button is pressed: a save that is quick has closed the editor by then.
        LoginEditorViewModel editor = viewModel.Editor!;
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "SaveButton"));
        await editor.SaveCommand.ExecutionTask!;
        HeadlessUi.Settle();

        Assert.Null(viewModel.Editor);
        Assert.Equal("Saved.", Text(view, "StatusMessage"));

        using VaultService reopened = NewVaultService();
        await reopened.UnlockAsync(Decomposed);
        Assert.Equal("GitHub", Assert.Single(reopened.Items).Name);
    });

    [Fact]
    public Task Saving_WhenTheVaultChangedOnDisk_IsRefused_AndTheEditorSaysWhatToDo() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = NewVaultService();
        await vault.CreateVaultAsync(Composed);
        using (VaultService elsewhere = NewVaultService())
        {
            // What a second instance, a sync tool or a restored copy would do.
            await elsewhere.UnlockAsync(Composed);
            await elsewhere.AddLoginAsync(new LoginItem { Name = "Added elsewhere" });
        }

        byte[] before = await File.ReadAllBytesAsync(_vaultPath);
        var viewModel = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        var view = new VaultPageView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "AddLoginButton"));
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "NameBox"), "GitHub");
        LoginEditorViewModel editor = viewModel.Editor!;
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "SaveButton"));
        await editor.SaveCommand.ExecutionTask!;
        HeadlessUi.Settle();

        // The editor stays open with what was typed, and says why nothing was saved.
        Assert.Same(editor, viewModel.Editor);
        Assert.True(Shown(view, "ErrorMessage"));
        string error = Text(view, "ErrorMessage")!;
        Assert.Contains("changed on disk", error, StringComparison.Ordinal);
        Assert.Contains("Lock and reopen", error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(_vaultPath));
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

    /// <summary>A locked vault whose unlock fails with a given exception.</summary>
    private sealed class ThrowingVaultService(Exception failure) : IVaultService
    {
        public VaultState State => VaultState.Locked;

#pragma warning disable CS0067 // The state never changes.
        public event EventHandler<VaultState>? StateChanged;
#pragma warning restore CS0067

        public IReadOnlyList<VaultItem> Items => [];
        public Argon2Parameters? KdfParameters => null;
        public bool UsesLegacyPasswordEncoding => false;

        public Task<VaultState> RefreshStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task UnlockAsync(string masterPassword, CancellationToken cancellationToken = default) => Task.FromException(failure);
        public void Lock() { }

        public Task CreateVaultAsync(string masterPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RotateVaultKeyAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StrengthenKeyDerivationAsync(string currentPassword, Argon2Parameters newParameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginItem> AddLoginAsync(LoginItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoginItem>> AddLoginsAsync(Func<IReadOnlyList<VaultItem>, IReadOnlyList<LoginItem>> chooseItems, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<VaultItem>> VerifyMasterPasswordAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultItem> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
