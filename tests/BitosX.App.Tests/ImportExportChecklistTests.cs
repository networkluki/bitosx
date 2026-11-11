using System.Globalization;
using System.Text;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using BitosX.App.Services;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;

namespace BitosX.App.Tests;

/// <summary>
/// Import and export on the whole application (<see cref="WholeApplication"/>), without a display:
/// the services as the application wires them, the real cryptography with the settings a new vault
/// gets, the real views. A file is chosen, the preview read, buttons pressed from the keyboard; the
/// vault file and the export file are compared byte for byte.
/// </summary>
/// <remarks>
/// As with the other checklists run this way, nobody looked at a window, and the file dialogs are
/// stood in for: the dialogs of the operating system are not opened.
/// </remarks>
public sealed class ImportExportChecklistTests
{
    private const string MasterPassword = "correct horse battery staple";

    private const string ThreeLogins =
        "name,url,username,password,note\n"
        + "Mail,https://mail.example.com/,fake.user@example.test,FAKE-pw-mail,\n"
        + "Shop,shop.example.com,fake.shopper,FAKE-pw-shop,a note\n"
        + "Bank,https://bank.example.com/,fake.banker,FAKE-pw-bank,\n";

    /// <summary>What a card must not show after the vault was locked and unlocked again.</summary>
    private static readonly string[] GoneAfterALock =
    [
        "ImportPreviewPanel", "ImportFileName", "ImportStatus", "ImportFileReminder", "ExportPanel", "ExportStatus",
    ];

    /// <summary>The lines of a preview that <see cref="EverythingAPreviewCanSay"/> gives a reason for.</summary>
    private static readonly string[] EveryLineButTheColumns =
    [
        "ImportSource", "ImportToAdd", "ImportDuplicates", "ImportNotImported", "ImportUnsupported", "ImportInTrash",
        "ImportEmpty", "ImportExtras", "ImportHttpsAdded", "ImportNamesDerived",
    ];

    // ---- What is done more than once ----

    private static async Task<WholeApplication> UnlockedAsync(double width = 1000, double height = 680)
    {
        var app = new WholeApplication();
        await app.StartAsync(width, height);
        await app.WaitForAsync<CreateVaultView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
        return app;
    }

    private static async Task UnlockAsync(WholeApplication app)
    {
        await app.WaitForAsync<UnlockView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Press("UnlockButton");
        await app.WaitForAsync<UnlockedShellView>();
    }

    private static async Task AddLoginAsync(WholeApplication app, string name, string username, string password, string url)
    {
        app.Press("VaultNavButton");
        await app.WaitForAsync<VaultPageView>();
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Type("NameBox", name);
        app.Type("UsernameBox", username);
        app.Type("PasswordBox", password);
        app.Type("UrlsBox", url);
        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
    }

    private static async Task OpenSettingsAsync(WholeApplication app)
    {
        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();
    }

    private static string Write(WholeApplication app, string name, string content)
    {
        // Next to the application's own files, but not one of them.
        string directory = Path.Combine(app.Directory, "files of the test");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        return path;
    }

