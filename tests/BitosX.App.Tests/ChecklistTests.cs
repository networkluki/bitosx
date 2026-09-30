using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using BitosX.App.Services;
using BitosX.App.Views;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;

namespace BitosX.App.Tests;

/// <summary>
/// The manual checklists of docs/VERIFICATION.md — the 17 steps, and the A-, H- and R-series —
/// carried out on the whole application (<see cref="WholeApplication"/>), without a display. What
/// a person did by hand in a window is done here through the same views: text is typed, buttons
/// are pressed from the keyboard or clicked, and what the screen shows is read back. What is on
/// disk is compared byte for byte, as the manual runs did with a hash.
/// </summary>
/// <remarks>
/// This does not replace a person at a real window on a real screen. It makes the checklist
/// something that is run for every change, on every system the tests run on.
/// </remarks>
public sealed class ChecklistTests
{
    private const string MasterPassword = "correct horse battery staple";
    private const string WrongPasswordMessage = "Incorrect master password, or the vault file has been modified.";

    private const string Name = "Example Bank";
    private const string Username = "alice@example.com";
    private const string Password = "S3cret-Pass-Phrase!";
    private const string Url = "https://bank.example.com";
    private const string Notes = "Security question: periwinkle";

    // ---- What is done more than once ----

    private static async Task CreateVaultAsync(WholeApplication app)
    {
        await app.WaitForAsync<CreateVaultView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
    }

    private static async Task UnlockAsync(WholeApplication app, string password = MasterPassword)
    {
        await app.WaitForAsync<UnlockView>();
        app.Type("MasterPasswordBox", password);
        app.Press("UnlockButton");
        await app.WaitForAsync<UnlockedShellView>();
    }

    private static async Task LockAsync(WholeApplication app)
    {
        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();
    }

    private static async Task AddLoginAsync(
        WholeApplication app, string name, string username, string password, string? url = null, string? notes = null)
    {
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Type("NameBox", name);
        app.Type("UsernameBox", username);
        app.Type("PasswordBox", password);
        if (url is not null)
        {
            app.Type("UrlsBox", url);
        }

        if (notes is not null)
        {
            app.Type("NotesBox", notes);
        }

        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
    }

    private static async Task OpenSettingsAsync(WholeApplication app)
    {
        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();
    }

    private static async Task OpenVaultPageAsync(WholeApplication app)
    {
        app.Press("VaultNavButton");
        await app.WaitForAsync<VaultPageView>();
    }

    private static void AssertNowhereInTheFile(WholeApplication app, params string[] values)
    {
        string file = Encoding.UTF8.GetString(app.VaultFile);
        foreach (string value in values)
        {
            Assert.DoesNotContain(value, file, StringComparison.Ordinal);
        }
    }

