using Avalonia.Controls;
using BitosX.App.Views;

namespace BitosX.App.Tests;

/// <summary>
/// How the screens are laid out, at the size the window opens with and at the smallest size it can
/// be given: no text is cut off, and nothing is outside what is shown unless it can be scrolled
/// to. Each screen is also drawn to a PNG file when <c>BITOSX_VIEW_SNAPSHOTS</c> names a directory
/// (see <see cref="HeadlessUi.Snapshot"/>), so that it can be looked at.
/// </summary>
/// <remarks>
/// The screens are those of the whole application (<see cref="WholeApplication"/>), measured and
/// drawn by the renderer of the running application with its font. What this cannot show is a
/// real window on a real screen: the title bar, display scaling, and the fonts of other systems.
/// </remarks>
public sealed class ViewAppearanceTests
{
    private const string MasterPassword = "correct horse battery staple";

    /// <summary>The size the window opens with, and the smallest it can be given.</summary>
    public static TheoryData<string, double, double> Sizes => new()
    {
        { "default", 1000, 680 },
        { "smallest", 760, 540 },
    };

    private static void AssertLaidOut(WholeApplication app, string screen, string size)
    {
        HeadlessUi.Snapshot(app.Window, $"{screen}-{size}");
        IReadOnlyList<string> problems = ViewLayout.Problems(app.Window);
        Assert.True(problems.Count == 0, $"{screen} ({size}):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static async Task<WholeApplication> StartedAsync(double width, double height)
    {
        var app = new WholeApplication();
        await app.StartAsync(width, height);
        return app;
    }

    private static async Task CreateVaultAsync(WholeApplication app)
    {
        await app.WaitForAsync<CreateVaultView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
    }

    private static async Task AddLoginAsync(WholeApplication app, string name, string username, string password, string url)
    {
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Type("NameBox", name);
        app.Type("UsernameBox", username);
        app.Type("PasswordBox", password);
        app.Type("UrlsBox", url);
        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task CreateVault(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await app.WaitForAsync<CreateVaultView>();
        AssertLaidOut(app, "01-create-empty", size);

        app.Type("MasterPasswordBox", "aaaaaaaaaaaa");
        app.Type("ConfirmPasswordBox", "something else");
        app.Press("CreateButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ErrorMessage"), "the error is shown");
        AssertLaidOut(app, "02-create-error", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task Unlock(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();
        AssertLaidOut(app, "03-unlock", size);

        app.Type("MasterPasswordBox", "not the password");
        app.Press("UnlockButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ErrorMessage"), "the error is shown");
        AssertLaidOut(app, "04-unlock-wrong-password", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task Vault(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        AssertLaidOut(app, "05-vault-empty", size);

        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        AssertLaidOut(app, "06-editor-empty", size);
        app.Press("CancelButton");

        await AddLoginAsync(app, "GitHub", "octocat@example.com", "P@ssw0rd-Strong-123", "https://github.com");
        await AddLoginAsync(
            app,
            "A service with a remarkably long name that goes on and on and on",
            "a.very.long.user.name.that.keeps.going@a-long-domain-name.example.com",
            "x",
            "https://a-long-domain-name.example.com/with/a/long/path/that/keeps/going/and/going");
        AssertLaidOut(app, "07-vault-items", size);

        // A favourite, with its password shown, and the question before an item is deleted.
        HeadlessUi.Click(app.Window, app.FindInItem<Button>("GitHub", "FavoriteButton"));
        await HeadlessUi.WaitUntilAsync(
            () => HeadlessUi.ShownTexts(app.Window).Contains("★"), "the favourite is marked");
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("GitHub", "ShowPasswordButton"));
        HeadlessUi.Press(
            app.Window,
            app.FindInItem<Button>("A service with a remarkably long name that goes on and on and on", "DeleteButton"));
        AssertLaidOut(app, "08-vault-password-shown-and-delete-asked", size);

        HeadlessUi.Press(app.Window, app.FindInItem<Button>("GitHub", "EditButton"));
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Press("GeneratePasswordButton");
        AssertLaidOut(app, "09-editor-filled", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task Account(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        app.Press("AccountNavButton");
        await app.WaitForAsync<AccountPageView>();
        AssertLaidOut(app, "14-account-signed-out", size);

        // Signed in, against the scripted server the test application talks to.
        app.Type("AccountEmailBox", "alice@example.com");
        app.Type("AccountPasswordBox", "correct horse battery");
        app.Press("SignInButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("SessionsPanel"), "the account is shown");
        AssertLaidOut(app, "15-account-signed-in", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task TheNotice(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);

        var guard = (BitosX.App.Services.UnexpectedErrorGuard)app.Services.GetService(typeof(BitosX.App.Services.UnexpectedErrorGuard))!;
        guard.Recover(new InvalidOperationException("for the picture"));
        await app.WaitForAsync<UnlockView>();
        Assert.True(app.Shows("NoticeBar"));
        AssertLaidOut(app, "15-notice-after-an-unexpected-error", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task Generator(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        app.Press("GeneratorNavButton");
        await app.WaitForAsync<GeneratorPageView>();
        AssertLaidOut(app, "10-generator", size);

        app.Find<Slider>("LengthSlider").Value = 128;
        app.Press("CopyButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("StatusMessage"), "the copy is confirmed");
        AssertLaidOut(app, "11-generator-longest", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task SecurityCenter(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        await AddLoginAsync(app, "First", "user", "password", "http://insecure.example.com");
        await AddLoginAsync(app, "Second", "user", "password", "https://example.com");
        await AddLoginAsync(app, "Second", "user", "password", "https://example.com");
        app.Press("SecurityNavButton");
        await app.WaitForAsync<SecurityPageView>();
        AssertLaidOut(app, "12-security-center", size);
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task Settings(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await StartedAsync(width, height);
        await CreateVaultAsync(app);
        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();
        AssertLaidOut(app, "13-settings-top", size);

        // Everything the page can say at once: each action has reported, and a restore is pending.
        app.Type("NewPasswordBox", "aaaaaaaaaaaa");
        app.Press("ChangePasswordButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ChangePasswordStatus"), "the password change reports");
        app.Press("RotateVaultKeyButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("RotateStatus"), "the rotation reports");
        app.Press("TestKeyDerivationButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("KeyDerivationTestResult"), "the measurement reports");
        app.Type("StrengthenPasswordBox", "not the password");
        app.Press("StrengthenKeyDerivationButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("StrengthenStatus"), "the strengthening reports");

        string backup = Path.Combine(app.Directory, "a-backup-with-a-rather-long-file-name-2026-09-29.bitosx-backup");
        app.Files.SavePath = backup;
        app.Press("ExportBackupButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("BackupStatus"), "the export reports");
        app.Files.OpenPath = backup;
        app.Press("ChooseBackupButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("RestorePasswordBox"), "the backup is chosen");
        app.Type("RestorePasswordBox", MasterPassword);
        app.Press("ValidateBackupButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ReplaceVaultButton"), "the restore is offered");

        // A window tall enough for the whole page, so that all of it is drawn and checked at once.
        app.Window.Height = 3200;
        HeadlessUi.Settle();
        AssertLaidOut(app, "14-settings-everything", size);
    });
}
