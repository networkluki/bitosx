using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using BitosX.App.Services;
using BitosX.App.Views;
using BitosX.Platform.Clipboard;

namespace BitosX.App.Tests;

/// <summary>
/// The smaller fixes of 0.11.0 as rendered: secrets that cannot be selected past the clipboard
/// protections, where the keyboard focus goes, what a screen reader is told, and the notice shown
/// when BitosX does not start.
/// </summary>
public sealed class PolishUiTests
{
    private const string MasterPassword = "correct horse battery staple";

    private static async Task<WholeApplication> UnlockedAsync()
    {
        var app = new WholeApplication();
        await app.StartAsync(1000, 1400);
        await app.WaitForAsync<CreateVaultView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
        return app;
    }

    private static async Task AddAsync(WholeApplication app, string name, string? password)
    {
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Type("NameBox", name);
        if (password is not null)
        {
            app.Type("PasswordBox", password);
        }

        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
    }

    private static Control? Focused(WholeApplication app) => app.Window.FocusManager?.GetFocusedElement() as Control;

    [Fact]
    public Task RevealedAndGeneratedPasswords_CannotBeSelected_SoTheyLeaveOnlyThroughCopy() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await AddAsync(app, "GitHub", "P@ssw0rd-Strong-123");
        HeadlessUi.Click(app.Window, app.FindInItem<Button>("GitHub", "ShowPasswordButton"));
        await HeadlessUi.WaitUntilAsync(() => app.FindInItem<TextBlock>("GitHub", "ItemPassword").Text == "P@ssw0rd-Strong-123", "the password is shown");

        Assert.IsNotType<SelectableTextBlock>(app.FindInItem<TextBlock>("GitHub", "ItemPassword"));

        app.Press("GeneratorNavButton");
        await app.WaitForAsync<GeneratorPageView>();
        Assert.IsNotType<SelectableTextBlock>(app.Find<TextBlock>("GeneratedPassword"));
        Assert.Equal(256, app.Find<Slider>("LengthSlider").Maximum);
    });

    [Fact]
    public Task AnItemWithoutAPassword_ShowsNoMask() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await AddAsync(app, "Only a name", password: null);

        Assert.False(app.FindInItem<TextBlock>("Only a name", "ItemPassword").IsEffectivelyVisible);
    });

    [Fact]
    public Task TheKeyboard_IsWhereTheNextKeystrokeBelongs() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();

        // The editor opens in its first field; Escape closes it, Enter saves it.
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => Focused(app)?.Name == "NameBox", "the name field has the focus");
        HeadlessUi.Key(app.Window, PhysicalKey.Escape);
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "Escape closes the editor");

        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => Focused(app)?.Name == "NameBox", "the name field has the focus");
        app.Window.KeyTextInput("GitHub");
        HeadlessUi.Key(app.Window, PhysicalKey.Enter);
        await HeadlessUi.WaitUntilAsync(() => app.ItemNames.Contains("GitHub"), "Enter saves");

        // Asking to delete moves the focus to the safe answer on the same card.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("GitHub", "DeleteButton"));
        await HeadlessUi.WaitUntilAsync(() => Focused(app)?.Name == "KeepButton", "Keep has the focus");

        // After a lock, the master password can be typed at once.
        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();
        await HeadlessUi.WaitUntilAsync(() => Focused(app)?.Name == "MasterPasswordBox", "the master password field has the focus");
    });

    [Fact]
    public Task TheSelectedPage_AndEveryItemsActions_AreToldApart() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await AddAsync(app, "GitHub", "pw-one-Strong-1");
        await AddAsync(app, "GitLab", "pw-two-Strong-2");

        string Name(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();
        Assert.Equal("Vault, current page", Name(app.Find<Button>("VaultNavButton")));
        Assert.Equal("Generator", Name(app.Find<Button>("GeneratorNavButton")));
        Assert.Equal("Edit GitHub", Name(app.FindInItem<Button>("GitHub", "EditButton")));
        Assert.Equal("Edit GitLab", Name(app.FindInItem<Button>("GitLab", "EditButton")));
        Assert.Equal("Copy the password of GitLab", Name(app.FindInItem<Button>("GitLab", "CopyPasswordButton")));

        app.Press("GeneratorNavButton");
        await app.WaitForAsync<GeneratorPageView>();
        Assert.Equal("Generator, current page", Name(app.Find<Button>("GeneratorNavButton")));
        Assert.Equal("Vault", Name(app.Find<Button>("VaultNavButton")));
    });

    [Fact]
    public Task StatusMessages_AreAnnouncedToScreenReaders() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(app.Find<TextBlock>("StatusMessage")));

        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(app.Find<TextBlock>("ErrorMessage")));

        app.Type("MasterPasswordBox", MasterPassword);
        app.Press("UnlockButton");
        await app.WaitForAsync<UnlockedShellView>();
        app.Press("AccountNavButton");
        await app.WaitForAsync<AccountPageView>();
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(app.Find<TextBlock>("AccountStatus")));
    });

    [Fact]
    public Task ASearchWithoutMatches_SaysSo_OnThePage() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        Assert.True(app.Shows("EmptyVaultNote"));
        await AddAsync(app, "GitHub", null);

        app.Type("SearchBox", "gitlab");

        await app.WaitForTextAsync("NoMatchesNote", "No logins match “gitlab”.");
        Assert.False(app.Shows("EmptyVaultNote"));
    });

    [Fact]
    public Task TheRestoreConfirmation_SaysTheNextSyncUploadsIt_AndWhereTheRecoveryCopyIs() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();

        string all = string.Join(" ", app.Window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("the restored vault is what the next “Sync now” uploads", all, StringComparison.Ordinal);
        Assert.Contains("vault.bitosx.pre-restore", all, StringComparison.Ordinal);
        Assert.DoesNotContain("never leaves it", all, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheStartupNotice_ShowsTheMessage_AndClosesWithItsButton() => HeadlessUi.RunAsync(() =>
    {
        Window window = StartupNotice.CreateWindow("BitosX", "BitosX is already running on this vault.");
        window.Show();
        HeadlessUi.Settle();

        Assert.Equal("BitosX is already running on this vault.", HeadlessUi.Find<TextBlock>(window, "StartupNoticeText").Text);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        Button ok = HeadlessUi.Find<Button>(window, "StartupNoticeOkButton");
        Assert.True(ok.IsDefault); // Enter closes it too
        HeadlessUi.Click(window, ok);
        HeadlessUi.Settle();

        Assert.True(closed);
        return Task.CompletedTask;
    });
}