    private static void AssertNoTemporaryFiles(WholeApplication app)
        => Assert.DoesNotContain(
            Directory.EnumerateFiles(app.Directory),
            file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

    private static EncryptedVault Envelope(WholeApplication app) => EncryptedVaultSerializer.Deserialize(app.VaultFile);

    // ---- The 17 steps ----

    [Fact]
    public Task TheSeventeenSteps() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication { ClipboardDelay = TimeSpan.FromSeconds(1) };

        // 1. First launch, no vault file present: the Create Vault screen.
        await app.StartAsync();
        Assert.IsType<CreateVaultView>(app.Screen);
        Assert.False(File.Exists(app.Location.VaultFilePath));

        // 2. Create vault: the main screen with an empty vault, and a vault file.
        await CreateVaultAsync(app);
        Assert.IsType<VaultPageView>(app.Page);
        Assert.True(app.Shows("EmptyVaultNote"));
        Assert.Empty(app.ItemNames);
        Assert.True(File.Exists(app.Location.VaultFilePath));
        AssertNowhereInTheFile(app, MasterPassword);

        // 3. Restart: the Unlock screen, not Create Vault.
        await app.RestartAsync();
        Assert.IsType<UnlockView>(app.Screen);

        // 4. Wrong master password: one generic error, still locked, the attempt is not kept.
        byte[] untouched = app.VaultFile;
        app.Type("MasterPasswordBox", "not the master password");
        app.Press("UnlockButton");
        await app.WaitForTextAsync("ErrorMessage", WrongPasswordMessage);
        Assert.Equal(WrongPasswordMessage, app.Text("ErrorMessage"));
        Assert.IsType<UnlockView>(app.Screen);
        Assert.Equal(VaultState.Locked, app.Vault.State);
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("MasterPasswordBox").Text));
        Assert.Equal(untouched, app.VaultFile);

        // 5. Correct master password: unlocked.
        await UnlockAsync(app);
        Assert.Equal(VaultState.Unlocked, app.Vault.State);

        // 6. Add a login: it is listed, the file changes, and none of it is readable in the file.
        byte[] beforeAdding = app.VaultFile;
        await AddLoginAsync(app, Name, Username, Password, Url, Notes);
        Assert.Equal([Name], app.ItemNames);
        Assert.Equal(Username, app.FindInItem<TextBlock>(Name, "ItemUsername").Text);
        Assert.Equal(Url, app.FindInItem<TextBlock>(Name, "ItemUrl").Text);
        Assert.Equal("Saved.", app.Text("StatusMessage"));
        Assert.NotEqual(beforeAdding, app.VaultFile);
        AssertNowhereInTheFile(app, MasterPassword, Name, Username, Password, "bank.example.com", "periwinkle");

        // The password is masked until it is asked for.
        Assert.DoesNotContain(Password, HeadlessUi.ShownTexts(app.Window));
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "ShowPasswordButton"));
        Assert.Equal(Password, app.FindInItem<TextBlock>(Name, "ItemPassword").Text);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "ShowPasswordButton"));
        Assert.DoesNotContain(Password, HeadlessUi.ShownTexts(app.Window));

        // 7. Edit the login: the change is shown and saved.
        const string newUsername = "alice.cooper@example.com";
        byte[] beforeEditing = app.VaultFile;
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "EditButton"));
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        Assert.Equal("Edit login", app.Text("EditorTitle"));
        Assert.Equal(Name, app.Find<TextBox>("NameBox").Text);
        Assert.Equal(Notes, app.Find<TextBox>("NotesBox").Text);
        app.Type("UsernameBox", newUsername);

        // 8. Generate a password in the editor: random, and different the next time.
        app.Press("GeneratePasswordButton");
        string generated = app.Find<TextBox>("PasswordBox").Text!;
        Assert.Equal(20, generated.Length);
        app.Press("GeneratePasswordButton");
        Assert.NotEqual(generated, app.Find<TextBox>("PasswordBox").Text);
        app.Type("PasswordBox", Password); // keep the known one for the steps below

        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
        Assert.Equal(newUsername, app.FindInItem<TextBlock>(Name, "ItemUsername").Text);
        Assert.NotEqual(beforeEditing, app.VaultFile);

        // 9. Copy the username: confirmed with the countdown, and on the clipboard.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyUsernameButton"));
        await app.WaitForTextAsync("StatusMessage", "Username copied. Clipboard clears in 1s.");
        Assert.Equal(newUsername, await app.ClipboardTextAsync());

        // 10. Copy the password: the same.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied. Clipboard clears in 1s.");
        Assert.Equal(Password, await app.ClipboardTextAsync());

        // 11. Wait for the timeout: the clipboard is cleared ...
        await HeadlessUi.WaitUntilAsync(
            async () => string.IsNullOrEmpty(await app.ClipboardTextAsync()),
            "the clipboard is cleared");

        // ... but what the user copied afterwards is not.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");
        await app.Window.Clipboard!.SetTextAsync("something the user copied");
        await Task.Delay(TimeSpan.FromSeconds(2));
        HeadlessUi.Settle();
        Assert.Equal("something the user copied", await app.ClipboardTextAsync());

        // 12. Favourite and unfavourite: marked, sorted to the top, kept across lock and unlock.
        await AddLoginAsync(app, "Alpha", "first@example.com", "another-password-1");
        Assert.Equal(["Alpha", Name], app.ItemNames);
        HeadlessUi.Click(app.Window, app.FindInItem<Button>(Name, "FavoriteButton"));
        await HeadlessUi.WaitUntilAsync(() => app.ItemNames[0] == Name, "the favourite is first");
        Assert.Equal("★", app.FindInItem<Button>(Name, "FavoriteButton").Content);
        Assert.Equal("☆", app.FindInItem<Button>("Alpha", "FavoriteButton").Content);

        // 13. Lock the vault: back to the Unlock screen.
        await LockAsync(app);
        Assert.Equal(VaultState.Locked, app.Vault.State);

        // 14. The secrets are gone: nothing of the vault is on screen, and no way back in.
        Assert.IsType<UnlockView>(app.Screen);
        Assert.Null(app.Page);
        Assert.Empty(app.Window.GetVisualDescendants().OfType<LoginEditorView>());
        string screen = string.Join('\n', HeadlessUi.ShownTexts(app.Window));
        foreach (string secret in new[] { Name, "Alpha", newUsername, Password, Url, Notes, MasterPassword })
        {
            Assert.DoesNotContain(secret, screen, StringComparison.Ordinal);
        }

        Assert.Empty(app.Vault.Items);

        // 15. Unlock again: everything is back, the favourite still first.
        await UnlockAsync(app);
        Assert.Equal([Name, "Alpha"], app.ItemNames);
        Assert.Equal("★", app.FindInItem<Button>(Name, "FavoriteButton").Content);

        HeadlessUi.Click(app.Window, app.FindInItem<Button>(Name, "FavoriteButton"));
        await HeadlessUi.WaitUntilAsync(() => app.ItemNames[0] == "Alpha", "the order is by name again");
        Assert.Equal("☆", app.FindInItem<Button>(Name, "FavoriteButton").Content);

        // 16. Delete an item: asked for, confirmed, gone.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("Alpha", "DeleteButton"));
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("Alpha", "ConfirmDeleteButton"));
        await app.WaitForTextAsync("StatusMessage", "Item deleted.");
        Assert.Equal([Name], app.ItemNames);

        // 17. Restart: what was added and edited is there, what was deleted is not.
        await app.RestartAsync();
        await UnlockAsync(app);
        Assert.Equal([Name], app.ItemNames);
        Assert.Equal(newUsername, app.FindInItem<TextBlock>(Name, "ItemUsername").Text);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "ShowPasswordButton"));
        Assert.Equal(Password, app.FindInItem<TextBlock>(Name, "ItemPassword").Text);
        AssertNowhereInTheFile(app, MasterPassword, Name, "Alpha", newUsername, Password, "bank.example.com", "periwinkle");
        AssertNoTemporaryFiles(app);
    });

    [Fact]
    public Task Deleting_IsAskedForTwice_AndCanBeCalledOff() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        await AddLoginAsync(app, "Alpha", "first@example.com", "another-password-1");
        byte[] before = app.VaultFile;

        // Asking shows the question in place of the actions, and deletes nothing.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "DeleteButton"));
        Assert.True(HeadlessUi.IsShown(app.FindInItem<Control>(Name, "DeleteConfirmation")));
        Assert.False(HeadlessUi.IsShown(app.FindInItem<Control>(Name, "ItemActions")));
        Assert.Equal(before, app.VaultFile);

        // Asking about another item withdraws the first question.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("Alpha", "DeleteButton"));
        Assert.False(HeadlessUi.IsShown(app.FindInItem<Control>(Name, "DeleteConfirmation")));
        Assert.True(HeadlessUi.IsShown(app.FindInItem<Control>("Alpha", "DeleteConfirmation")));

        // "Keep" calls it off.
        HeadlessUi.Press(app.Window, app.FindInItem<Button>("Alpha", "KeepButton"));
        Assert.False(HeadlessUi.IsShown(app.FindInItem<Control>("Alpha", "DeleteConfirmation")));
        Assert.Equal(["Alpha", Name], app.ItemNames);
        Assert.Equal(before, app.VaultFile);
    });

    // ---- A-series: automatic locking ----

    [Fact]
    public Task A1_A2_Inactivity_LocksTheVault_AndActivityPutsItOff() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);
        app.Find<NumericUpDown>("InactivityMinutesBox").Value = 2;
        HeadlessUi.Settle();

        // A2. Almost two minutes pass, the user presses a key, and almost two minutes pass again.
        app.Clock.MoveForward(TimeSpan.FromSeconds(110));
        HeadlessUi.Key(app.Window, PhysicalKey.ShiftLeft);
        app.Clock.MoveForward(TimeSpan.FromSeconds(110));
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // several checks of the timeout
        HeadlessUi.Settle();
        Assert.Equal(VaultState.Unlocked, app.Vault.State);
        Assert.IsType<UnlockedShellView>(app.Screen);

        // A1. Nothing more happens, and the two minutes are over.
        app.Clock.MoveForward(TimeSpan.FromSeconds(15));
        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
    });

    [Fact]
    public Task APointerThatRests_DoesNotKeepTheVaultOpen_OneThatMovesDoes() => HeadlessUi.RunAsync(async () =>
    {
        // An operating system reports the pointer on its own, where it already was, whenever
        // something changes under it. Nobody moved it; it is not activity.
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);
        app.Find<NumericUpDown>("InactivityMinutesBox").Value = 2;
        HeadlessUi.Settle();
        var resting = new Avalonia.Point(400, 300);
        app.Window.MouseMove(resting);

        // Moved: the two minutes start again.
        app.Clock.MoveForward(TimeSpan.FromSeconds(110));
        app.Window.MouseMove(new Avalonia.Point(410, 300));
        app.Window.MouseMove(resting);
        app.Clock.MoveForward(TimeSpan.FromSeconds(110));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        HeadlessUi.Settle();
        Assert.Equal(VaultState.Unlocked, app.Vault.State);

        // Reported again and again where it is: the two minutes run out.
        for (int i = 0; i < 5; i++)
        {
            app.Clock.MoveForward(TimeSpan.FromSeconds(10));
            app.Window.MouseMove(resting);
        }

        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
    });

    [Fact]
    public Task A1_InactivityLock_CanBeTurnedOff() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("AutoLockCheckBox"));
        Assert.False(app.Find<CheckBox>("AutoLockCheckBox").IsChecked);

        app.Clock.MoveForward(TimeSpan.FromHours(3));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        HeadlessUi.Settle();

        Assert.Equal(VaultState.Unlocked, app.Vault.State);
    });

    [Fact]
    public Task A3_A4_Minimizing_LocksOnlyWhenAskedTo() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);

        // A4. Off, which is the default: minimizing and restoring leaves the vault open.
        app.Window.WindowState = WindowState.Minimized;
        HeadlessUi.Settle();
        app.Window.WindowState = WindowState.Normal;
        HeadlessUi.Settle();
        Assert.Equal(VaultState.Unlocked, app.Vault.State);

        // A3. On: minimizing locks at once.
        await OpenSettingsAsync(app);
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("LockOnMinimizeCheckBox"));
        Assert.True(app.Find<CheckBox>("LockOnMinimizeCheckBox").IsChecked);
        app.Window.WindowState = WindowState.Minimized;
        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
    });

    [Theory]
    [InlineData(false)] // A5. the workstation is locked
    [InlineData(true)] // A6. the system goes to sleep
    public Task A5_A6_WhenTheUserLeaves_TheVaultLocks_AndACopiedSecretIsCleared(bool suspend) => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        Assert.True(app.Signals.Started);

        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");
        Assert.Equal(Password, await app.ClipboardTextAsync());

        await (suspend ? app.Signals.SuspendAsync() : app.Signals.LockWorkstationAsync());

        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
        await HeadlessUi.WaitUntilAsync(
            async () => string.IsNullOrEmpty(await app.ClipboardTextAsync()),
            "the clipboard is cleared at once, not by the timer half a minute later",
            timeoutSeconds: 5);
    });

    [Fact]
    public Task WhenTheUserLeaves_WhatTheyCopiedThemselves_IsLeftAlone() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");
        await app.Window.Clipboard!.SetTextAsync("something the user copied");

        await app.Signals.LockWorkstationAsync();
        await app.WaitForAsync<UnlockView>();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        HeadlessUi.Settle();

        Assert.Equal("something the user copied", await app.ClipboardTextAsync());
    });

    [Fact]
    public Task LockingTheVault_LeavesACopiedValue_ForTheRestOfItsTime() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");

        // Copy, lock, switch to the other window, paste: the intended use.
        await LockAsync(app);

        Assert.Equal(Password, await app.ClipboardTextAsync());
    });

    [Fact]
    public Task ClosingTheWindow_ClearsACopiedSecret() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");
        IClipboard clipboard = app.Window.Clipboard!;
        Assert.Equal(Password, await clipboard.TryGetTextAsync());

        await app.StopAsync();

        Assert.True(string.IsNullOrEmpty(await clipboard.TryGetTextAsync()));
    });

    [Fact]
    public Task A7_TheSettings_AreKeptAcrossARestart() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);
        app.Find<NumericUpDown>("InactivityMinutesBox").Value = 7;
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("LockOnMinimizeCheckBox"));
        Assert.False(app.Shows("AutoLockStatus"));
        Assert.Contains("\"inactivityTimeoutSeconds\": 420", File.ReadAllText(app.Location.SettingsFilePath), StringComparison.Ordinal);

        await app.RestartAsync();
        await UnlockAsync(app);
        await OpenSettingsAsync(app);

        Assert.Equal(7m, app.Find<NumericUpDown>("InactivityMinutesBox").Value);
        Assert.True(app.Find<CheckBox>("LockOnMinimizeCheckBox").IsChecked);
        Assert.True(app.Find<CheckBox>("AutoLockCheckBox").IsChecked);
    });

    [Fact]
    public Task AfterLockAndUnlock_Settings_ShowsWhatApplies_AndChangingOneKeepsTheOthers() => HeadlessUi.RunAsync(async () =>
    {
        // Before 0.7.0 the page was filled, every time the vault was unlocked, with the settings
        // the application had started with. It showed choices as they no longer were — and saving
        // one of them put the others back. Found by running the checklist on the real application.
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("LockOnMinimizeCheckBox"));
        app.Find<NumericUpDown>("InactivityMinutesBox").Value = 7;
        HeadlessUi.Settle();

        await LockAsync(app);
        await UnlockAsync(app);
        await OpenSettingsAsync(app);

        Assert.True(app.Find<CheckBox>("LockOnMinimizeCheckBox").IsChecked);
        Assert.Equal(7m, app.Find<NumericUpDown>("InactivityMinutesBox").Value);

        // Changing one of them leaves the other as it is, on the page and in the file.
        app.Find<NumericUpDown>("InactivityMinutesBox").Value = 9;
        HeadlessUi.Settle();
        string saved = File.ReadAllText(app.Location.SettingsFilePath);
        Assert.Contains("\"inactivityTimeoutSeconds\": 540", saved, StringComparison.Ordinal);
        Assert.Contains("\"lockOnMinimize\": true", saved, StringComparison.Ordinal);

        // And it is what the application does.
        app.Window.WindowState = WindowState.Minimized;
        await app.WaitForAsync<UnlockView>();
    });

    [Fact]
    public Task A8_LockNow_LocksAtOnce() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await OpenSettingsAsync(app);

        app.Press("LockNowButton");

        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
    });

    // ---- Changing the master password ----

    [Fact]
    public Task ChangingTheMasterPassword_FromSettings() => HeadlessUi.RunAsync(async () =>
    {
        const string newPassword = "a new and different master password";
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        await OpenSettingsAsync(app);

        // A wrong current password changes nothing.
        byte[] before = app.VaultFile;
        app.Type("CurrentPasswordBox", "not the master password");
        app.Type("NewPasswordBox", newPassword);
        app.Type("ConfirmPasswordBox", newPassword);
        app.Press("ChangePasswordButton");
        await app.WaitForTextAsync("ChangePasswordStatus", "The current password is incorrect.");
        Assert.Equal(before, app.VaultFile);

        // A confirmation that does not match changes nothing.
        app.Type("CurrentPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", newPassword + "x");
        app.Press("ChangePasswordButton");
        await app.WaitForTextAsync("ChangePasswordStatus", "do not match");
        Assert.Equal(before, app.VaultFile);

        // The right one does, and the form is emptied.
        app.Type("ConfirmPasswordBox", newPassword);
        app.Press("ChangePasswordButton");
        await app.WaitForTextAsync("ChangePasswordStatus", "Master password changed.");
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("CurrentPasswordBox").Text));
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("NewPasswordBox").Text));
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("ConfirmPasswordBox").Text));
        Assert.Equal(Envelope(app).Ciphertext, EncryptedVaultSerializer.Deserialize(before).Ciphertext); // not re-encrypted

        // After a restart the old password is refused and the new one opens the vault.
        await app.RestartAsync();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Press("UnlockButton");
        await app.WaitForTextAsync("ErrorMessage", WrongPasswordMessage);
        await UnlockAsync(app, newPassword);
        Assert.Equal([Name], app.ItemNames);
    });

    // ---- H-series: the confirmation before a restore ----

    /// <summary>A vault with one item, a backup of it, a second item added afterwards, and the backup validated.</summary>
    private static async Task<(WholeApplication App, string Backup, byte[] Live)> AtTheRestorePreviewAsync()
    {
        var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);

        await OpenSettingsAsync(app);
        string backup = Path.Combine(app.Directory, "backup.bitosx-backup");
        app.Files.SavePath = backup;
        app.Press("ExportBackupButton");
        await app.WaitForTextAsync("BackupStatus", "Encrypted backup exported.");
        Assert.Equal(app.VaultFile, await File.ReadAllBytesAsync(backup));

        await OpenVaultPageAsync(app);
        await AddLoginAsync(app, "Added after the backup", "later@example.com", "another-password-1");
        await OpenSettingsAsync(app);

        await ValidateAsync(app, backup);
        return (app, backup, app.VaultFile);
    }

    private static async Task ValidateAsync(WholeApplication app, string backup, string password = MasterPassword)
    {
        app.Files.OpenPath = backup;
        app.Press("ChooseBackupButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("RestorePasswordBox"), "the backup is chosen");
        app.Type("RestorePasswordBox", password);
        app.Press("ValidateBackupButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("RestoreConfirmPanel"), "the restore is offered");
    }

    [Fact]
    public Task H3_ThePreview_StartsUnacknowledged_WithReplaceDisabled_AndCancelFocused() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, _, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            Assert.Contains("1 item(s)", app.Text("RestoreSummary"), StringComparison.Ordinal);
            Assert.False(app.Find<CheckBox>("RestoreAcknowledgeCheckBox").IsChecked);
            Assert.False(app.Find<Button>("ReplaceVaultButton").IsEffectivelyEnabled);
            Assert.True(app.Find<Button>("RestoreCancelButton").IsFocused);
            Assert.Equal(live, app.VaultFile); // validating wrote nothing
            AssertNoTemporaryFiles(app);
        }
    });

    [Fact]
    public Task H1_Enter_AtThePreview_RestoresNothing() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, _, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            HeadlessUi.Key(app.Window, PhysicalKey.Enter);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            HeadlessUi.Settle();

            Assert.Equal(live, app.VaultFile);
            Assert.Equal(VaultState.Unlocked, app.Vault.State);
            AssertNoTemporaryFiles(app);
        }
    });

    [Fact]
    public Task H2_Space_AtThePreview_Cancels_AndRestoresNothing() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, _, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            HeadlessUi.Key(app.Window, PhysicalKey.Space);
            await HeadlessUi.WaitUntilAsync(() => !app.Shows("RestoreConfirmPanel"), "the preview closes");

            Assert.Equal(live, app.VaultFile);
            Assert.Equal(VaultState.Unlocked, app.Vault.State);
            AssertNoTemporaryFiles(app);
        }
    });

    [Fact]
    public Task H4_Acknowledging_WritesNothing_AndOnlyTheButtonRestores() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, string backup, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("RestoreAcknowledgeCheckBox"));
            Assert.True(app.Find<Button>("ReplaceVaultButton").IsEffectivelyEnabled);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            HeadlessUi.Settle();
            Assert.Equal(live, app.VaultFile);

            HeadlessUi.Click(app.Window, app.Find<Button>("ReplaceVaultButton"));

            // The vault is replaced by the backup, the application locks, and the vault that was
            // replaced is kept as a recovery copy.
            await app.WaitForAsync<UnlockView>();
            Assert.Equal(await File.ReadAllBytesAsync(backup), app.VaultFile);
            string[] recovery = [.. Directory.EnumerateFiles(app.Directory).Where(f => f.EndsWith(FileVaultBackup.RecoverySuffix, StringComparison.Ordinal))];
            Assert.Single(recovery);
            Assert.Equal(live, await File.ReadAllBytesAsync(recovery[0]));
            AssertNoTemporaryFiles(app);

            await UnlockAsync(app);
            Assert.Equal([Name], app.ItemNames);
        }
    });

    [Fact]
    public Task H5_Escape_Cancels_AndTheAcknowledgement_IsNotKept() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, string backup, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("RestoreAcknowledgeCheckBox"));
            Assert.True(app.Find<Button>("ReplaceVaultButton").IsEffectivelyEnabled);

            HeadlessUi.Key(app.Window, PhysicalKey.Escape);
            await HeadlessUi.WaitUntilAsync(() => !app.Shows("RestoreConfirmPanel"), "the preview closes");
            Assert.Equal(live, app.VaultFile);
            Assert.False(app.Shows("RestorePasswordBox")); // the chosen file and its password are forgotten

            // Coming back to it starts from the beginning.
            await ValidateAsync(app, backup);
            Assert.False(app.Find<CheckBox>("RestoreAcknowledgeCheckBox").IsChecked);
            Assert.False(app.Find<Button>("ReplaceVaultButton").IsEffectivelyEnabled);

            // And so does the Cancel button.
            HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("RestoreAcknowledgeCheckBox"));
            app.Press("RestoreCancelButton");
            await HeadlessUi.WaitUntilAsync(() => !app.Shows("RestoreConfirmPanel"), "the preview closes");
            await ValidateAsync(app, backup);
            Assert.False(app.Find<CheckBox>("RestoreAcknowledgeCheckBox").IsChecked);
            Assert.False(app.Find<Button>("ReplaceVaultButton").IsEffectivelyEnabled);
            Assert.Equal(live, app.VaultFile);
        }
    });

    [Fact]
    public Task H6_ChangingThePassword_AfterValidating_WithdrawsTheOffer() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, _, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("RestoreAcknowledgeCheckBox"));

            app.Type("RestorePasswordBox", MasterPassword + "x");

            Assert.False(app.Shows("RestoreConfirmPanel"));
            Assert.Equal(live, app.VaultFile);
        }
    });

    [Fact]
    public Task ABackup_WithTheWrongPassword_IsRefused_AndNothingIsOffered() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, string backup, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            app.Press("RestoreCancelButton");
            app.Files.OpenPath = backup;
            app.Press("ChooseBackupButton");
            await HeadlessUi.WaitUntilAsync(() => app.Shows("RestorePasswordBox"), "the backup is chosen");

            app.Type("RestorePasswordBox", "not the master password");
            app.Press("ValidateBackupButton");

            await app.WaitForTextAsync("RestoreStatus", "The backup password is incorrect, or the backup is corrupted.");
            Assert.False(app.Shows("RestoreConfirmPanel"));
            Assert.Equal(live, app.VaultFile);
        }
    });

    // ---- R-series: rotating the vault key ----

    [Fact]
    public Task R_RotatingTheVaultKey() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        await AddLoginAsync(app, "Alpha", "first@example.com", "another-password-1");

        // A backup from before the rotation.
        await OpenSettingsAsync(app);
        string backup = Path.Combine(app.Directory, "before-rotation.bitosx-backup");
        app.Files.SavePath = backup;
        app.Press("ExportBackupButton");
        await app.WaitForTextAsync("BackupStatus", "Encrypted backup exported.");
        byte[] before = app.VaultFile;
        EncryptedVault old = Envelope(app);

        // Wrong current password: refused, and the file is byte for byte what it was.
        app.Type("RotatePasswordBox", "not the master password");
        app.Press("RotateVaultKeyButton");
        await app.WaitForTextAsync("RotateStatus", "The current password is incorrect. The vault key was not rotated.");
        Assert.Equal(before, app.VaultFile);
        AssertNoTemporaryFiles(app);

        // Abandoned: the password is typed, the user goes elsewhere and comes back.
        app.Type("RotatePasswordBox", MasterPassword);
        await OpenVaultPageAsync(app);
        await LockAsync(app);
        await UnlockAsync(app);
        await OpenSettingsAsync(app);
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("RotatePasswordBox").Text));
        Assert.Equal(before, app.VaultFile);

        // Valid rotation: everything that is key or ciphertext changes; the format and the settings do not.
        app.Type("RotatePasswordBox", MasterPassword);
        app.Press("RotateVaultKeyButton");
        await app.WaitForTextAsync("RotateStatus", "Vault key rotated.");
        Assert.True(string.IsNullOrEmpty(app.Find<TextBox>("RotatePasswordBox").Text));
        EncryptedVault rotated = Envelope(app);
        Assert.NotEqual(before, app.VaultFile);
        Assert.NotEqual(old.Salt, rotated.Salt);
        Assert.NotEqual(old.KeyWrapNonce, rotated.KeyWrapNonce);
        Assert.NotEqual(old.EncryptedVaultKey, rotated.EncryptedVaultKey);
        Assert.NotEqual(old.ContentNonce, rotated.ContentNonce);
        Assert.NotEqual(old.Ciphertext, rotated.Ciphertext);
        Assert.Equal(old.Ciphertext.Length, rotated.Ciphertext.Length);
        Assert.Equal(1, rotated.Version);
        Assert.Equal(old.KdfParameters, rotated.KdfParameters);
        AssertNoTemporaryFiles(app);

        // Lock and unlock with the same password: the items are intact.
        await LockAsync(app);
        await UnlockAsync(app);
        Assert.Equal(["Alpha", Name], app.ItemNames);

        // A save after the rotation is written under the rotated key; the retired one does not return.
        await AddLoginAsync(app, "Added after the rotation", "later@example.com", "another-password-2");
        EncryptedVault saved = Envelope(app);
        Assert.Equal(rotated.Salt, saved.Salt);
        Assert.Equal(rotated.EncryptedVaultKey, saved.EncryptedVaultKey);
        Assert.NotEqual(rotated.Ciphertext, saved.Ciphertext);

        // A restart: nothing is rewritten by closing or starting, and the vault opens.
        byte[] beforeRestart = app.VaultFile;
        await app.RestartAsync();
        Assert.Equal(beforeRestart, app.VaultFile);
        await UnlockAsync(app);
        Assert.Equal(beforeRestart, app.VaultFile);
        Assert.Equal(["Added after the rotation", "Alpha", Name], app.ItemNames);

        // The backup from before still validates, shows the older generation, and changes nothing.
        await OpenSettingsAsync(app);
        await ValidateAsync(app, backup);
        Assert.Contains("2 item(s)", app.Text("RestoreSummary"), StringComparison.Ordinal);
        Assert.Equal(beforeRestart, app.VaultFile);
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
    });

    [Fact]
    public Task R_WhileLocked_NothingOfSettingsIsWithinReach() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await LockAsync(app);

        Assert.IsType<UnlockView>(app.Screen);
        Assert.Empty(app.Window.GetVisualDescendants().OfType<SettingsPageView>());
        Assert.Empty(app.Window.GetVisualDescendants().OfType<UnlockedShellView>());
        Assert.DoesNotContain(
            app.Window.GetVisualDescendants().OfType<Button>(),
            button => button.Name is "RotateVaultKeyButton" or "ChangePasswordButton" or "ExportBackupButton" or "SettingsNavButton");
    });

    // ---- What an empty password field does ----

    [Fact]
    public Task AnEmptyPasswordField_IsAWrongPassword_NotACrash() => HeadlessUi.RunAsync(async () =>
    {
        (WholeApplication app, string backup, byte[] live) = await AtTheRestorePreviewAsync();
        await using (app)
        {
            app.Press("RestoreCancelButton");

            app.Press("RotateVaultKeyButton");
            await app.WaitForTextAsync("RotateStatus", "The current password is incorrect. The vault key was not rotated.");

            app.Type("NewPasswordBox", "a new and different master password");
            app.Type("ConfirmPasswordBox", "a new and different master password");
            app.Press("ChangePasswordButton");
            await app.WaitForTextAsync("ChangePasswordStatus", "The current password is incorrect.");

            app.Files.OpenPath = backup;
            app.Press("ChooseBackupButton");
            await HeadlessUi.WaitUntilAsync(() => app.Shows("RestorePasswordBox"), "the backup is chosen");
            app.Press("ValidateBackupButton");
            await app.WaitForTextAsync("RestoreStatus", "The backup password is incorrect, or the backup is corrupted.");

            Assert.False(app.Find<Button>("StrengthenKeyDerivationButton").IsEffectivelyEnabled);
            Assert.Equal(VaultState.Unlocked, app.Vault.State);
            Assert.False(app.Shows("NoticeBar"));
            Assert.Equal(live, app.VaultFile);
        }
    });

    // ---- A fault nobody foresaw ----

    [Fact]
    public Task AnExceptionThatReachesTheUserInterface_LocksTheVault_InsteadOfEndingTheApplication() => HeadlessUi.RunAsync(async () =>
    {
        // The way it happens: something on the UI thread throws and nothing catches it. Before
        // 0.7.0 a command did exactly that when a password field was empty.
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => throw new InvalidOperationException("a secret the user typed"));

        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
        Assert.Equal(UnexpectedErrorGuard.Notice, app.Text("NoticeText"));
        Assert.DoesNotContain("a secret the user typed", string.Join('\n', HeadlessUi.ShownTexts(app.Window)), StringComparison.Ordinal);

        // The application goes on.
        await UnlockAsync(app);
        Assert.Equal([Name], app.ItemNames);
    });

    [Fact]
    public Task AnUnexpectedError_LocksTheVault_ClearsTheClipboard_AndSaysSo() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();
        await CreateVaultAsync(app);
        await AddLoginAsync(app, Name, Username, Password);
        HeadlessUi.Press(app.Window, app.FindInItem<Button>(Name, "CopyPasswordButton"));
        await app.WaitForTextAsync("StatusMessage", "Password copied.");
        byte[] before = app.VaultFile;
        Assert.False(app.Shows("NoticeBar"));

        bool goOn = app.Services.GetService(typeof(UnexpectedErrorGuard)) is UnexpectedErrorGuard guard
                    && guard.Recover(new InvalidOperationException("a secret the user typed"));

        Assert.True(goOn);
        await app.WaitForAsync<UnlockView>();
        Assert.Equal(VaultState.Locked, app.Vault.State);
        Assert.True(app.Shows("NoticeBar"));
        Assert.Equal(UnexpectedErrorGuard.Notice, app.Text("NoticeText"));
        Assert.DoesNotContain("a secret the user typed", string.Join('\n', HeadlessUi.ShownTexts(app.Window)), StringComparison.Ordinal);
        await HeadlessUi.WaitUntilAsync(
            async () => string.IsNullOrEmpty(await app.ClipboardTextAsync()),
            "the clipboard is cleared at once, not by the timer half a minute later",
            timeoutSeconds: 5);
        Assert.Equal(before, app.VaultFile);

        // The notice goes away when dismissed, and the vault opens as usual.
        app.Press("DismissNoticeButton");
        Assert.False(app.Shows("NoticeBar"));
        await UnlockAsync(app);
        Assert.Equal([Name], app.ItemNames);
    });
}
