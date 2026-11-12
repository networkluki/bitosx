using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using BitosX.ApiClient;
using BitosX.App.Controls;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using BitosX.Platform.Session;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// The "Import and export" card of the real Settings view, run without a display (see
/// <see cref="HeadlessUi"/>) on top of the real vault service, the real import and export services
/// and real files in a temporary directory. The tests press the view's buttons, tick its boxes and
/// type into its password box, and read what its text blocks show. Only the file dialogs are stood
/// in for.
/// </summary>
public sealed class ImportExportUiTests : IDisposable
{
    private const string MasterPassword = "correct horse battery";

    private const string SecretPassword = "FAKE-SECRET-PASSWORD";
    private const string SecretNotes = "FAKE-SECRET-NOTES";
    private const string SecretTotp = "FAKESECRETTOTPKEY";
    private const string SecretFolder = "FAKE-SECRET-FOLDER";
    private const string SecretFieldName = "FAKE-SECRET-FIELD-NAME";
    private const string SecretFieldValue = "FAKE-SECRET-FIELD-VALUE";
    private const string SecretAddress = "fake-secret-address.example";
    private const string SecretUsername = "fake.secret.username";

    private static readonly string[] Secrets =
    [
        SecretPassword, SecretNotes, SecretTotp, SecretFolder, SecretFieldName, SecretFieldValue, SecretAddress, SecretUsername,
    ];

    private const string ThreeLogins =
        "name,url,username,password,note\n"
        + "Mail,https://mail.example.com/,fake.user@example.test,FAKE-pw-mail,\n"
        + "Shop,shop.example.com,fake.shopper,FAKE-pw-shop,a note\n"
        + "Bank,https://bank.example.com/,fake.banker,FAKE-pw-bank,\n";

    // Small enough to keep the tests quick.
    private static readonly Argon2Parameters Small = new(8192, 2, 4, 32);

    /// <summary>What the card does not show until an import or an export is under way.</summary>
    private static readonly string[] ShownOnlyWhenAskedFor =
    [
        "ImportPreviewPanel", "ImportFileName", "ImportReadingProgress", "ImportStatus", "ImportFileReminder",
        "ConfirmImportButton", "CancelImportButton", "CopyExtrasCheckBox", "ImportDuplicatesCheckBox",
        "ExportPanel", "ExportWarning", "ExportAcknowledgeCheckBox", "ExportPasswordBox", "WriteExportButton",
        "CancelExportButton", "ExportProgress", "ExportStatus",
    ];

    /// <summary>The lines of a preview that a file of three ordinary logins gives no reason for.</summary>
    private static readonly string[] LinesWithNothingToSay =
    [
        "ImportOverLimit", "ImportNotImportedPanel", "ImportUnsupported", "ImportInTrash", "ImportEmpty", "ImportExtras",
        "ImportNamesDerived", "ImportIgnoredColumns", "ImportStatus", "ImportFileReminder",
    ];

    private readonly string _directory;
    private readonly string _vaultPath;