    private static async Task ChooseAsync(WholeApplication app, string path)
    {
        app.Files.ImportPath = path;
        app.Press("ChooseImportFileButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ImportPreviewPanel") || app.Shows("ImportStatus"), "the file is read");
    }

    /// <summary>
    /// Presses the button that imports, once it can be pressed: coming back to the page makes the
    /// preview again, and until that is done nothing can be imported.
    /// </summary>
    private static async Task ImportAsync(WholeApplication app)
    {
        await HeadlessUi.WaitUntilAsync(
            () => app.Find<Button>("ConfirmImportButton").IsEffectivelyEnabled, "the preview is the current one");
        app.Press("ConfirmImportButton");
    }

    private static void OpenExport(WholeApplication app, string password)
    {
        app.Press("ExportUnencryptedButton");
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("ExportAcknowledgeCheckBox"));
        app.Type("ExportPasswordBox", password);
    }

    private static string[] Nameless(Window window)
        =>
        [
            .. window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible
                    && c is TextBox or ComboBox or Slider or NumericUpDown or CheckBox or Button
                    && !c.GetVisualAncestors().Any(a => a is TextBox or NumericUpDown or Slider or ComboBox or ScrollBar))
                .Where(c => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(c).GetName()))
                .Select(c => $"{c.GetType().Name} '{c.Name}'"),
        ];

    /// <summary>A Bitwarden export with something for every line of a preview, and names that are long.</summary>
    private static string EverythingAPreviewCanSay()
    {
        string longName = "An entry whose name goes on and on, " + new string('x', 120);
        return $$"""
            {
              "encrypted": false,
              "folders": [ { "id": "f1", "name": "Fake folder" } ],
              "items": [
                { "type": 1, "name": "With everything", "folderId": "f1", "notes": "a note",
                  "fields": [ { "name": "PIN", "value": "FAKE-1234", "type": 1 } ],
                  "login": { "username": "fake.one", "password": "FAKE-pw-1", "totp": "FAKEFAKEFAKEFAKE",
                             "uris": [ { "uri": "https://one.example.com/" }, { "uri": "androidapp://com.example.fake" } ] } },
                { "type": 1, "name": "", "login": { "username": "fake.two", "password": "FAKE-pw-2", "uris": [ { "uri": "two.example.com" } ] } },
                { "type": 1, "name": "With everything", "folderId": "f1", "notes": "another note",
                  "login": { "username": "fake.one", "password": "FAKE-pw-1", "uris": [ { "uri": "https://one.example.com/" } ] } },
                { "type": 1, "name": "{{longName}}", "login": { "username": "fake.four", "password": "FAKE-pw-4", "uris": [ { "uri": "https://" } ] } },
                { "type": 1, "name": "A password that is too long", "login": { "username": "fake.five", "password": "{{new string('p', 4097)}}" } },
                { "type": 1, "name": "", "login": { } },
                { "type": 2, "name": "A secure note", "notes": "FAKE note" },
                { "type": 3, "name": "A card" },
                { "type": 4, "name": "An identity" },
                { "type": 1, "name": "Thrown away", "deletedDate": "2026-01-02T03:04:05Z", "login": { "password": "FAKE-pw-7" } }
              ]
            }
            """;
    }

    // ---- Import ----

    [Fact]
    public Task AnImport_FromChoosingAFile_ToTheVaultPage() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await AddLoginAsync(app, "Bank", "fake.banker", "FAKE-pw-bank", "https://bank.example.com/");
        await OpenSettingsAsync(app);
        byte[] vaultBefore = app.VaultFile;
        string file = Write(app, "passwords.csv", ThreeLogins);

        // Choosing a file reads it and shows what it would do. Nothing is written.
        await ChooseAsync(app, file);
        Assert.Equal("passwords.csv", app.Text("ImportFileName"));
        Assert.Equal("Chrome or Edge (CSV): 3 entries in the file.", app.Text("ImportSource"));
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", app.Text("ImportToAdd"));
        Assert.Equal("1 login is already in the vault, or in the file more than once, and is skipped.", app.Text("ImportDuplicates"));
        Assert.Equal(vaultBefore, app.VaultFile);
        Assert.Single(app.Vault.Items);

        // Looking at another page and coming back does not close it, and writes nothing.
        app.Press("VaultNavButton");
        await app.WaitForAsync<VaultPageView>();
        Assert.Equal(["Bank"], app.ItemNames);
        await OpenSettingsAsync(app);
        Assert.True(app.Shows("ImportPreviewPanel"));
        Assert.Equal(vaultBefore, app.VaultFile);

        // Only the button imports.
        await ImportAsync(app);
        await app.WaitForTextAsync("ImportStatus", "2 logins were added to the vault.");
        Assert.Equal(ImportTexts.FileStillHoldsPasswords, app.Text("ImportFileReminder"));
        Assert.False(app.Shows("ImportPreviewPanel"));
        Assert.NotEqual(vaultBefore, app.VaultFile);

        // What was imported is encrypted like everything else, and nothing was left behind.
        string vaultFile = Encoding.UTF8.GetString(app.VaultFile);
        Assert.DoesNotContain("FAKE-pw-shop", vaultFile, StringComparison.Ordinal);
        Assert.DoesNotContain("shop.example.com", vaultFile, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(app.Directory), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ThreeLogins, File.ReadAllText(file)); // the file is the user's to delete

        // The vault page shows them, by name.
        app.Press("VaultNavButton");
        await app.WaitForAsync<VaultPageView>();
        Assert.Equal(["Bank", "Mail", "Shop"], app.ItemNames);

        // They are still there after a lock and an unlock, and the same file now adds nothing.
        app.Press("LockButton");
        await UnlockAsync(app);
        await HeadlessUi.WaitUntilAsync(() => app.ItemNames.Count == 3, "the vault page lists the logins");
        Assert.Equal(["Bank", "Mail", "Shop"], app.ItemNames);
        LoginItem shop = Assert.Single(app.Vault.Items.OfType<LoginItem>(), i => i.Name == "Shop");
        Assert.Equal("FAKE-pw-shop", shop.Password);
        Assert.Equal(["https://shop.example.com"], shop.Urls);

        await OpenSettingsAsync(app);
        byte[] vaultAfter = app.VaultFile;
        await ChooseAsync(app, file);
        Assert.Equal("Nothing in this file would be added to the vault.", app.Text("ImportToAdd"));
        Assert.Equal("3 logins are already in the vault, or in the file more than once, and are skipped.", app.Text("ImportDuplicates"));
        Assert.False(app.Find<Button>("ConfirmImportButton").IsEffectivelyEnabled);
        Assert.Equal(vaultAfter, app.VaultFile);
    });

    [Fact]
    public Task ALoginAddedOnTheVaultPage_WhileAPreviewIsOpen_IsADuplicateWhenThePageIsComeBackTo() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await OpenSettingsAsync(app);
        await ChooseAsync(app, Write(app, "passwords.csv", ThreeLogins));
        Assert.Equal("3 logins will be added. The vault holds 0 items now and will hold 3.", app.Text("ImportToAdd"));

        await AddLoginAsync(app, "Mail", "fake.user@example.test", "FAKE-pw-mail", "https://mail.example.com/");
        await OpenSettingsAsync(app);

        await app.WaitForTextAsync("ImportToAdd", "2 logins will be added. The vault holds 1 item now and will hold 3.");
        Assert.Equal("Import 2 logins", app.Find<Button>("ConfirmImportButton").Content);
        await ImportAsync(app);
        await app.WaitForTextAsync("ImportStatus", "2 logins were added to the vault.");
        Assert.Equal(3, app.Vault.Items.Count);
    });

    // ---- Export ----

    [Fact]
    public Task AnExport_AsksForTheMasterPasswordAgain_AndOnlyThenWritesTheFile() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await AddLoginAsync(app, "Bank", "fake.banker", "FAKE-pw-bank", "https://bank.example.com/");
        await AddLoginAsync(app, "Quotes \"and\", commas", "fake,user", "FAKE \"pw\", with a comma", "https://example.net/?ids=1,2,3");
        await OpenSettingsAsync(app);
        byte[] vaultBefore = app.VaultFile;
        string destination = Path.Combine(app.Directory, "files of the test", "export.csv");
        app.Files.ExportPath = destination;

        // Not without the box, not without a password.
        app.Press("ExportUnencryptedButton");
        Assert.Equal(ExportTexts.Warning, app.Text("ExportWarning"));
        Assert.False(app.Find<Button>("WriteExportButton").IsEffectivelyEnabled);
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("ExportAcknowledgeCheckBox"));
        Assert.False(app.Find<Button>("WriteExportButton").IsEffectivelyEnabled);

        // A wrong password: said so, nothing written, the box emptied.
        app.Type("ExportPasswordBox", "not the master password");
        app.Press("WriteExportButton");
        await app.WaitForTextAsync("ExportStatus", "The master password is incorrect. Nothing was exported.");
        Assert.False(File.Exists(destination));
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("ExportPasswordBox").Text));
        Assert.False(app.Find<Button>("WriteExportButton").IsEffectivelyEnabled);

        // The dialog suggested a name with today's date, as the clock of the application has it.
        string today = app.Clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal($"BitosX-export-{today}.csv", app.Files.SuggestedExportName);

        // The right one.
        app.Type("ExportPasswordBox", MasterPassword);
        app.Press("WriteExportButton");
        await app.WaitForTextAsync("ExportStatus", "2 items were exported as CSV.");
        Assert.False(app.Shows("ExportPanel"));
        Assert.Equal(VaultExporter.ToCsv(app.Vault.Items), File.ReadAllBytes(destination));
        Assert.Contains("\"FAKE \"\"pw\"\", with a comma\"", File.ReadAllText(destination), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
        }

        // An export changes nothing in the vault, and leaves nothing else behind.
        Assert.Equal(vaultBefore, app.VaultFile);
        Assert.Equal(["export.csv"], Directory.EnumerateFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
        Assert.DoesNotContain(Directory.EnumerateFiles(app.Directory), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

        // And what it wrote is what an import reads back: nothing new for the vault it came from.
        await ChooseAsync(app, destination);
        Assert.Equal("Chrome or Edge (CSV): 2 entries in the file.", app.Text("ImportSource"));
        Assert.Equal("Nothing in this file would be added to the vault.", app.Text("ImportToAdd"));
    });

    // ---- Locking ----

    public enum HowItLocks
    {
        LockNow,
        TheLockButton,
        TheWorkstationIsLocked,
        TheSystemGoesToSleep,
        Inactivity,
        AnUnexpectedError,
        TheVaultIsReplacedByABackup,
    }

    [Theory]
    [InlineData(HowItLocks.LockNow)]
    [InlineData(HowItLocks.TheLockButton)]
    [InlineData(HowItLocks.TheWorkstationIsLocked)]
    [InlineData(HowItLocks.TheSystemGoesToSleep)]
    [InlineData(HowItLocks.Inactivity)]
    [InlineData(HowItLocks.AnUnexpectedError)]
    [InlineData(HowItLocks.TheVaultIsReplacedByABackup)]
    public Task HoweverTheVaultLocks_BothAreClosed_AndTheNextUnlockStartsFromNothing(HowItLocks how) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await OpenSettingsAsync(app);

        string backup = Path.Combine(app.Directory, "files of the test", "a.bitosx-backup");
        if (how == HowItLocks.TheVaultIsReplacedByABackup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            app.Files.SavePath = backup;
            app.Press("ExportBackupButton");
            await app.WaitForTextAsync("BackupStatus", "Encrypted backup exported.");
        }

        // An import with its preview open and a choice made, and an export with everything filled in.
        await ChooseAsync(app, Write(app, "passwords.csv", ThreeLogins));
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("ImportDuplicatesCheckBox"));
        await app.WaitForTextAsync("ImportToAdd", "3 logins will be added.");
        app.Files.ExportPath = Path.Combine(app.Directory, "files of the test", "export.csv");
        OpenExport(app, "not the master password");
        app.Press("WriteExportButton");
        await app.WaitForTextAsync("ExportStatus", "The master password is incorrect.");
        app.Type("ExportPasswordBox", MasterPassword);
        Assert.True(app.Find<Button>("WriteExportButton").IsEffectivelyEnabled);
        byte[] vaultBefore = app.VaultFile;

        switch (how)
        {
            case HowItLocks.LockNow:
                app.Press("LockNowButton");
                break;
            case HowItLocks.TheLockButton:
                app.Press("LockButton");
                break;
            case HowItLocks.TheWorkstationIsLocked:
                await app.Signals.LockWorkstationAsync();
                break;
            case HowItLocks.TheSystemGoesToSleep:
                await app.Signals.SuspendAsync();
                break;
            case HowItLocks.Inactivity:
                app.Clock.MoveForward(TimeSpan.FromHours(25)); // longer than any timeout that can be set
                break;
            case HowItLocks.AnUnexpectedError:
                ((UnexpectedErrorGuard)app.Services.GetService(typeof(UnexpectedErrorGuard))!).Recover(new InvalidOperationException("for the test"));
                break;
            case HowItLocks.TheVaultIsReplacedByABackup:
                app.Files.OpenPath = backup;
                app.Press("ChooseBackupButton");
                await HeadlessUi.WaitUntilAsync(() => app.Shows("RestorePasswordBox"), "the backup is chosen");
                app.Type("RestorePasswordBox", MasterPassword);
                app.Press("ValidateBackupButton");
                await HeadlessUi.WaitUntilAsync(() => app.Shows("ReplaceVaultButton"), "the restore is offered");
                HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("RestoreAcknowledgeCheckBox"));
                app.Press("ReplaceVaultButton");
                break;
        }

        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);

        // Locked: nothing of the card is within reach, and nothing was imported or exported.
        Assert.False(app.Shows("ImportPreviewPanel"));
        Assert.False(app.Shows("ExportPanel"));
        Assert.False(app.Shows("ChooseImportFileButton"));
        Assert.False(File.Exists(app.Files.ExportPath));
        if (how != HowItLocks.TheVaultIsReplacedByABackup)
        {
            Assert.Equal(vaultBefore, app.VaultFile);
        }

        await UnlockAsync(app);
        Assert.Empty(app.Vault.Items);
        await OpenSettingsAsync(app);

        // A card that knows nothing of what was there before.
        foreach (string name in GoneAfterALock)
        {
            Assert.False(app.Shows(name), name + " is shown after the unlock");
        }

        app.Press("ExportUnencryptedButton");
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("ExportPasswordBox").Text));
        Assert.False(app.Find<CheckBox>("ExportAcknowledgeCheckBox").IsChecked);
        Assert.False(app.Find<Button>("WriteExportButton").IsEffectivelyEnabled);
        Assert.False(app.Shows("ExportStatus"));

        // And a file chosen now starts with both choices as they are at first.
        await ChooseAsync(app, Write(app, "passwords.csv", ThreeLogins));
        Assert.False(app.Find<CheckBox>("ImportDuplicatesCheckBox").IsChecked);
        Assert.False(app.Find<CheckBox>("CopyExtrasCheckBox").IsChecked);
        Assert.Equal("3 logins will be added. The vault holds 0 items now and will hold 3.", app.Text("ImportToAdd"));
    });

    // ---- Sync ----

    [Fact]
    public Task AnImport_IsAChangeTheNextSyncUploads_ExactlyAsALoginAddedByHandIs() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        app.Press("AccountNavButton");
        await app.WaitForAsync<AccountPageView>();
        app.Type("AccountEmailBox", ScriptedServer.Email);
        app.Type("AccountPasswordBox", ScriptedServer.Password);
        app.Press("SignInButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("SyncPanel"), "the account is shown");

        // "Sync now", and what the card says once the sync is over. (The status is taken away when a
        // sync starts and set when it ends; the button is disabled in between.)
        async Task<string?> SyncAsync()
        {
            app.Press("AccountNavButton");
            await app.WaitForAsync<AccountPageView>();
            app.Press("SyncNowButton");
            await HeadlessUi.WaitUntilAsync(
                () => app.Shows("SyncStatus") && app.Find<Button>("SyncNowButton").IsEffectivelyEnabled, "the sync reports");
            return app.Text("SyncStatus");
        }

        // The empty vault goes up; after that, nothing has changed.
        Assert.Equal("This device's vault was uploaded.", await SyncAsync());
        Assert.Equal(1, app.Server.UploadCount);
        Assert.StartsWith("Up to date", await SyncAsync(), StringComparison.Ordinal);
        Assert.Equal(1, app.Server.UploadCount);

        // A login added by hand: a change on this device only, which the next sync uploads.
        await AddLoginAsync(app, "Bank", "fake.banker", "FAKE-pw-bank", "https://bank.example.com/");
        Assert.NotEqual(app.VaultFile, app.Server.StoredEnvelope);
        Assert.Equal("This device's vault was uploaded.", await SyncAsync());
        Assert.Equal(2, app.Server.UploadCount);
        Assert.Equal(app.VaultFile, app.Server.StoredEnvelope);
        Assert.StartsWith("Up to date", await SyncAsync(), StringComparison.Ordinal);
        Assert.Equal(2, app.Server.UploadCount);

        // An import: the same. A preview alone is no change.
        await OpenSettingsAsync(app);
        await ChooseAsync(app, Write(app, "passwords.csv", ThreeLogins));
        Assert.StartsWith("Up to date", await SyncAsync(), StringComparison.Ordinal);
        Assert.Equal(2, app.Server.UploadCount);

        await OpenSettingsAsync(app);
        await ImportAsync(app);
        await app.WaitForTextAsync("ImportStatus", "2 logins were added to the vault.");
        Assert.NotEqual(app.VaultFile, app.Server.StoredEnvelope);
        Assert.Equal("This device's vault was uploaded.", await SyncAsync());
        Assert.Equal(3, app.Server.UploadCount);
        Assert.Equal(app.VaultFile, app.Server.StoredEnvelope);
        Assert.Equal(3, app.Server.StoredRevision);

        // One write, one revision: there is nothing left to upload, and an export is no change either.
        Assert.StartsWith("Up to date", await SyncAsync(), StringComparison.Ordinal);
        await OpenSettingsAsync(app);
        app.Files.ExportPath = Path.Combine(app.Directory, "files of the test", "export.json");
        OpenExport(app, MasterPassword);
        app.Find<RadioButton>("ExportJsonRadio").IsChecked = true;
        app.Press("WriteExportButton");
        await app.WaitForTextAsync("ExportStatus", "3 items were exported as BitosX JSON.");
        Assert.StartsWith("Up to date", await SyncAsync(), StringComparison.Ordinal);
        Assert.Equal(3, app.Server.UploadCount);
    });

    // ---- How it is laid out, and what it tells a screen reader ----

    public static TheoryData<string, double, double> Sizes => new()
    {
        { "default", 1000, 680 },
        { "smallest", 760, 540 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task TheCard_InItsFullestStates_IsLaidOutWithoutFaults_AndEveryInputHasAName(string size, double width, double height) => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync(width, height);
        await app.Vault.AddLoginAsync(new LoginItem
        {
            Name = "Two addresses", Username = "fake.two", Password = "FAKE-pw-two", Urls = ["https://one.example.com/", "https://two.example.com/"],
        });
        await OpenSettingsAsync(app);

        void AssertLaidOut(string screen)
        {
            HeadlessUi.Snapshot(app.Window, $"{screen}-{size}");
            IReadOnlyList<string> problems = ViewLayout.Problems(app.Window);
            Assert.True(problems.Count == 0, $"{screen} ({size}):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
            Assert.Empty(Nameless(app.Window));
        }

        // A CSV file with columns that are not read, under a file name that does not fit the line.
        await ChooseAsync(app, Write(
            app,
            "passwords exported from another password manager on a day in november 2026 - the final copy.csv",
            "Title,Website,User Name,Password,Tags,Last Used,A column with a remarkably long name that is not read either\n"
            + "Fake,https://fake.example.com,fake.user,FAKE-pw,a tag,yesterday,x\n"));
        Assert.True(app.Shows("ImportIgnoredColumns"));
        app.Window.Height = 3600;
        HeadlessUi.Settle();
        AssertLaidOut("18-settings-import-columns");

        // A file with something for every line of the preview, the list of what cannot be imported
        // open, both choices made; and the export open, with its warning, the note and a password.
        await ChooseAsync(app, Write(app, "bitwarden_export.json", EverythingAPreviewCanSay()));
        foreach (string line in EveryLineButTheColumns)
        {
            Assert.True(app.Shows(line), line + " is not shown");
        }

        app.Press("ToggleNotImportedButton");
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("CopyExtrasCheckBox"));
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("ImportDuplicatesCheckBox"));
        await app.WaitForTextAsync("ImportDuplicates", "It is added as well.");
        await app.WaitForTextAsync("ImportExtras", "It is copied into each item's notes.");
        OpenExport(app, MasterPassword);
        Assert.True(app.Shows("ExportSeveralUrlsNote"));
        AssertLaidOut("19-settings-import-preview-and-export");

        // After the import, with its reminder; and an export that was refused.
        await ImportAsync(app);
        await HeadlessUi.WaitUntilAsync(() => app.Shows("ImportFileReminder"), "the import reports");
        app.Type("ExportPasswordBox", "not the master password");
        app.Files.ExportPath = Path.Combine(app.Directory, "files of the test", "export.csv");
        app.Press("WriteExportButton");
        await app.WaitForTextAsync("ExportStatus", "The master password is incorrect.");
        AssertLaidOut("20-settings-import-done-and-export-refused");

        // Six password boxes are on the page now, and a screen reader can tell them apart.
        string?[] passwordBoxes =
        [
            .. app.Window.GetVisualDescendants().OfType<TextBox>()
                .Where(box => box.PasswordChar != default && box.IsEffectivelyVisible)
                .Select(box => ControlAutomationPeer.CreatePeerForElement(box).GetName()),
        ];
        Assert.Equal(6, passwordBoxes.Length);
        Assert.Equal(passwordBoxes.Length, passwordBoxes.Distinct(StringComparer.Ordinal).Count());
    });
}
