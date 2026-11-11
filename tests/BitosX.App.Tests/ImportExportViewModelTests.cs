using System.Collections;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using BitosX.App.ViewModels;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// The "Import and export" card, driven through its view model on top of the real vault service,
/// the real import and export services and real files in a temporary directory. Only the file
/// dialogs are stood in for, and — where a test has to hold a call back or count it — the services
/// are wrapped, not replaced.
/// </summary>
public sealed class ImportExportViewModelTests : IDisposable
{
    private const string MasterPassword = "correct horse battery";

    // What a preview must never show. Each stands where the kind of value it names stands in a file.
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

    private readonly string _directory;

    public ImportExportViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BitosX.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- What the tests stand on ----

    private string VaultPath => Path.Combine(_directory, "vault.bitosx");

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        return path;
    }

    /// <summary>An unlocked vault that holds the login called Bank of <see cref="ThreeLogins"/>, and the card on top of it.</summary>
    private async Task<Card> NewCardAsync(bool withBank = true)
    {
        var vault = new VaultService(
            VaultCryptor.CreateDefault(), new FileLocalVaultStorage(VaultPath), Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await vault.CreateVaultAsync(MasterPassword);
        if (withBank)
        {
            await vault.AddLoginAsync(new LoginItem
            {
                Name = "Bank", Username = "fake.banker", Password = "FAKE-pw-bank", Urls = ["https://bank.example.com/"],
            });
        }

        return new Card(vault, VaultPath);
    }

    private sealed class Card : IDisposable
    {
        public Card(VaultService vault, string vaultPath)
        {
            Vault = vault;
            var files = new FileImportExportFiles(vaultPath);
            Import = new WrappedImport(new VaultImportService(vault, files, Clock, NullLogger<VaultImportService>.Instance));
            Export = new WrappedExport(new VaultExportService(vault, files, Clock, NullLogger<VaultExportService>.Instance));
            Vm = new ImportExportViewModel(Import, Export, vault, Files, InlineUiDispatcher.Instance, Clock);
        }

        public VaultService Vault { get; }

        public FakeFilePicker Files { get; } = new();

        public FixedClock Clock { get; } = new();

        public WrappedImport Import { get; }

        public WrappedExport Export { get; }

        public ImportExportViewModel Vm { get; }

        public async Task ChooseAsync(string path)
        {
            Files.ImportPath = path;
            await Vm.ChooseImportFileCommand.ExecuteAsync(null);
        }

        public void OpenExport(string password, bool acknowledged = true)
        {
            Vm.OpenExportCommand.Execute(null);
            Vm.ExportAcknowledged = acknowledged;
            Vm.ExportPassword = password;
        }

        public void Dispose()
        {
            Vm.Dispose();
            Vault.Dispose();
        }
    }

    /// <summary>10 November 2026, half past nine in the morning, in a time zone that is UTC.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 9, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>The real import service, counted, and held back or made to fail where a test asks for it.</summary>
    private sealed class WrappedImport(IVaultImportService inner) : IVaultImportService
    {
        public int Reads { get; private set; }

        public int Previews { get; private set; }

        public int Applies { get; private set; }

        /// <summary>The file that was read last. Weak: this must not be what keeps it alive.</summary>
        public WeakReference<ParsedImport>? LastFile { get; private set; }

        public Exception? ReadThrows { get; set; }

        public Exception? ApplyThrows { get; set; }

        /// <summary>When set, a read does not answer until this completes.</summary>
        public TaskCompletionSource? ReadHeld { get; set; }

        /// <summary>When set, every preview is made at once but answered only when its entry here completes.</summary>
        public List<TaskCompletionSource>? PreviewsHeld { get; set; }

        public async Task<ParsedImport> ReadAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (ReadThrows is not null)
            {
                throw ReadThrows;
            }

            ParsedImport file = await inner.ReadAsync(sourcePath, cancellationToken);
            LastFile = new WeakReference<ParsedImport>(file);
            if (ReadHeld is not null)
            {
                await ReadHeld.Task;
            }

            return file;
        }

        public async Task<ImportPlan> PreviewAsync(ParsedImport file, ImportOptions options, CancellationToken cancellationToken = default)
        {
            Previews++;
            ImportPlan plan = await inner.PreviewAsync(file, options, cancellationToken);
            if (PreviewsHeld is not null)
            {
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                PreviewsHeld.Add(held);
                await held.Task;
            }

            return plan;
        }

        public Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default)
        {
            Applies++;
            return ApplyThrows is not null ? Task.FromException<ImportResult>(ApplyThrows) : inner.ApplyAsync(plan, cancellationToken);
        }
    }

    /// <summary>The real export service, counted, and made to fail where a test asks for it.</summary>
    private sealed class WrappedExport(IVaultExportService inner) : IVaultExportService
    {
        public int Exports { get; private set; }

        public Exception? Throws { get; set; }

        public Task<ExportResult> ExportAsync(
            string destinationPath, ExportFormat format, string masterPassword, CancellationToken cancellationToken = default)
        {
            Exports++;
            return Throws is not null
                ? Task.FromException<ExportResult>(Throws)
                : inner.ExportAsync(destinationPath, format, masterPassword, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Waited 20 s, but this did not happen: {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Every text a binding could reach from <paramref name="root"/>: its public properties, those of
    /// the BitosX objects they hold, and what their lists hold. A plan or a parsed file among them
    /// would bring its passwords along, which is what this is for.
    /// </summary>
    private static List<string> EverythingBindable(object root)
    {
        var texts = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            object current = pending.Pop();
            if (current is string text)
            {
                texts.Add(text);
                continue;
            }

            Type type = current.GetType();
            if (type.IsPrimitive || type.IsEnum || current is Task or Delegate or ICommand || !seen.Add(current))
            {
                continue;
            }

            if (current is IEnumerable items)
            {
                foreach (object? item in items)
                {
                    if (item is not null)
                    {
                        pending.Push(item);
                    }
                }
            }

            if (type.Namespace?.StartsWith("BitosX.", StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0 && property.GetValue(current) is { } value)
                {
                    pending.Push(value);
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// Fails unless nothing holds the logins that were read from a file any more: not the card, not a
    /// command of it, not a call that has ended. Asked of the garbage collector, a number of times.
    /// </summary>
    private static async Task AssertLetGoOfAsync(WeakReference<ParsedImport>? file)
    {
        Assert.NotNull(file);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!IsHeld(file))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The logins that were read from the file are still held by something.");
    }

    // In a method of its own, so that looking does not itself keep what is looked for.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsHeld(WeakReference<ParsedImport> file) => file.TryGetTarget(out _);

    // =====================================================================================
    // Import
    // =====================================================================================

    [Fact]
    public async Task ChoosingAFile_ShowsWhatWouldBeAdded_AndWritesNothing()
    {
        using Card card = await NewCardAsync();
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);
        string file = Write("passwords from chrome.csv", ThreeLogins);
        byte[] fileBefore = File.ReadAllBytes(file);

        Assert.False(card.Vm.HasImportPreview);
        await card.ChooseAsync(file);

        ImportPreview preview = Assert.IsType<ImportPreview>(card.Vm.Preview);
        Assert.Equal("Chrome or Edge (CSV): 3 entries in the file.", preview.Source);
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", preview.ToAdd);
        Assert.Equal("1 login is already in the vault, or in the file more than once, and is skipped.", preview.Duplicates);
        Assert.Equal("https:// is put in front of 1 address that has no scheme.", preview.HttpsAdded);
        Assert.Null(preview.NotImported);
        Assert.Null(preview.OverLimit);
        Assert.Equal("Import 2 logins", preview.ConfirmText);
        Assert.True(card.Vm.IsPreviewCurrent);
        Assert.True(card.Vm.ConfirmImportCommand.CanExecute(null));

        // The name of the file, without its folder; and both choices as they are at first.
        Assert.Equal("passwords from chrome.csv", card.Vm.ImportFileName);
        Assert.False(card.Vm.CopyExtrasToNotes);
        Assert.False(card.Vm.ImportDuplicates);
        Assert.False(card.Vm.IsImportBusy);
        Assert.Null(card.Vm.ImportStatus);

        // Nothing was written: not the vault, not the file.
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
        Assert.Equal(fileBefore, File.ReadAllBytes(file));
        Assert.Single(card.Vault.Items);
        Assert.Equal(0, card.Import.Applies);
    }

    [Fact]
    public async Task ThePreview_SaysEverythingThePlanHolds()
    {
        using Card card = await NewCardAsync();
        string file = Write("bitwarden.json", """
            {
              "encrypted": false,
              "folders": [ { "id": "f1", "name": "Fake folder" } ],
              "items": [
                { "type": 1, "name": "With everything", "folderId": "f1", "notes": "a note",
                  "fields": [ { "name": "PIN", "value": "FAKE-1234", "type": 1 } ],
                  "login": { "username": "fake.one", "password": "FAKE-pw-1", "totp": "FAKEFAKEFAKEFAKE",
                             "uris": [ { "uri": "https://one.example.com/" }, { "uri": "androidapp://com.example.fake" } ] } },
                { "type": 1, "name": "", "login": { "username": "fake.two", "password": "FAKE-pw-2", "uris": [ { "uri": "two.example.com" } ] } },
                { "type": 1, "name": "Bank", "login": { "username": "fake.banker", "password": "FAKE-pw-bank", "uris": [ { "uri": "https://bank.example.com/" } ] } },
                { "type": 1, "name": "Address that is none", "login": { "username": "fake.four", "password": "FAKE-pw-4", "uris": [ { "uri": "https://" } ] } },
                { "type": 1, "name": "", "login": { } },
                { "type": 2, "name": "A secure note", "notes": "FAKE note" },
                { "type": 2, "name": "Another secure note", "notes": "FAKE note" },
                { "type": 3, "name": "A card" },
                { "type": 1, "name": "Thrown away", "deletedDate": "2026-01-02T03:04:05Z", "login": { "password": "FAKE-pw-7" } }
              ]
            }
            """);

        await card.ChooseAsync(file);

        ImportPreview preview = Assert.IsType<ImportPreview>(card.Vm.Preview);
        Assert.Equal("Bitwarden (JSON): 9 entries in the file.", preview.Source);
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", preview.ToAdd);
        Assert.Equal("1 login is already in the vault, or in the file more than once, and is skipped.", preview.Duplicates);
        Assert.Equal("1 entry cannot be imported.", preview.NotImported);
        ImportPreviewEntry entry = Assert.Single(preview.NotImportedEntries);
        Assert.Equal(new ImportPreviewEntry("Entry 4", "Address that is none", ImportTexts.Text(ImportReason.UrlInvalid)), entry);
        Assert.Null(preview.NotImportedMore);
        Assert.Equal("BitosX stores logins only. Not imported — secure notes: 2; cards: 1.", preview.Unsupported);
        Assert.Equal("1 entry is in the trash or recycle bin of the file and is not imported.", preview.InTrash);
        Assert.Equal("1 entry has nothing in it and is passed over.", preview.Empty);
        Assert.Equal(
            "BitosX has no field for some of what these logins hold — authenticator keys (TOTP): 1; folder or group names: 1; "
            + "addresses that are not web addresses: 1; custom fields: 1. It is left out, unless you choose below to copy it into the notes.",
            preview.Extras);
        Assert.Equal("https:// is put in front of 1 address that has no scheme.", preview.HttpsAdded);
        Assert.Equal("1 login has no name in the file. It is named after its address or its username.", preview.NamesDerived);
        Assert.Null(preview.IgnoredColumns);
        Assert.Null(preview.OverLimit);
        Assert.True(preview.CanBeImported);
    }

    [Fact]
    public async Task ThePreview_NamesTheColumnsThatAreNotRead()
    {
        using Card card = await NewCardAsync();
        string file = Write("other.csv", "Title,Website,Password,Tags,Last Used\nFake,https://fake.example.com,FAKE-pw,a tag,yesterday\n");

        await card.ChooseAsync(file);

        Assert.Equal("CSV: 1 entry in the file.", card.Vm.Preview!.Source);
        Assert.Equal("Columns of the file that are not read: Tags, Last Used.", card.Vm.Preview.IgnoredColumns);
        Assert.Equal("1 login will be added. The vault holds 1 item now and will hold 2.", card.Vm.Preview.ToAdd);
        Assert.Equal("Import 1 login", card.Vm.Preview.ConfirmText);
    }

    [Fact]
    public async Task EachChoice_AsksForThePreviewAgain()
    {
        using Card card = await NewCardAsync();
        string file = Write("bitwarden.csv",
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n"
            + "Fake folder,,login,Mail,,,0,https://mail.example.com/,fake.user,FAKE-pw-mail,FAKEFAKEFAKEFAKE\n"
            + ",,login,Bank,,,0,https://bank.example.com/,fake.banker,FAKE-pw-bank,\n");
        await card.ChooseAsync(file);
        Assert.Equal(1, card.Import.Previews);
        Assert.Equal("1 login will be added. The vault holds 1 item now and will hold 2.", card.Vm.Preview!.ToAdd);
        Assert.EndsWith("It is left out, unless you choose below to copy it into the notes.", card.Vm.Preview.Extras, StringComparison.Ordinal);

        card.Vm.ImportDuplicates = true;
        await card.Vm.PreviewRefresh;
        Assert.Equal(2, card.Import.Previews);
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", card.Vm.Preview!.ToAdd);
        Assert.Equal("1 login is already in the vault, or in the file more than once. It is added as well.", card.Vm.Preview.Duplicates);
        Assert.Equal("Import 2 logins", card.Vm.Preview.ConfirmText);

        card.Vm.CopyExtrasToNotes = true;
        await card.Vm.PreviewRefresh;
        Assert.Equal(3, card.Import.Previews);
        Assert.EndsWith("It is copied into each item's notes.", card.Vm.Preview!.Extras, StringComparison.Ordinal);

        card.Vm.ImportDuplicates = false;
        await card.Vm.PreviewRefresh;
        Assert.Equal(4, card.Import.Previews);
        Assert.Equal("1 login will be added. The vault holds 1 item now and will hold 2.", card.Vm.Preview!.ToAdd);
        Assert.True(card.Vm.IsPreviewCurrent);

        // And what is imported is what the choices ask for: the extras are in the notes.
        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        LoginItem mail = Assert.Single(card.Vault.Items.OfType<LoginItem>(), i => i.Name == "Mail");
        Assert.Equal("Authenticator key (TOTP): FAKEFAKEFAKEFAKE\nFolder: Fake folder", mail.Notes);
        Assert.Equal(2, card.Vault.Items.Count);
    }

    [Fact]
    public async Task WhileAPreviewIsOnItsWay_NothingCanBeImported()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        Assert.True(card.Vm.ConfirmImportCommand.CanExecute(null));

        card.Import.PreviewsHeld = [];
        card.Vm.ImportDuplicates = true;

        // The preview on screen is the old one; the choice on screen is the new one.
        await WaitUntilAsync(() => card.Import.PreviewsHeld.Count == 1, "the preview is asked for");
        Assert.False(card.Vm.IsPreviewCurrent);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", card.Vm.Preview!.ToAdd);

        // Not even by going past the button.
        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(0, card.Import.Applies);
        Assert.Single(card.Vault.Items);

        card.Import.PreviewsHeld[0].SetResult();
        await card.Vm.PreviewRefresh;
        Assert.True(card.Vm.IsPreviewCurrent);
        Assert.True(card.Vm.ConfirmImportCommand.CanExecute(null));
        Assert.Equal("3 logins will be added. The vault holds 1 item now and will hold 4.", card.Vm.Preview!.ToAdd);
    }

    [Fact]
    public async Task OfTwoPreviewsOnTheirWay_OnlyTheLaterOneIsShown()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));

        card.Import.PreviewsHeld = [];
        card.Vm.ImportDuplicates = true;   // first: 3 to add
        await WaitUntilAsync(() => card.Import.PreviewsHeld.Count == 1, "the first preview is asked for");
        card.Vm.ImportDuplicates = false;  // second: 2 to add
        await WaitUntilAsync(() => card.Import.PreviewsHeld.Count == 2, "the second preview is asked for");

        // The later one answers first, the earlier one after it.
        card.Import.PreviewsHeld[1].SetResult();
        await card.Vm.PreviewRefresh;
        Assert.True(card.Vm.IsPreviewCurrent);
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", card.Vm.Preview!.ToAdd);

        card.Import.PreviewsHeld[0].SetResult();
        await Task.Delay(100);
        Assert.Equal("2 logins will be added. The vault holds 1 item now and will hold 3.", card.Vm.Preview!.ToAdd);
        Assert.False(card.Vm.ImportDuplicates);
        Assert.True(card.Vm.IsPreviewCurrent);
    }

    [Fact]
    public async Task AVaultThatWouldHoldTooManyItems_CanTakeNothing()
    {
        using Card card = await NewCardAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await card.Vault.AddLoginsAsync(_ =>
        [
            .. Enumerable.Range(0, ImportLimits.MaxVaultItems - 2).Select(i => new LoginItem
            {
                Id = Guid.NewGuid(), Name = "Filler " + i, CreatedAt = now, UpdatedAt = now,
            }),
        ]);
        Assert.Equal(ImportLimits.MaxVaultItems - 1, card.Vault.Items.Count);
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);

        await card.ChooseAsync(Write("logins.csv", ThreeLogins)); // two of the three are new: one too many

        ImportPreview preview = card.Vm.Preview!;
        Assert.Equal(ImportTexts.Text(ImportRefusal.VaultItemLimit), preview.OverLimit);
        Assert.StartsWith("2 logins are in this file to add. The vault holds ", preview.ToAdd, StringComparison.Ordinal);
        Assert.False(preview.CanBeImported);
        Assert.Equal("Import", preview.ConfirmText);
        Assert.True(card.Vm.IsPreviewCurrent);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(0, card.Import.Applies);
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public async Task Importing_AddsTheLogins_SaysHowMany_AndRemindsOfTheFile()
    {
        using Card card = await NewCardAsync();
        string file = Write("logins.csv", ThreeLogins);
        await card.ChooseAsync(file);

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);

        Assert.Equal(["Bank", "Mail", "Shop"], card.Vault.Items.Select(i => i.Name).Order());
        LoginItem shop = Assert.Single(card.Vault.Items.OfType<LoginItem>(), i => i.Name == "Shop");
        Assert.Equal(["https://shop.example.com"], shop.Urls);
        Assert.Equal("FAKE-pw-shop", shop.Password);
        Assert.Equal("a note", shop.Notes);
        Assert.Equal(card.Clock.GetUtcNow(), shop.CreatedAt);

        Assert.Equal("2 logins were added to the vault.", card.Vm.ImportStatus);
        Assert.Equal(ImportTexts.FileStillHoldsPasswords, card.Vm.ImportReminder);
        Assert.Equal("logins.csv", card.Vm.ImportFileName);

        // The import is over: no preview, nothing to confirm, the choices as they are at first.
        Assert.False(card.Vm.HasImportPreview);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
        Assert.False(card.Vm.IsImportBusy);
        Assert.Equal(1, card.Import.Applies);
        await AssertLetGoOfAsync(card.Import.LastFile);

        // The file is not touched: deleting it is the user's to do.
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ASecondImportOfTheSameFile_FindsOnlyDuplicates()
    {
        using Card card = await NewCardAsync();
        string file = Write("logins.csv", ThreeLogins);
        await card.ChooseAsync(file);
        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(3, card.Vault.Items.Count);
        byte[] vaultAfterTheFirst = File.ReadAllBytes(VaultPath);

        await card.ChooseAsync(file);

        ImportPreview preview = card.Vm.Preview!;
        Assert.Equal("Nothing in this file would be added to the vault.", preview.ToAdd);
        Assert.Equal("3 logins are already in the vault, or in the file more than once, and are skipped.", preview.Duplicates);
        Assert.False(preview.CanBeImported);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));

        // What the first import said is gone with it.
        Assert.Null(card.Vm.ImportStatus);
        Assert.Null(card.Vm.ImportReminder);

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(vaultAfterTheFirst, File.ReadAllBytes(VaultPath));
        Assert.Equal(1, card.Import.Applies);
    }

    [Fact]
    public async Task Cancel_ChangesNothing_AndLetsGoOfTheFile()
    {
        using Card card = await NewCardAsync();
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        card.Vm.ImportDuplicates = true;
        await card.Vm.PreviewRefresh;

        card.Vm.CancelImportCommand.Execute(null);

        Assert.False(card.Vm.HasImportPreview);
        Assert.Null(card.Vm.Preview);
        Assert.Null(card.Vm.ImportFileName);
        Assert.Null(card.Vm.ImportStatus);
        Assert.Null(card.Vm.ImportReminder);
        Assert.False(card.Vm.ImportDuplicates);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
        Assert.Equal(0, card.Import.Applies);
        await AssertLetGoOfAsync(card.Import.LastFile);
    }

    [Fact]
    public async Task ACancelledDialog_ChangesNothing()
    {
        using Card card = await NewCardAsync();
        card.Files.ImportPath = null;

        await card.Vm.ChooseImportFileCommand.ExecuteAsync(null);

        Assert.Equal(1, card.Files.ImportAsked);
        Assert.Equal(0, card.Import.Reads);
        Assert.False(card.Vm.HasImportPreview);
        Assert.Null(card.Vm.ImportFileName);
        Assert.Null(card.Vm.ImportStatus);
    }

    [Fact]
    public async Task ALocationThatIsNotOnThisComputer_IsSaidToBe()
    {
        using Card card = await NewCardAsync();
        card.Files.ChoosesANonLocalLocation = true;

        await card.Vm.ChooseImportFileCommand.ExecuteAsync(null);
        Assert.Contains("not a folder on this computer", card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.Equal(0, card.Import.Reads);

        card.OpenExport(MasterPassword);
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        Assert.Contains("not a folder on this computer", card.Vm.ExportStatus, StringComparison.Ordinal);
        Assert.Equal(0, card.Export.Exports);
        Assert.Equal(MasterPassword, card.Vm.ExportPassword); // nothing was tried: what was typed is still there
    }

    [Fact]
    public async Task AFileThatIsRefused_IsRefusedInItsFixedWords_AndNothingIsOffered()
    {
        using Card card = await NewCardAsync();
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);

        var files = new (string Name, byte[] Content, ImportRefusal Kind)[]
        {
            ("not-utf8.csv", [0x6E, 0x61, 0x6D, 0x65, 0x2C, 0xFF, 0xFE, 0x0A], ImportRefusal.InvalidUtf8),
            ("utf16.csv", [0xFF, 0xFE, 0x6E, 0x00, 0x61, 0x00], ImportRefusal.UnsupportedEncoding),
            ("empty.csv", [], ImportRefusal.Empty),
            ("columns.csv", Encoding.UTF8.GetBytes("name;url;password\nFake;https://fake.example;FAKE-pw\n"), ImportRefusal.NoRecognisedColumns),
            ("encrypted.json", Encoding.UTF8.GetBytes("{\"encrypted\":true,\"items\":[]}"), ImportRefusal.EncryptedExport),
            ("other.json", Encoding.UTF8.GetBytes("{\"something\":1}"), ImportRefusal.NotRecognised),
            ("doctype.xml", Encoding.UTF8.GetBytes("<!DOCTYPE KeePassFile><KeePassFile/>"), ImportRefusal.Doctype),
            ("damaged.xml", Encoding.UTF8.GetBytes("<KeePassFile><Root>"), ImportRefusal.InvalidXml),
        };

        foreach ((string name, byte[] content, ImportRefusal kind) in files)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, content);

            await card.ChooseAsync(path);

            Assert.Equal(ImportTexts.Text(kind), card.Vm.ImportStatus);
            Assert.Equal(name, card.Vm.ImportFileName);
            Assert.False(card.Vm.HasImportPreview);
            Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
            Assert.False(card.Vm.IsImportBusy);
            Assert.Null(card.Vm.ImportReminder);
        }

        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
        Assert.Equal(0, card.Import.Applies);
    }

    [Fact]
    public async Task ADamagedCsvFile_IsRefused_WithTheLineOfTheFault()
    {
        using Card card = await NewCardAsync();

        await card.ChooseAsync(Write("damaged.csv", "name,url,username,password\nFake,https://fake.example,fake.user,FAKE-pw\nFake two,\"https://two.example,fake.two,FAKE-pw-2\n"));

        Assert.Equal(ImportTexts.Text(ImportRefusal.InvalidCsv) + " The fault is on line 3 of the file.", card.Vm.ImportStatus);
        Assert.False(card.Vm.HasImportPreview);
    }

    [Fact]
    public async Task AFileThatIsGone_IsSaidToBeGone_WithoutItsPath()
    {
        using Card card = await NewCardAsync();
        string gone = Path.Combine(_directory, "a folder", "gone.csv");

        await card.ChooseAsync(gone);

        Assert.Equal("The file was not found. It may have been moved or deleted.", card.Vm.ImportStatus);
        Assert.DoesNotContain("vault", card.Vm.ImportStatus, StringComparison.OrdinalIgnoreCase); // not "No vault file was found."
        Assert.DoesNotContain(_directory, card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.False(card.Vm.HasImportPreview);
        Assert.Equal("gone.csv", card.Vm.ImportFileName);
    }

    public static TheoryData<Exception, string> ReadFailures => new()
    {
        { new FileNotFoundException("Could not find file '/home/fake/FAKE-PATH/passwords.csv'."), "The file was not found. It may have been moved or deleted." },
        { new DirectoryNotFoundException("Could not find a part of the path '/home/fake/FAKE-PATH'."), "The file was not found. It may have been moved or deleted." },
        { new UnauthorizedAccessException("Access to the path '/home/fake/FAKE-PATH/passwords.csv' is denied."), "BitosX does not have permission to read that file." },
        { new IOException("The process cannot access the file '/home/fake/FAKE-PATH/passwords.csv'."), "The file could not be read." },
        { new InvalidDataException("Unexpected token near FAKE-PATH FAKE-SECRET-PASSWORD"), "The file could not be read. Nothing was imported." },
        { new FormatException("FAKE-PATH FAKE-SECRET-PASSWORD is not a number"), "The file could not be read. Nothing was imported." },
        { new ArgumentException("FAKE-PATH FAKE-SECRET-PASSWORD"), "The file could not be read. Nothing was imported." },
        { new InvalidOperationException("FAKE-PATH"), "The vault is locked. Nothing was imported." },
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task WhatGoesWrongWhileReading_IsToldApartByItsKind_AndNeverInAnExceptionsWords(Exception failure, string expected)
    {
        using Card card = await NewCardAsync();
        card.Import.ReadThrows = failure;

        await card.ChooseAsync(Write("logins.csv", ThreeLogins));

        Assert.Equal(expected, card.Vm.ImportStatus);
        Assert.DoesNotContain("FAKE-PATH", card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPassword, card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.False(card.Vm.HasImportPreview);
        Assert.False(card.Vm.IsImportBusy);

        // The vault stays open, and another file can be chosen.
        Assert.Equal(VaultState.Unlocked, card.Vault.State);
        Assert.True(card.Vm.ChooseImportFileCommand.CanExecute(null));
    }

    public static TheoryData<Exception, string> ApplyFailures => new()
    {
        { new ImportRefusedException(ImportRefusal.VaultItemLimit), ImportTexts.Text(ImportRefusal.VaultItemLimit) },
        { new VaultTooLargeException(16 * 1024 * 1024), "With these logins the vault would be larger than 16 MiB, which is the most BitosX can store. Nothing was imported." },
        { new VaultChangedExternallyException(), "The vault changed on disk while it was open. Nothing was imported. Lock and reopen the vault, then try again." },
        { new VaultValidationException("'https://FAKE-PATH/FAKE-SECRET-PASSWORD' is not a valid absolute http(s) URL."), "The file holds an entry the vault does not accept. Nothing was imported." },
        { new VaultAuthenticationException(), "The vault could not be processed. Nothing was imported." },
        { new UnauthorizedAccessException("Access to the path '/home/fake/FAKE-PATH/vault.bitosx' is denied."), "BitosX does not have permission to write the vault file. Nothing was imported." },
        { new IOException("There is not enough space on the disk: '/home/fake/FAKE-PATH'."), "Could not save the vault. Nothing was imported." },
    };

    [Theory]
    [MemberData(nameof(ApplyFailures))]
    public async Task WhatGoesWrongWhileImporting_IsSaidInFixedWords_AndThePreviewIsMadeAgain(Exception failure, string expected)
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);
        card.Import.ApplyThrows = failure;

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        await card.Vm.PreviewRefresh;

        Assert.Equal(expected, card.Vm.ImportStatus);
        Assert.DoesNotContain("FAKE-PATH", card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPassword, card.Vm.ImportStatus, StringComparison.Ordinal);
        Assert.Null(card.Vm.ImportReminder);
        Assert.False(card.Vm.IsImportBusy);
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));

        // The file is still open, with a preview made against the vault as it is now.
        Assert.True(card.Vm.HasImportPreview);
        Assert.True(card.Vm.IsPreviewCurrent);
        Assert.Equal(2, card.Import.Previews);
    }

    [Fact]
    public async Task AnImportTheVaultWasLockedUnder_SaysNothingAfterwards()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        card.Import.ApplyThrows = new OperationCanceledException();
        card.Vault.Lock();

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);

        Assert.Equal(0, card.Import.Applies); // the import was closed by the lock; there was nothing to confirm
        Assert.Null(card.Vm.ImportStatus);
        Assert.False(card.Vm.HasImportPreview);
    }

    [Fact]
    public async Task WhatIsBound_HoldsNamesCountsAndReasons_AndNoSecretOfTheFile()
    {
        using Card card = await NewCardAsync();
        string longNotes = new('n', 16385);
        string file = Write("secrets.csv",
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n"
            // Imported.
            + $"{SecretFolder},1,login,Good entry,{SecretNotes},{SecretFieldName}: {SecretFieldValue},0,\"https://{SecretAddress}/,ftp://{SecretAddress}\",{SecretUsername},{SecretPassword},{SecretTotp}\n"
            // Not imported: its notes are too long. Listed by its name.
            + $"{SecretFolder},,login,Notes too long,{SecretNotes}{longNotes},{SecretFieldName}: {SecretFieldValue},0,https://{SecretAddress}/2,{SecretUsername},{SecretPassword},{SecretTotp}\n"
            // Not imported: an address that is none. Listed by its name.
            + $",,login,Address that is none,{SecretNotes},,0,https://,{SecretUsername},{SecretPassword},{SecretTotp}\n"
            // Not imported: a comma that was not quoted moved its values, and its "name" is now half of something else.
            + $",,login,{SecretPassword},{SecretNotes},,0,https://{SecretAddress}/4,{SecretUsername},{SecretPassword},{SecretTotp},{SecretNotes}\n");

        await card.ChooseAsync(file);
        foreach (bool copyExtras in new[] { false, true })
        {
            card.Vm.CopyExtrasToNotes = copyExtras;
            await card.Vm.PreviewRefresh;

            ImportPreview preview = card.Vm.Preview!;
            Assert.Equal("1 login will be added. The vault holds 1 item now and will hold 2.", preview.ToAdd);
            Assert.Equal("3 entries cannot be imported.", preview.NotImported);
            Assert.Equal(
                [
                    new ImportPreviewEntry("Entry 2", "Notes too long", ImportTexts.Text(ImportReason.NotesTooLong)),
                    new ImportPreviewEntry("Entry 3", "Address that is none", ImportTexts.Text(ImportReason.UrlInvalid)),
                    new ImportPreviewEntry("Entry 4", string.Empty, ImportTexts.Text(ImportReason.RowHasExtraValues)),
                ],
                preview.NotImportedEntries);

            List<string> bound = EverythingBindable(card.Vm);
            Assert.Contains("Notes too long", bound);                  // it does reach what is shown
            Assert.Contains(ImportTexts.Text(ImportReason.UrlInvalid), bound);
            foreach (string secret in Secrets)
            {
                Assert.DoesNotContain(bound, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task ANameFromTheFile_IsShownOnOneLine_AndCutToALengthThatFits()
    {
        using Card card = await NewCardAsync();
        string longName = new string('x', 300);
        string file = Write("names.csv",
            "name,url,password\n"
            + $"{longName},https://,FAKE-pw-1\n"
            + "\"two\nlines\tand a tab\",https://,FAKE-pw-2\n");

        await card.ChooseAsync(file);

        IReadOnlyList<ImportPreviewEntry> entries = card.Vm.Preview!.NotImportedEntries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(new string('x', ImportPreview.MaxNameLength) + "…", entries[0].Name);
        Assert.Equal(ImportTexts.Text(ImportReason.NameTooLong), entries[0].Reason);
        Assert.Equal("two lines and a tab", entries[1].Name);
    }

    [Fact]
    public async Task OnlyTheFirstEntriesThatCannotBeImported_AreListed_AndTheRestAreCounted()
    {
        using Card card = await NewCardAsync();
        var rows = new StringBuilder("name,url,password\n");
        for (int i = 1; i <= ImportPreview.MaxListedEntries + 25; i++)
        {
            rows.Append("Fake ").Append(i).Append(",https://,FAKE-pw\n");
        }

        await card.ChooseAsync(Write("many.csv", rows.ToString()));

        ImportPreview preview = card.Vm.Preview!;
        Assert.Equal("225 entries cannot be imported.", preview.NotImported);
        Assert.Equal(ImportPreview.MaxListedEntries, preview.NotImportedEntries.Count);
        Assert.Equal("Fake 200", preview.NotImportedEntries[^1].Name);
        Assert.Equal("… and 25 more.", preview.NotImportedMore);
        Assert.Equal("Nothing in this file would be added to the vault.", preview.ToAdd);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManyColumnsThatAreNotRead_AreNotAllNamed_AndALongNameIsCut()
    {
        using Card card = await NewCardAsync();
        string header = "name,password," + string.Join(',', Enumerable.Range(1, 14).Select(i => i == 1 ? new string('c', 100) : "column " + i));

        await card.ChooseAsync(Write("columns.csv", header + "\nFake,FAKE-pw\n"));

        Assert.Equal(
            "Columns of the file that are not read: " + new string('c', ImportPreview.MaxColumnNameLength) + "…, "
            + string.Join(", ", Enumerable.Range(2, 9).Select(i => "column " + i)) + ", and 4 more.",
            card.Vm.Preview!.IgnoredColumns);
    }

    [Fact]
    public async Task TheListOfEntries_IsShownAndHidden()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("one.csv", "name,url,password\nFake,https://,FAKE-pw\n"));
        Assert.False(card.Vm.IsNotImportedListShown);
        Assert.Equal("Show them", card.Vm.NotImportedToggleText);

        card.Vm.ToggleNotImportedListCommand.Execute(null);
        Assert.True(card.Vm.IsNotImportedListShown);
        Assert.Equal("Hide them", card.Vm.NotImportedToggleText);

        card.Vm.ToggleNotImportedListCommand.Execute(null);
        Assert.False(card.Vm.IsNotImportedListShown);
    }

    [Fact]
    public async Task AVaultThatChangedAfterThePreview_IsComparedWithAgain_AndWhatWasDoneIsWhatIsSaid()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        Assert.Equal("Import 2 logins", card.Vm.Preview!.ConfirmText);

        // As from the vault page, while the preview is open: one of the two is added by hand.
        await card.Vault.AddLoginAsync(new LoginItem
        {
            Name = "Mail", Username = "fake.user@example.test", Password = "FAKE-pw-mail", Urls = ["https://mail.example.com/"],
        });

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);

        Assert.Equal(
            "The vault changed after the preview was made, so the file was compared with it again: 1 login was added, not 2.",
            card.Vm.ImportStatus);
        Assert.Equal(["Bank", "Mail", "Shop"], card.Vault.Items.Select(i => i.Name).Order());
        Assert.Equal(ImportTexts.FileStillHoldsPasswords, card.Vm.ImportReminder);
    }

    [Fact]
    public async Task AVaultThatTookEverythingMeanwhile_IsToldSo()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        await card.Vault.AddLoginAsync(new LoginItem { Name = "Mail", Username = "fake.user@example.test", Password = "FAKE-pw-mail", Urls = ["https://mail.example.com/"] });
        await card.Vault.AddLoginAsync(new LoginItem { Name = "Shop", Username = "fake.shopper", Password = "FAKE-pw-shop", Urls = ["https://shop.example.com"] });
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);

        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);

        Assert.Equal(
            "The vault changed after the preview was made, so the file was compared with it again: nothing was added, because everything in the file is in the vault already.",
            card.Vm.ImportStatus);
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public async Task ComingBackToThePage_MakesThePreviewAgain_AgainstTheVaultAsItIsThen()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        await card.Vault.AddLoginAsync(new LoginItem
        {
            Name = "Mail", Username = "fake.user@example.test", Password = "FAKE-pw-mail", Urls = ["https://mail.example.com/"],
        });

        card.Vm.Refresh();
        await card.Vm.PreviewRefresh;

        Assert.Equal("1 login will be added. The vault holds 2 items now and will hold 3.", card.Vm.Preview!.ToAdd);
        Assert.Equal("2 logins are already in the vault, or in the file more than once, and are skipped.", card.Vm.Preview.Duplicates);
        Assert.True(card.Vm.IsPreviewCurrent);
    }

    // =====================================================================================
    // Export
    // =====================================================================================

    [Fact]
    public async Task AnExport_IsNotPossible_WithoutTheBox_AndWithoutAPassword()
    {
        using Card card = await NewCardAsync();
        card.Files.ExportPath = Path.Combine(_directory, "export.csv");

        // Not open: nothing to write.
        Assert.False(card.Vm.IsExportOpen);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        card.Vm.OpenExportCommand.Execute(null);
        Assert.True(card.Vm.IsExportOpen);
        Assert.False(card.Vm.ExportAcknowledged);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.Equal(ExportTexts.Warning, card.Vm.ExportWarning);
        Assert.Equal(ExportTexts.Acknowledgement, card.Vm.ExportAcknowledgementText);
        Assert.True(card.Vm.IsExportCsv);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        card.Vm.ExportAcknowledged = true;              // the box, without a password
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        card.Vm.ExportAcknowledged = false;             // a password, without the box
        card.Vm.ExportPassword = MasterPassword;
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        // Not even by going past the button.
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        Assert.Equal(0, card.Files.ExportAsked);
        Assert.Equal(0, card.Export.Exports);
        Assert.False(File.Exists(card.Files.ExportPath));

        card.Vm.ExportAcknowledged = true;              // both
        Assert.True(card.Vm.WriteExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDialogComesFirst_AndCallingItOff_ChecksNoPassword()
    {
        using Card card = await NewCardAsync();
        card.OpenExport(MasterPassword);
        card.Files.ExportPath = null; // called off

        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal(1, card.Files.ExportAsked);
        Assert.Equal(0, card.Export.Exports);         // the key was not derived for nothing
        Assert.Null(card.Vm.ExportStatus);
        Assert.True(card.Vm.IsExportOpen);
        Assert.Equal(MasterPassword, card.Vm.ExportPassword); // no attempt was made: what was typed is still there
        Assert.True(card.Vm.WriteExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheRightPassword_WritesTheFile_Once_AndClosesTheExport()
    {
        using Card card = await NewCardAsync();
        await card.Vault.AddLoginAsync(new LoginItem
        {
            Name = "Two addresses", Username = "fake.two", Password = "FAKE-pw-two", Urls = ["https://one.example.com/", "https://two.example.com/"],
        });
        string destination = Path.Combine(_directory, "export.csv");
        card.Files.ExportPath = destination;
        card.OpenExport(MasterPassword);
        Assert.Equal(
            "1 item in this vault has more than one address. A CSV file holds only the first address of each item; only the JSON file keeps them all.",
            card.Vm.ExportSeveralUrlsNote);

        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal(1, card.Files.ExportAsked);
        Assert.Equal(1, card.Export.Exports); // one call: the check and the write
        Assert.Equal(("BitosX-export-2026-11-10.csv", "csv"), card.Files.LastExportRequest);
        Assert.Equal(VaultExporter.ToCsv(card.Vault.Items), File.ReadAllBytes(destination));
        Assert.Equal(
            "2 items were exported as CSV. The file is not encrypted: delete it when you no longer need it. "
            + "1 item has more than one address; the file holds only the first.",
            card.Vm.ExportStatus);

        // Closed, with nothing kept for a next time.
        Assert.False(card.Vm.IsExportOpen);
        Assert.False(card.Vm.ExportAcknowledged);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.False(card.Vm.IsExporting);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
        }
    }

    [Fact]
    public async Task BitosXJson_IsWrittenWhenItIsChosen_AndReadsBackToTheSameLogins()
    {
        using Card card = await NewCardAsync();
        await card.Vault.AddLoginAsync(new LoginItem
        {
            Name = "Two addresses", Username = "fake.two", Password = "FAKE-pw-two", Urls = ["https://one.example.com/", "https://two.example.com/"],
        });
        string destination = Path.Combine(_directory, "export.json");
        card.Files.ExportPath = destination;
        card.OpenExport(MasterPassword);

        card.Vm.IsExportJson = true;
        Assert.False(card.Vm.IsExportCsv);
        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal(("BitosX-export-2026-11-10.json", "json"), card.Files.LastExportRequest);
        Assert.Equal("2 items were exported as BitosX JSON. The file is not encrypted: delete it when you no longer need it.", card.Vm.ExportStatus);
        Assert.Equal(VaultExporter.ToBitosXJson(card.Vault.Items, card.Clock.GetUtcNow()), File.ReadAllBytes(destination));

        // Read back into the vault it came from: everything is there already.
        await card.ChooseAsync(destination);
        Assert.Equal("BitosX (JSON): 2 entries in the file.", card.Vm.Preview!.Source);
        Assert.Equal("Nothing in this file would be added to the vault.", card.Vm.Preview.ToAdd);

        // The next export starts with CSV again.
        card.Vm.OpenExportCommand.Execute(null);
        Assert.True(card.Vm.IsExportCsv);
        Assert.False(card.Vm.IsExportJson);
    }

    [Fact]
    public async Task AWrongPassword_WritesNothing_SaysSo_AndThePasswordBoxIsEmptied()
    {
        using Card card = await NewCardAsync();
        string destination = Write("an-existing-file.csv", "what was here before");
        string?[] filesBefore = [.. Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order()];
        card.Files.ExportPath = destination;
        card.OpenExport("not the master password");

        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal("The master password is incorrect. Nothing was exported.", card.Vm.ExportStatus);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.Equal("what was here before", File.ReadAllText(destination));
        Assert.Equal(filesBefore, Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order()); // nothing was left behind either

        // Still open, with the box still ticked: the password can be typed again.
        Assert.True(card.Vm.IsExportOpen);
        Assert.True(card.Vm.ExportAcknowledged);
        Assert.False(card.Vm.IsExporting);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null)); // nothing is typed

        // And with the right one the same file is written.
        card.Vm.ExportPassword = MasterPassword;
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        Assert.Equal(VaultExporter.ToCsv(card.Vault.Items), File.ReadAllBytes(destination));
        Assert.Equal(2, card.Export.Exports);
    }

    public static TheoryData<Exception, string> ExportFailures => new()
    {
        { new VaultAuthenticationException(), "The master password is incorrect. Nothing was exported." },
        { new KeyDerivationResourceException(65536, new InvalidOperationException("no memory")), "This device does not have enough free memory to derive the key (64 MiB is needed). Nothing was exported." },
        { new UnsupportedVaultFormatException("FAKE-PATH"), "The vault could not be read to check the master password. Nothing was exported." },
        { new VaultChangedExternallyException(), "The vault changed on disk while it was open, so the master password could not be checked. Nothing was exported. Lock and reopen the vault, then try again." },
        { new ArgumentException("The export cannot be saved over the vault file itself.", "FAKE-PATH"), "The export cannot be saved there. Choose another name or folder; the vault file itself is never written over." },
        { new UnauthorizedAccessException("Access to the path '/home/fake/FAKE-PATH/export.csv' is denied."), "BitosX does not have permission to write to that location. Nothing was exported." },
        { new IOException("There is not enough space on the disk: '/home/fake/FAKE-PATH'."), "Could not write the export file. Nothing was exported." },
        { new OperationCanceledException("FAKE-PATH"), "The vault was locked before the export was written. Nothing was exported." },
        { new InvalidOperationException("FAKE-PATH"), "The vault is locked. Nothing was exported." },
    };

    [Theory]
    [MemberData(nameof(ExportFailures))]
    public async Task WhatGoesWrongWhileExporting_IsSaidInFixedWords_AndThePasswordBoxIsEmptied(Exception failure, string expected)
    {
        using Card card = await NewCardAsync();
        card.Files.ExportPath = Path.Combine(_directory, "export.csv");
        card.OpenExport(MasterPassword);
        card.Export.Throws = failure;

        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal(expected, card.Vm.ExportStatus);
        Assert.DoesNotContain("FAKE-PATH", card.Vm.ExportStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.False(card.Vm.IsExporting);
        Assert.True(card.Vm.IsExportOpen);
        Assert.Equal(VaultState.Unlocked, card.Vault.State);
    }

    [Fact]
    public async Task TheVaultFileItself_IsNeverWrittenOver()
    {
        using Card card = await NewCardAsync();
        byte[] vaultBefore = File.ReadAllBytes(VaultPath);
        card.Files.ExportPath = VaultPath;
        card.OpenExport(MasterPassword);

        await card.Vm.WriteExportCommand.ExecuteAsync(null);

        Assert.Equal("The export cannot be saved there. Choose another name or folder; the vault file itself is never written over.", card.Vm.ExportStatus);
        Assert.Equal(vaultBefore, File.ReadAllBytes(VaultPath));
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
    }

    [Fact]
    public async Task AVaultWithoutItemsOfSeveralAddresses_HasNoNoteAboutThem()
    {
        using Card card = await NewCardAsync();

        card.Vm.OpenExportCommand.Execute(null);
        Assert.Null(card.Vm.ExportSeveralUrlsNote);

        // Two of them, added while the export is open on another page: the note follows when the page is shown again.
        await card.Vault.AddLoginAsync(new LoginItem { Name = "A", Urls = ["https://a.example.com/", "https://a.example.org/"] });
        await card.Vault.AddLoginAsync(new LoginItem { Name = "B", Urls = ["https://b.example.com/", "https://b.example.org/"] });
        card.Vm.Refresh();
        Assert.Equal(
            "2 items in this vault have more than one address. A CSV file holds only the first address of each item; only the JSON file keeps them all.",
            card.Vm.ExportSeveralUrlsNote);
    }

    [Fact]
    public async Task CancellingAnExport_ClosesIt_AndKeepsNothing()
    {
        using Card card = await NewCardAsync();
        card.Files.ExportPath = Path.Combine(_directory, "export.csv");
        card.OpenExport("not the master password");
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        Assert.NotNull(card.Vm.ExportStatus);
        card.Vm.ExportPassword = MasterPassword;

        card.Vm.CancelExportCommand.Execute(null);

        Assert.False(card.Vm.IsExportOpen);
        Assert.False(card.Vm.ExportAcknowledged);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.Null(card.Vm.ExportStatus);
        Assert.False(File.Exists(card.Files.ExportPath));

        // Opened again, it asks for everything again.
        card.Vm.OpenExportCommand.Execute(null);
        Assert.False(card.Vm.ExportAcknowledged);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));
    }

    // =====================================================================================
    // Locking
    // =====================================================================================

    [Fact]
    public async Task Locking_ClosesBoth_AndLetsGoOfEverythingTheyHold()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        card.Vm.ImportDuplicates = true;
        card.Vm.CopyExtrasToNotes = true;
        await card.Vm.PreviewRefresh;
        card.Vm.ToggleNotImportedListCommand.Execute(null);
        card.Files.ExportPath = Path.Combine(_directory, "export.csv");
        card.OpenExport("not the master password");
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        card.Vm.ExportPassword = MasterPassword;
        card.Vm.IsExportJson = true;
        Assert.True(card.Vm.HasImportPreview);
        Assert.NotNull(card.Vm.ExportStatus);

        card.Vault.Lock();

        // The import: no file, no preview, nothing said, the choices as they are at first.
        Assert.False(card.Vm.HasImportPreview);
        Assert.Null(card.Vm.Preview);
        Assert.Null(card.Vm.ImportFileName);
        Assert.Null(card.Vm.ImportStatus);
        Assert.Null(card.Vm.ImportReminder);
        Assert.False(card.Vm.ImportDuplicates);
        Assert.False(card.Vm.CopyExtrasToNotes);
        Assert.False(card.Vm.IsNotImportedListShown);
        Assert.False(card.Vm.IsPreviewCurrent);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));

        // The export: closed, nothing typed, nothing ticked, nothing said.
        Assert.False(card.Vm.IsExportOpen);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        Assert.False(card.Vm.ExportAcknowledged);
        Assert.True(card.Vm.IsExportCsv);
        Assert.Null(card.Vm.ExportStatus);
        Assert.Null(card.Vm.ExportSeveralUrlsNote);
        Assert.False(card.Vm.WriteExportCommand.CanExecute(null));

        // Nothing a binding could reach holds anything of the file, and nothing else does either.
        Assert.DoesNotContain(EverythingBindable(card.Vm), text => text.Contains("FAKE-pw", StringComparison.Ordinal));
        await AssertLetGoOfAsync(card.Import.LastFile);

        // Unlocked again, the same card starts from nothing: neither confirms what was there before.
        await card.Vault.UnlockAsync(MasterPassword);
        await card.Vm.ConfirmImportCommand.ExecuteAsync(null);
        await card.Vm.WriteExportCommand.ExecuteAsync(null);
        Assert.Equal(0, card.Import.Applies);
        Assert.Equal(1, card.Export.Exports); // the one with the wrong password, before the lock
        Assert.Single(card.Vault.Items);
        Assert.False(File.Exists(card.Files.ExportPath));
    }

    [Fact]
    public async Task LockingWhileTheImportDialogIsOpen_ReadsNothing_EvenAfterTheNextUnlock()
    {
        using Card card = await NewCardAsync();
        var dialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        card.Files.WhileOpen = dialog.Task;
        card.Files.ImportPath = Write("logins.csv", ThreeLogins);

        Task choosing = card.Vm.ChooseImportFileCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => card.Files.ImportAsked == 1, "the dialog is open");
        card.Vault.Lock();
        await card.Vault.UnlockAsync(MasterPassword);
        dialog.SetResult(); // the dialog is answered only now
        await choosing;

        Assert.Equal(0, card.Import.Reads);
        Assert.False(card.Vm.HasImportPreview);
        Assert.Null(card.Vm.ImportFileName);
        Assert.Null(card.Vm.ImportStatus);
    }

    [Fact]
    public async Task LockingWhileAFileIsBeingRead_DropsWhatWasRead()
    {
        using Card card = await NewCardAsync();
        card.Import.ReadHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        card.Files.ImportPath = Write("logins.csv", ThreeLogins);

        Task choosing = card.Vm.ChooseImportFileCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => card.Import.LastFile is not null, "the file is read");
        Assert.True(card.Vm.IsReadingImport);
        card.Vault.Lock();
        Assert.False(card.Vm.IsReadingImport);
        await card.Vault.UnlockAsync(MasterPassword);
        card.Import.ReadHeld.SetResult();
        await choosing;

        Assert.Equal(0, card.Import.Previews);
        Assert.False(card.Vm.HasImportPreview);
        Assert.Null(card.Vm.ImportFileName);
        Assert.Null(card.Vm.ImportStatus);
        Assert.False(card.Vm.IsImportBusy);
        await AssertLetGoOfAsync(card.Import.LastFile);
    }

    [Fact]
    public async Task LockingWhileAPreviewIsOnItsWay_DropsIt()
    {
        using Card card = await NewCardAsync();
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        card.Import.PreviewsHeld = [];
        card.Vm.ImportDuplicates = true;
        await WaitUntilAsync(() => card.Import.PreviewsHeld.Count == 1, "the preview is asked for");

        card.Vault.Lock();
        await card.Vault.UnlockAsync(MasterPassword);
        card.Import.PreviewsHeld[0].SetResult();
        await card.Vm.PreviewRefresh;

        Assert.False(card.Vm.HasImportPreview);
        Assert.False(card.Vm.ConfirmImportCommand.CanExecute(null));
        await AssertLetGoOfAsync(card.Import.LastFile);
    }

    [Fact]
    public async Task LockingWhileTheSaveDialogIsOpen_ChecksNoPassword_AndWritesNothing_EvenAfterTheNextUnlock()
    {
        using Card card = await NewCardAsync();
        var dialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        card.Files.WhileOpen = dialog.Task;
        card.Files.ExportPath = Path.Combine(_directory, "export.csv");
        card.OpenExport(MasterPassword);

        Task writing = card.Vm.WriteExportCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => card.Files.ExportAsked == 1, "the dialog is open");
        card.Vault.Lock();
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
        await card.Vault.UnlockAsync(MasterPassword);
        dialog.SetResult(); // a place to save is chosen only now
        await writing;

        Assert.Equal(0, card.Export.Exports);
        Assert.False(File.Exists(card.Files.ExportPath));
        Assert.False(card.Vm.IsExportOpen);
        Assert.Null(card.Vm.ExportStatus);
    }

    [Fact]
    public async Task DisposingOfThePage_ClosesBoth_AndTheCardStopsListening()
    {
        Card card = await NewCardAsync();
        try
        {
            await card.ChooseAsync(Write("logins.csv", ThreeLogins));
            card.OpenExport(MasterPassword);

            card.Vm.Dispose();

            Assert.False(card.Vm.HasImportPreview);
            Assert.Null(card.Vm.ImportFileName);
            Assert.False(card.Vm.IsExportOpen);
            Assert.Equal(string.Empty, card.Vm.ExportPassword);
            await AssertLetGoOfAsync(card.Import.LastFile);

            // Nothing can be started on it any more.
            card.Files.ExportPath = Path.Combine(_directory, "export.csv");
            await card.Vm.ChooseImportFileCommand.ExecuteAsync(null);
            card.Vm.OpenExportCommand.Execute(null);
            Assert.Equal(1, card.Files.ImportAsked);
            Assert.False(card.Vm.IsExportOpen);
            card.Vm.Refresh();
            card.Vault.Lock(); // and the vault locking is none of its business now
        }
        finally
        {
            card.Dispose();
        }
    }

    [Fact]
    public async Task TheSettingsPage_DisposesOfItsCard()
    {
        using Card card = await NewCardAsync();
        var controller = new AutoLockController(
            card.Vault, BitosX.Core.Settings.AppSettings.Default, TimeProvider.System, NullLogger<AutoLockController>.Instance);
        var page = new SettingsPageViewModel(
            card.Vault,
            new FileLocalVaultStorage(VaultPath),
            new FakeClipboardService(),
            new BitosX.ApiClient.LocalOnlyApiClient(),
            Argon2Parameters.Default,
            new FakeAppSettingsStore(),
            new BitosX.App.Services.AutoLockService(controller, new BitosX.Platform.Session.NullSystemSessionEvents()),
            new BitosX.Platform.Session.NullSystemSessionEvents(),
            new FakeVaultBackupService(),
            card.Files,
            new FakeKeyDerivationBenchmark(),
            card.Vm);
        Assert.Same(card.Vm, page.ImportExport);
        await card.ChooseAsync(Write("logins.csv", ThreeLogins));
        card.OpenExport(MasterPassword);

        page.Dispose();

        Assert.False(card.Vm.HasImportPreview);
        Assert.False(card.Vm.IsExportOpen);
        Assert.Equal(string.Empty, card.Vm.ExportPassword);
    }
}