    public ImportExportUiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BitosX.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "vault.bitosx");
    }

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

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        return path;
    }

    /// <summary>An unlocked vault that holds the login called Bank of <see cref="ThreeLogins"/>.</summary>
    private async Task<VaultService> UnlockedVaultAsync()
    {
        var vault = new VaultService(VaultCryptor.CreateDefault(), new FileLocalVaultStorage(_vaultPath), Small, NullLogger<VaultService>.Instance);
        await vault.CreateVaultAsync(MasterPassword);
        await vault.AddLoginAsync(new LoginItem
        {
            Name = "Bank", Username = "fake.banker", Password = "FAKE-pw-bank", Urls = ["https://bank.example.com/"],
        });
        return vault;
    }

    /// <summary>The Settings page as the application builds it, with the dialogs answered by <paramref name="files"/>.</summary>
    private SettingsPageViewModel NewViewModel(IVaultService vault, FakeFilePicker files)
    {
        var importExportFiles = new FileImportExportFiles(_vaultPath);
        var card = new ImportExportViewModel(
            new VaultImportService(vault, importExportFiles, TimeProvider.System, NullLogger<VaultImportService>.Instance),
            new VaultExportService(vault, importExportFiles, TimeProvider.System, NullLogger<VaultExportService>.Instance),
            vault,
            files,
            new AvaloniaUiDispatcher(),
            TimeProvider.System);
        var controller = new AutoLockController(vault, AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        return new SettingsPageViewModel(
            vault,
            new FileLocalVaultStorage(_vaultPath),
            new FakeClipboardService(),
            new LocalOnlyApiClient(),
            Small,
            new FakeAppSettingsStore(),
            new AutoLockService(controller, new NullSystemSessionEvents()),
            new NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            files,
            new FakeKeyDerivationBenchmark(),
            card);
    }

    private static (Window Window, SettingsPageView View) Show(SettingsPageViewModel viewModel)
    {
        var view = new SettingsPageView { DataContext = viewModel };
        Window window = HeadlessUi.Show(view, 1000, 2600);
        return (window, view);
    }

    private static string? Text(Control root, string name) => HeadlessUi.Find<TextBlock>(root, name).Text;

    private static bool Shown(Control root, string name) => HeadlessUi.IsShown(HeadlessUi.Find<Control>(root, name));

    private static Button TheButton(Control root, string name) => HeadlessUi.Find<Button>(root, name);

    /// <summary>The texts of every shown text block called <paramref name="name"/>, from the top: the rows of a list.</summary>
    private static string[] Rows(Control root, string name)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == name && t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

    /// <summary>
    /// Every text the window holds, shown or not: what its text blocks and text boxes say, what is
    /// written on its buttons and boxes, its tooltips, and the names it gives the accessibility
    /// interface.
    /// </summary>
    private static List<string> EverythingInTheWindow(Window window)
    {
        var texts = new List<string>();
        foreach (Control control in window.GetVisualDescendants().OfType<Control>())
        {
            switch (control)
            {
                case TextBlock block when block.Text is { Length: > 0 } text:
                    texts.Add(text);
                    break;
                case TextBox box when box.Text is { Length: > 0 } text:
                    texts.Add(text);
                    break;
                case ContentControl { Content: string content }:
                    texts.Add(content);
                    break;
            }

            if (ToolTip.GetTip(control) is string tip)
            {
                texts.Add(tip);
            }

            if (AutomationProperties.GetName(control) is { Length: > 0 } name)
            {
                texts.Add(name);
            }

            if (ControlAutomationPeer.CreatePeerForElement(control).GetName() is { Length: > 0 } spoken)
            {
                texts.Add(spoken);
            }
        }

        return texts;
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

    // ---- The card, before anything is asked of it ----

    [Fact]
    public Task TheCard_OffersAnImportAndAnExport_AndShowsNothingElseYet() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, new FakeFilePicker()));

        Assert.True(Shown(view, "ImportExportCard"));
        Assert.True(TheButton(view, "ChooseImportFileButton").IsEffectivelyEnabled);
        Assert.True(TheButton(view, "ExportUnencryptedButton").IsEffectivelyEnabled);

        foreach (string name in ShownOnlyWhenAskedFor)
        {
            Assert.False(Shown(view, name), name + " is shown");
        }

        // The card is below Backup & Restore, where what a vault can be copied out to is.
        var cards = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToList();
        int backup = cards.FindIndex(c => c.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ExportBackupButton"));
        Assert.Equal("ImportExportCard", cards[backup + 1].Name);
        Assert.Empty(Nameless(window));
    });

    // ---- Import ----

    [Fact]
    public Task AnImport_FromTheKeyboard_PreviewFirst_ThenTheLogins() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ImportPath = Write("passwords from another manager.csv", ThreeLogins) };
        SettingsPageViewModel viewModel = NewViewModel(vault, files);
        (Window window, SettingsPageView view) = Show(viewModel);
        byte[] vaultBefore = await File.ReadAllBytesAsync(_vaultPath);

        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportPreviewPanel"), "the preview is shown");

        Assert.Equal("passwords from another manager.csv", Text(view, "ImportFileName"));
        Assert.Equal("Chrome or Edge (CSV): 3 entries in the file.", Text(view, "ImportSource"));
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", Text(view, "ImportToAdd"));
        Assert.Equal("1 login is already in the vault, or in the file more than once, and is skipped.", Text(view, "ImportDuplicates"));
        Assert.Equal("https:// is put in front of 1 address that has no scheme.", Text(view, "ImportHttpsAdded"));
        foreach (string name in LinesWithNothingToSay)
        {
            Assert.False(Shown(view, name), name + " is shown");
        }

        // The two choices, in the words both clients use, and neither made.
        CheckBox copyExtras = HeadlessUi.Find<CheckBox>(view, "CopyExtrasCheckBox");
        CheckBox duplicates = HeadlessUi.Find<CheckBox>(view, "ImportDuplicatesCheckBox");
        Assert.Equal(ImportTexts.OptionCopyExtrasToNotes, copyExtras.Content);
        Assert.Equal(ImportTexts.OptionImportDuplicates, duplicates.Content);
        Assert.False(copyExtras.IsChecked);
        Assert.False(duplicates.IsChecked);

        Button import = TheButton(view, "ConfirmImportButton");
        Assert.Equal("Import 2 logins", import.Content);
        Assert.True(import.IsEffectivelyEnabled);
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_vaultPath)); // nothing was written yet

        // A choice asks for the preview again; the page follows.
        HeadlessUi.Toggle(window, duplicates);
        await HeadlessUi.WaitUntilAsync(() => (string?)import.Content == "Import 3 logins", "the preview follows the choice");
        Assert.Equal("3 logins will be added. The vault holds 1 item now and will hold 4.", Text(view, "ImportToAdd"));
        Assert.Equal("1 login is already in the vault, or in the file more than once. It is added as well.", Text(view, "ImportDuplicates"));
        HeadlessUi.Toggle(window, duplicates);
        await HeadlessUi.WaitUntilAsync(() => (string?)import.Content == "Import 2 logins", "the preview follows the choice back");
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_vaultPath));

        HeadlessUi.Press(window, import);
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportStatus"), "the import reports");

        Assert.Equal("2 logins were added to the vault.", Text(view, "ImportStatus"));
        Assert.Equal(ImportTexts.FileStillHoldsPasswords, Text(view, "ImportFileReminder"));
        Assert.False(Shown(view, "ImportPreviewPanel"));
        Assert.Equal(["Bank", "Mail", "Shop"], vault.Items.Select(i => i.Name).Order());
        Assert.Empty(Nameless(window));
    });

    [Fact]
    public Task Cancel_ClosesThePreview_AndWritesNothing() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ImportPath = Write("logins.csv", ThreeLogins) };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, files));
        byte[] vaultBefore = await File.ReadAllBytesAsync(_vaultPath);
        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportPreviewPanel"), "the preview is shown");

        HeadlessUi.Press(window, TheButton(view, "CancelImportButton"));

        Assert.False(Shown(view, "ImportPreviewPanel"));
        Assert.False(Shown(view, "ImportFileName"));
        Assert.False(Shown(view, "ImportStatus"));
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_vaultPath));
        Assert.Single(vault.Items);
    });

    [Fact]
    public Task WhatCannotBeImported_IsListedByNameAndReason_AndNoSecretOfTheFile_IsAnywhereInTheWindow() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        string longNotes = new('n', 16385);
        string file = Write("secrets.csv",
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n"
            + $"{SecretFolder},1,login,Good entry,{SecretNotes},{SecretFieldName}: {SecretFieldValue},0,\"https://{SecretAddress}/,ftp://{SecretAddress}\",{SecretUsername},{SecretPassword},{SecretTotp}\n"
            + $"{SecretFolder},,login,Notes too long,{SecretNotes}{longNotes},{SecretFieldName}: {SecretFieldValue},0,https://{SecretAddress}/2,{SecretUsername},{SecretPassword},{SecretTotp}\n"
            + $",,login,Address that is none,{SecretNotes},,0,https://,{SecretUsername},{SecretPassword},{SecretTotp}\n"
            // A comma that was not quoted moved the values of this row: what stands where its name should is not its name.
            + $",,login,{SecretPassword},{SecretNotes},,0,https://{SecretAddress}/4,{SecretUsername},{SecretPassword},{SecretTotp},{SecretNotes}\n");
        var files = new FakeFilePicker { ImportPath = file };
        SettingsPageViewModel viewModel = NewViewModel(vault, files);
        (Window window, SettingsPageView view) = Show(viewModel);

        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportPreviewPanel"), "the preview is shown");

        Assert.Equal("Bitwarden (CSV): 4 entries in the file.", Text(view, "ImportSource"));
        Assert.Equal("3 entries cannot be imported.", Text(view, "ImportNotImported"));
        // The authenticator key of the file is a key (docs/TOTP.md §3), and is imported with its login.
        Assert.Equal("1 login brings an authenticator key, which is imported with it.", Text(view, "ImportAuthenticatorKeys"));
        Assert.False(Shown(view, "ImportUnreadableKeys"));
        Assert.Equal(
            "BitosX has no field for some of what these logins hold — folder or group names: 1; "
            + "addresses that are not web addresses: 1; custom fields: 1. It is left out, unless you choose below to copy it into the notes.",
            Text(view, "ImportExtras"));

        // The list is there to be opened, and closed again.
        Button toggle = TheButton(view, "ToggleNotImportedButton");
        Assert.Equal("Show them", toggle.Content);
        Assert.False(Shown(view, "ImportNotImportedList"));
        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(EverythingInTheWindow(window), text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        HeadlessUi.Press(window, toggle);
        Assert.True(Shown(view, "ImportNotImportedList"));
        Assert.Equal("Hide them", toggle.Content);
        Assert.Equal(["Entry 2", "Entry 3", "Entry 4"], Rows(view, "NotImportedPosition"));
        Assert.Equal(["Notes too long", "Address that is none"], Rows(view, "NotImportedName")); // the row that is out of step has no name to show
        Assert.Equal(
            [
                ImportTexts.Text(ImportReason.NotesTooLong),
                ImportTexts.Text(ImportReason.UrlInvalid),
                ImportTexts.Text(ImportReason.RowHasExtraValues),
            ],
            Rows(view, "NotImportedReason"));

        // With the list open, and with the extras copied into the notes: still nothing of the file
        // but names, counts and reasons, in anything the window holds.
        HeadlessUi.Toggle(window, HeadlessUi.Find<CheckBox>(view, "CopyExtrasCheckBox"));
        await HeadlessUi.WaitUntilAsync(
            () => Text(view, "ImportExtras")?.EndsWith("It is copied into each item's notes.", StringComparison.Ordinal) ?? false,
            "the preview follows the choice");
        List<string> everything = EverythingInTheWindow(window);
        Assert.Contains("Notes too long", everything);
        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(everything, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Empty(Nameless(window));
        HeadlessUi.Press(window, toggle);
        Assert.False(Shown(view, "ImportNotImportedList"));
    });

    [Fact]
    public Task AFileThatAddsNothing_CannotBeImported() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ImportPath = Write("bank.csv", "name,url,username,password\nBank,https://bank.example.com/,fake.banker,FAKE-pw-bank\n") };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, files));

        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportPreviewPanel"), "the preview is shown");

        Assert.Equal("Nothing in this file would be added to the vault.", Text(view, "ImportToAdd"));
        Assert.Equal("Import", TheButton(view, "ConfirmImportButton").Content);
        Assert.False(TheButton(view, "ConfirmImportButton").IsEffectivelyEnabled);
        Assert.True(TheButton(view, "CancelImportButton").IsEffectivelyEnabled);
    });

    [Fact]
    public Task ARefusedFile_IsRefusedInWords_AndNoPreviewIsShown() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ImportPath = Write("encrypted.json", "{\"encrypted\":true,\"items\":[]}") };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, files));

        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportStatus"), "the refusal is shown");

        Assert.Equal(ImportTexts.Text(ImportRefusal.EncryptedExport), Text(view, "ImportStatus"));
        Assert.Equal("encrypted.json", Text(view, "ImportFileName"));
        Assert.False(Shown(view, "ImportPreviewPanel"));
        Assert.False(Shown(view, "ImportFileReminder"));
        Assert.Equal(VaultState.Unlocked, vault.State);
    });

    // ---- Export ----

    [Fact]
    public Task AnExport_FromTheKeyboard_TheBoxAndThePasswordFirst() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        await vault.AddLoginAsync(new LoginItem { Name = "Two addresses", Password = "FAKE-pw-two", Urls = ["https://one.example.com/", "https://two.example.com/"] });
        string destination = Path.Combine(_directory, "export.csv");
        var files = new FakeFilePicker { ExportPath = destination };
        SettingsPageViewModel viewModel = NewViewModel(vault, files);
        (Window window, SettingsPageView view) = Show(viewModel);

        HeadlessUi.Press(window, TheButton(view, "ExportUnencryptedButton"));

        Assert.True(Shown(view, "ExportPanel"));
        Assert.Equal(ExportTexts.Warning, Text(view, "ExportWarning"));
        Assert.Equal(
            "1 item in this vault has more than one address. A CSV file holds only the first address of each item; only the JSON file keeps them all.",
            Text(view, "ExportSeveralUrlsNote"));
        Assert.True(HeadlessUi.Find<RadioButton>(view, "ExportCsvRadio").IsChecked);
        Assert.False(HeadlessUi.Find<RadioButton>(view, "ExportJsonRadio").IsChecked);
        CheckBox acknowledge = HeadlessUi.Find<CheckBox>(view, "ExportAcknowledgeCheckBox");
        Assert.Equal(ExportTexts.Acknowledgement, acknowledge.Content);
        Assert.False(acknowledge.IsChecked);
        SecretBox password = HeadlessUi.Find<SecretBox>(view, "ExportPasswordBox");
        Assert.Equal("Master password, to export the vault unencrypted", ControlAutomationPeer.CreatePeerForElement(password).GetName());
        Button export = TheButton(view, "WriteExportButton");
        Assert.False(export.IsEffectivelyEnabled);
        Assert.False(TheButton(view, "ExportUnencryptedButton").IsEffectivelyEnabled); // it is open already
        Assert.Empty(Nameless(window));

        // A password alone does not enable it; nor does the box alone.
        HeadlessUi.Type(window, password, "not the master password");
        Assert.False(export.IsEffectivelyEnabled);
        HeadlessUi.Toggle(window, acknowledge);
        Assert.True(export.IsEffectivelyEnabled);
        password.Text = string.Empty;
        HeadlessUi.Settle();
        Assert.False(export.IsEffectivelyEnabled);

        // What is typed is handed out as dots, like every other password on the page.
        HeadlessUi.Type(window, password, "not the master password");
        Assert.Equal(new string(SecretBox.DefaultPasswordChar, "not the master password".Length), password.DisplayedText);
        Assert.DoesNotContain(EverythingInTheWindow(window).Where(t => t != password.Text), text => text.Contains("not the master password", StringComparison.Ordinal));

        // A wrong password: nothing is written, the box is emptied, the export stays open.
        HeadlessUi.Press(window, export);
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ExportStatus"), "the export reports");
        Assert.Equal("The master password is incorrect. Nothing was exported.", Text(view, "ExportStatus"));
        Assert.True(string.IsNullOrEmpty(password.Text));
        Assert.False(File.Exists(destination));
        Assert.True(Shown(view, "ExportPanel"));
        Assert.True(acknowledge.IsChecked);
        Assert.False(export.IsEffectivelyEnabled);

        // The right one, and BitosX JSON chosen with the keyboard.
        files.ExportPath = Path.Combine(_directory, "export.json");
        RadioButton json = HeadlessUi.Find<RadioButton>(view, "ExportJsonRadio");
        json.Focus();
        HeadlessUi.Key(window, Avalonia.Input.PhysicalKey.Space);
        Assert.True(json.IsChecked);
        Assert.False(HeadlessUi.Find<RadioButton>(view, "ExportCsvRadio").IsChecked);
        Assert.True(viewModel.ImportExport.IsExportJson);
        HeadlessUi.Type(window, password, MasterPassword);
        HeadlessUi.Press(window, export);
        await HeadlessUi.WaitUntilAsync(() => !Shown(view, "ExportPanel"), "the export is written");

        Assert.Equal(
            "2 items were exported as BitosX JSON. The file is not encrypted: delete it when you no longer need it.",
            Text(view, "ExportStatus"));
        Assert.Equal("json", files.LastExportRequest!.Value.Extension);
        ParsedImport written = ImportParser.Parse(await File.ReadAllBytesAsync(files.ExportPath));
        Assert.Equal(["Bank", "Two addresses"], written.Entries.Select(e => e.Name));
        Assert.Equal(2, written.Entries[1].Addresses.Count);
        Assert.False(File.Exists(destination));
        Assert.True(string.IsNullOrEmpty(password.Text));
        Assert.True(TheButton(view, "ExportUnencryptedButton").IsEffectivelyEnabled);
    });

    [Fact]
    public Task CancellingAnExport_ClosesIt_AndTheNextOneStartsFromNothing() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ExportPath = Path.Combine(_directory, "export.csv") };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, files));
        HeadlessUi.Press(window, TheButton(view, "ExportUnencryptedButton"));
        HeadlessUi.Toggle(window, HeadlessUi.Find<CheckBox>(view, "ExportAcknowledgeCheckBox"));
        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "ExportPasswordBox"), MasterPassword);
        Assert.True(TheButton(view, "WriteExportButton").IsEffectivelyEnabled);

        HeadlessUi.Press(window, TheButton(view, "CancelExportButton"));

        Assert.False(Shown(view, "ExportPanel"));
        Assert.False(Shown(view, "ExportStatus"));
        Assert.Equal(0, files.ExportAsked);
        Assert.False(File.Exists(files.ExportPath));

        HeadlessUi.Press(window, TheButton(view, "ExportUnencryptedButton"));
        Assert.False(HeadlessUi.Find<CheckBox>(view, "ExportAcknowledgeCheckBox").IsChecked);
        Assert.True(string.IsNullOrEmpty(HeadlessUi.Find<TextBox>(view, "ExportPasswordBox").Text));
        Assert.False(TheButton(view, "WriteExportButton").IsEffectivelyEnabled);
        Assert.False(Shown(view, "ExportSeveralUrlsNote")); // no item of this vault has more than one address
    });

    // ---- Locking ----

    [Fact]
    public Task Locking_EmptiesTheCard_WhileThePageIsStillThere() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var files = new FakeFilePicker { ImportPath = Write("logins.csv", ThreeLogins) };
        (Window window, SettingsPageView view) = Show(NewViewModel(vault, files));
        HeadlessUi.Press(window, TheButton(view, "ChooseImportFileButton"));
        await HeadlessUi.WaitUntilAsync(() => Shown(view, "ImportPreviewPanel"), "the preview is shown");
        HeadlessUi.Press(window, TheButton(view, "ExportUnencryptedButton"));
        HeadlessUi.Toggle(window, HeadlessUi.Find<CheckBox>(view, "ExportAcknowledgeCheckBox"));
        TextBox password = HeadlessUi.Find<TextBox>(view, "ExportPasswordBox");
        HeadlessUi.Type(window, password, MasterPassword);

        // From another thread, as the timer and the operating system lock it.
        await Task.Run(vault.Lock);
        await HeadlessUi.WaitUntilAsync(() => !Shown(view, "ImportPreviewPanel"), "the card follows the lock");

        Assert.False(Shown(view, "ExportPanel"));
        Assert.False(Shown(view, "ImportFileName"));
        Assert.False(Shown(view, "ImportStatus"));
        Assert.False(Shown(view, "ExportStatus"));
        Assert.True(string.IsNullOrEmpty(password.Text));
        Assert.False(HeadlessUi.Find<CheckBox>(view, "ExportAcknowledgeCheckBox").IsChecked);
        List<string> everything = EverythingInTheWindow(window);
        Assert.DoesNotContain(everything, text => text.Contains("FAKE-pw", StringComparison.Ordinal));
        Assert.DoesNotContain(everything, text => text.Contains(MasterPassword, StringComparison.Ordinal));
    });
}
