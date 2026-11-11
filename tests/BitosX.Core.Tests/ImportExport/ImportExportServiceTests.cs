using System.Runtime.InteropServices;
using System.Text;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Services;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests.ImportExport;

/// <summary>
/// The two services an import and an export go through, on a real vault service: who may ask, what
/// is written and when, and what is left behind in memory and in the log.
/// </summary>
public sealed class ImportExportServiceTests
{
    private const string MasterPassword = "correct horse battery";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 30, 45, TimeSpan.Zero);

    /// <summary>The files of an import and an export, in memory.</summary>
    private sealed class FakeFiles : IImportExportFiles
    {
        public Dictionary<string, byte[]> Sources { get; } = new(StringComparer.Ordinal);

        /// <summary>The very array each read handed out, to see what became of it.</summary>
        public List<byte[]> HandedOut { get; } = [];

        public List<(string Path, byte[] Content)> Written { get; } = [];

        /// <summary>The memory each write was given, to see what became of it afterwards.</summary>
        public List<ReadOnlyMemory<byte>> GivenToWrite { get; } = [];

        public Exception? WriteThrows { get; set; }

        public Task<byte[]> ReadImportFileAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            byte[] copy = [.. Sources[sourcePath]];
            HandedOut.Add(copy);
            return Task.FromResult(copy);
        }

        public Task WriteExportFileAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteThrows is not null)
            {
                return Task.FromException(WriteThrows);
            }

            GivenToWrite.Add(content);
            Written.Add((destinationPath, content.ToArray()));
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock that stands still, and can do something when it is read.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            OnRead?.Invoke();
            return now;
        }
    }

    /// <summary>Keeps every line that is logged, to check that none of them holds anything from a file.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add($"{logLevel} {eventId.Name}: {formatter(state, exception)} {exception}");
            }
        }
    }

    private sealed class Setup
    {
        public required VaultService Vault { get; init; }

        public required InstrumentedVaultStorage Storage { get; init; }

        public required FakeFiles Files { get; init; }

        public required FixedTime Time { get; init; }

        public required VaultImportService Import { get; init; }

        public required VaultExportService Export { get; init; }

        public required RecordingLogger<VaultImportService> ImportLog { get; init; }

        public required RecordingLogger<VaultExportService> ExportLog { get; init; }

        public required RecordingLogger<VaultService> VaultLog { get; init; }

        public IEnumerable<string> AllLogLines => [.. ImportLog.Lines, .. ExportLog.Lines, .. VaultLog.Lines];
    }

    private static async Task<Setup> UnlockedAsync(params LoginItem[] existing)
    {
        var storage = new InstrumentedVaultStorage();
        var vaultLog = new RecordingLogger<VaultService>();
        var vault = new VaultService(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, vaultLog);
        await vault.CreateVaultAsync(MasterPassword);
        if (existing.Length > 0)
        {
            await vault.AddLoginsAsync(_ => existing);
        }

        var files = new FakeFiles();
        var time = new FixedTime(Now);
        var importLog = new RecordingLogger<VaultImportService>();
        var exportLog = new RecordingLogger<VaultExportService>();
        return new Setup
        {
            Vault = vault,
            Storage = storage,
            Files = files,
            Time = time,
            Import = new VaultImportService(vault, files, time, importLog),
            Export = new VaultExportService(vault, files, time, exportLog),
            ImportLog = importLog,
            ExportLog = exportLog,
            VaultLog = vaultLog,
        };
    }

    private static LoginItem Login(string name, string? username = "u", string? password = "FAKE", params string[] urls)
        => new() { Name = name, Username = username, Password = password, Urls = urls };

    private const string ThreeRows =
        "name,url,username,password,note\n" +
        "One,https://one.example,fake.one,FAKE-secret-one,note one\n" +
        "Two,two.example,fake.two,FAKE-secret-two,\n" +
        "Three,,fake.three,FAKE-secret-three,\n";

    private static async Task<IReadOnlyList<VaultItem>> ItemsAfterReopenAsync(InstrumentedVaultStorage storage)
    {
        var reopen = new VaultService(VaultCryptor.CreateDefault(), storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await reopen.UnlockAsync(MasterPassword);
        return reopen.Items;
    }

    // ---- Import ----

    [Fact]
    public async Task AnImport_IsRead_Previewed_AndThenAppliedInOneWrite()
    {
        Setup s = await UnlockedAsync(Login("Already there"));
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        int writesBefore = s.Storage.WriteCount;

        ParsedImport file = await s.Import.ReadAsync("passwords.csv");
        ImportPlan plan = await s.Import.PreviewAsync(file, ImportOptions.Default);

        // Nothing has been written yet: a preview is only that.
        Assert.Equal(3, plan.ToAdd);
        Assert.Equal(writesBefore, s.Storage.WriteCount);
        Assert.Single(s.Vault.Items);

        ImportResult result = await s.Import.ApplyAsync(plan);

        Assert.Equal(3, result.Added);
        Assert.Equal(3, result.Applied.ToAdd);
        Assert.Equal(writesBefore + 1, s.Storage.WriteCount); // one write for all three
        Assert.Equal(["Already there", "One", "Two", "Three"], s.Vault.Items.Select(i => i.Name));

        IReadOnlyList<VaultItem> stored = await ItemsAfterReopenAsync(s.Storage);
        Assert.Equal(4, stored.Count);
        var two = (LoginItem)stored[2];
        Assert.Equal("fake.two", two.Username);
        Assert.Equal("FAKE-secret-two", two.Password);
        Assert.Equal(["https://two.example"], two.Urls);
        Assert.Null(two.Notes);
    }

    [Fact]
    public async Task ImportedItems_AreNew_AndCarryTheTimeOfTheImport()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);

        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), ImportOptions.Default);
        await s.Import.ApplyAsync(plan);

        Assert.All(s.Vault.Items, item =>
        {
            Assert.Equal(Now, item.CreatedAt);
            Assert.Equal(Now, item.UpdatedAt);
        });
        Assert.Equal(3, s.Vault.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task WhenTheWriteFails_NothingWasImported()
    {
        Setup s = await UnlockedAsync(Login("Already there"));
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), ImportOptions.Default);
        EncryptedVault before = s.Storage.Stored!;
        s.Storage.FailNextWrite = true;

        await Assert.ThrowsAsync<IOException>(() => s.Import.ApplyAsync(plan));

        Assert.Same(before, s.Storage.Stored);
        Assert.Equal(["Already there"], s.Vault.Items.Select(i => i.Name));
        Assert.Single(await ItemsAfterReopenAsync(s.Storage));

        // And the same plan can be applied once the disk is back.
        Assert.Equal(3, (await s.Import.ApplyAsync(plan)).Added);
    }

    [Fact]
    public async Task EveryStep_IsRefusedWhileTheVaultIsLocked()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ParsedImport file = await s.Import.ReadAsync("passwords.csv");
        ImportPlan plan = await s.Import.PreviewAsync(file, ImportOptions.Default);
        int handedOut = s.Files.HandedOut.Count;
        int writesBefore = s.Storage.WriteCount;
        s.Vault.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Import.ReadAsync("passwords.csv"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Import.PreviewAsync(file, ImportOptions.Default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Import.ApplyAsync(plan));

        Assert.Equal(handedOut, s.Files.HandedOut.Count); // the file was not even opened
        Assert.Equal(writesBefore, s.Storage.WriteCount);
    }

    [Fact]
    public async Task WhatIsAdded_IsDecidedOnTheVaultAsItIsWhenItIsWritten_NotAsItWasWhenPreviewed()
    {
        Setup s = await UnlockedAsync(Login("Kept"));
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ImportPlan previewed = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), ImportOptions.Default);
        Assert.Equal(3, previewed.ToAdd);

        // Between the preview and the confirmation, one of the three is added by hand.
        await s.Vault.AddLoginAsync(Login("Two", "fake.two", "FAKE-secret-two", "https://two.example"));

        ImportResult result = await s.Import.ApplyAsync(previewed);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Applied.Duplicates);
        Assert.Equal(2, result.Applied.VaultItemsBefore); // the one that was there, and the one added by hand
        Assert.Equal(1, previewed.VaultItemsBefore);
        Assert.Equal(["Kept", "Two", "One", "Three"], s.Vault.Items.Select(i => i.Name));
        Assert.Single(s.Vault.Items, i => i.Name == "Two"); // not a second time
    }

    [Fact]
    public async Task WhenEverythingIsAlreadyThere_NothingIsWritten()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ParsedImport file = await s.Import.ReadAsync("passwords.csv");
        await s.Import.ApplyAsync(await s.Import.PreviewAsync(file, ImportOptions.Default));
        EncryptedVault before = s.Storage.Stored!;
        int writesBefore = s.Storage.WriteCount;

        ImportPlan again = await s.Import.PreviewAsync(file, ImportOptions.Default);
        ImportResult result = await s.Import.ApplyAsync(again);

        Assert.Equal(3, again.Duplicates);
        Assert.Equal(0, result.Added);
        Assert.Equal(writesBefore, s.Storage.WriteCount);
        Assert.Same(before, s.Storage.Stored);
    }

    [Fact]
    public async Task TheChoices_AreCarriedOut()
    {
        Setup s = await UnlockedAsync(Login("One", "fake.one", "FAKE-secret-one", "https://one.example"));
        s.Files.Sources["bitwarden.csv"] = Encoding.UTF8.GetBytes(
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            ",,login,One,,,0,https://one.example,fake.one,FAKE-secret-one,\n" +
            "Work,1,login,Two,own,PIN: 0000,0,,fake.two,FAKE-secret-two,FAKEFAKE\n");
        ParsedImport file = await s.Import.ReadAsync("bitwarden.csv");

        ImportPlan plan = await s.Import.PreviewAsync(file, new ImportOptions(CopyExtrasToNotes: true, ImportDuplicates: true));
        ImportResult result = await s.Import.ApplyAsync(plan);

        Assert.Equal(2, result.Added);
        Assert.Equal(["One", "One", "Two"], s.Vault.Items.Select(i => i.Name));
        var two = (LoginItem)s.Vault.Items[2];
        Assert.Equal("own\n\nAuthenticator key (TOTP): FAKEFAKE\nFolder: Work\nPIN: 0000", two.Notes);
        Assert.True(two.Favorite);
    }

    [Fact]
    public async Task AnImportThatWouldPassTheLimitOfAVault_IsRefusedAsAWhole()
    {
        Setup s = await UnlockedAsync([.. Enumerable.Range(0, 9_998).Select(i => Login("Existing " + i))]);
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), ImportOptions.Default);
        EncryptedVault before = s.Storage.Stored!;

        Assert.True(plan.ExceedsVaultItemLimit); // the preview already says so
        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => s.Import.ApplyAsync(plan));

        Assert.Equal(ImportRefusal.VaultItemLimit, refused.Kind);
        Assert.Same(before, s.Storage.Stored);       // not two of the three: none
        Assert.Equal(9_998, s.Vault.Items.Count);

        // Two more are exactly ten thousand, and are imported.
        s.Files.Sources["two.csv"] = Encoding.UTF8.GetBytes("name,password\nNew 1,FAKE\nNew 2,FAKE\n");
        ImportPlan two = await s.Import.PreviewAsync(await s.Import.ReadAsync("two.csv"), ImportOptions.Default);
        Assert.False(two.ExceedsVaultItemLimit);
        Assert.Equal(2, (await s.Import.ApplyAsync(two)).Added);
        Assert.Equal(10_000, s.Vault.Items.Count);
    }

    [Fact]
    public async Task TheLimit_IsCheckedOnTheVaultAsItIsWhenItIsWritten()
    {
        Setup s = await UnlockedAsync([.. Enumerable.Range(0, 9_997).Select(i => Login("Existing " + i))]);
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), ImportOptions.Default);
        Assert.False(plan.ExceedsVaultItemLimit); // 9,997 and three

        await s.Vault.AddLoginAsync(Login("Added meanwhile"));

        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => s.Import.ApplyAsync(plan));
        Assert.Equal(ImportRefusal.VaultItemLimit, refused.Kind);
        Assert.Equal(9_998, s.Vault.Items.Count);
    }

    [Fact]
    public async Task AFileThatIsRefused_IsRefusedWithItsKind_AndNothingChanges()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["encrypted.json"] = Encoding.UTF8.GetBytes("{\"encrypted\": true, \"data\": \"FAKE-secret-marker\"}");
        int writesBefore = s.Storage.WriteCount;

        var refused = await Assert.ThrowsAsync<ImportRefusedException>(() => s.Import.ReadAsync("encrypted.json"));

        Assert.Equal(ImportRefusal.EncryptedExport, refused.Kind);
        Assert.Equal(writesBefore, s.Storage.WriteCount);
        Assert.Contains(s.ImportLog.Lines, line => line.Contains("encrypted_export", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBytesOfTheFile_AreWipedOnceTheyAreParsed_AlsoWhenTheFileIsRefused()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        s.Files.Sources["damaged.csv"] = Encoding.UTF8.GetBytes("name,password\nOne,\"FAKE-secret-never-closed\n");

        await s.Import.ReadAsync("passwords.csv");
        await Assert.ThrowsAsync<ImportRefusedException>(() => s.Import.ReadAsync("damaged.csv"));

        Assert.Equal(2, s.Files.HandedOut.Count);
        Assert.All(s.Files.HandedOut, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task NothingFromAFile_IsEverLogged()
    {
        Setup s = await UnlockedAsync();
        s.Files.Sources["passwords.csv"] = Encoding.UTF8.GetBytes(ThreeRows);
        s.Files.Sources["damaged.csv"] = Encoding.UTF8.GetBytes("name,password\nOne,\"FAKE-secret-never-closed\n");

        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("passwords.csv"), new ImportOptions(CopyExtrasToNotes: true));
        await s.Import.ApplyAsync(plan);
        await Assert.ThrowsAsync<ImportRefusedException>(() => s.Import.ReadAsync("damaged.csv"));
        await s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword);
        await Assert.ThrowsAsync<VaultAuthenticationException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, "FAKE-wrong-master-pw"));

        string log = string.Join('\n', s.AllLogLines);
        foreach (string secret in new[] { "FAKE-", "fake.", "note one", "one.example", "passwords.csv", "out.csv", MasterPassword })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }

        // What it does say: that items were added and how many, and that an unencrypted export was written.
        Assert.Contains(s.ImportLog.Lines, line => line.Contains("import_applied", StringComparison.Ordinal) && line.Contains('3', StringComparison.Ordinal));
        Assert.Contains(s.ImportLog.Lines, line => line.Contains("import_refused", StringComparison.Ordinal) && line.Contains("invalid_csv", StringComparison.Ordinal));
        Assert.Contains(s.ExportLog.Lines, line => line.StartsWith("Warning unencrypted_export_written", StringComparison.Ordinal));
        Assert.Contains(s.ExportLog.Lines, line => line.Contains("unencrypted_export_failed", StringComparison.Ordinal));
    }

    // ---- Export ----

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.BitosXJson)]
    public async Task AnExport_NeedsTheMasterPassword_AndWritesWhatTheExporterGives(ExportFormat format)
    {
        Setup s = await UnlockedAsync(
            Login("One", "fake.one", "FAKE-secret-one", "https://one.example", "https://one.example/other"),
            Login("Two", "fake.two", "FAKE-secret-two"));
        int writesBefore = s.Storage.WriteCount;

        ExportResult result = await s.Export.ExportAsync("out.file", format, MasterPassword);

        Assert.Equal(format, result.Format);
        Assert.Equal(2, result.ItemCount);
        Assert.Equal(1, result.ItemsWithSeveralUrls);
        (string path, byte[] content) = Assert.Single(s.Files.Written);
        Assert.Equal("out.file", path);
        Assert.Equal(VaultExporter.Export(s.Vault.Items, format, Now), content);
        Assert.Equal(writesBefore, s.Storage.WriteCount); // the vault itself is not written
    }

    [Theory]
    [InlineData("correct horse batterx")]
    [InlineData("")]
    [InlineData(" correct horse battery")]
    public async Task WithAWrongPassword_NothingIsWritten(string wrong)
    {
        Setup s = await UnlockedAsync(Login("One"));

        await Assert.ThrowsAsync<VaultAuthenticationException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, wrong));

        Assert.Empty(s.Files.Written);
        Assert.Equal(VaultState.Unlocked, s.Vault.State);
    }

    [Fact]
    public async Task WhileLocked_AnExportIsRefused_EvenWithTheRightPassword()
    {
        Setup s = await UnlockedAsync(Login("One"));
        s.Vault.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword));

        Assert.Empty(s.Files.Written);
    }

    [Fact]
    public async Task ALockAfterThePasswordWasChecked_StillStopsTheExport()
    {
        Setup s = await UnlockedAsync(Login("One"));
        s.Time.OnRead = s.Vault.Lock; // the clock is read after the password is verified, before the file is written

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword));

        Assert.Empty(s.Files.Written);
    }

    [Fact]
    public async Task ALockWhileThePasswordIsChecked_StopsTheExport()
    {
        Setup s = await UnlockedAsync(Login("One"));
        s.Storage.OnRead = s.Vault.Lock;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword));

        Assert.Empty(s.Files.Written);
    }

    [Fact]
    public async Task WhatIsExported_IsWhatThePasswordWasVerifiedFor()
    {
        // A vault changed by another instance: the password would open the file, but the file is
        // not what this session shows. Nothing is written.
        Setup s = await UnlockedAsync(Login("One"));
        var other = new VaultService(VaultCryptor.CreateDefault(), s.Storage, Argon2Parameters.ForTesting, NullLogger<VaultService>.Instance);
        await other.UnlockAsync(MasterPassword);
        await other.AddLoginAsync(Login("Added elsewhere"));

        await Assert.ThrowsAsync<VaultChangedExternallyException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword));

        Assert.Empty(s.Files.Written);
    }

    [Fact]
    public async Task TheBytesOfTheExport_AreWipedOnceTheyAreWritten_AlsoWhenWritingFails()
    {
        Setup s = await UnlockedAsync(Login("One", "fake.one", "FAKE-secret-one"));

        await s.Export.ExportAsync("out.json", ExportFormat.BitosXJson, MasterPassword);

        ReadOnlyMemory<byte> given = Assert.Single(s.Files.GivenToWrite);
        Assert.True(MemoryMarshal.TryGetArray(given, out ArraySegment<byte> array));
        Assert.All(array.Array!, b => Assert.Equal(0, b));
        Assert.Contains("FAKE-secret-one", Encoding.UTF8.GetString(s.Files.Written[0].Content), StringComparison.Ordinal); // it was written first

        s.Files.WriteThrows = new IOException("disk full");
        await Assert.ThrowsAsync<IOException>(() => s.Export.ExportAsync("out.json", ExportFormat.BitosXJson, MasterPassword));
    }

    [Fact]
    public async Task ACancelledExport_WritesNothing()
    {
        Setup s = await UnlockedAsync(Login("One"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.Export.ExportAsync("out.csv", ExportFormat.Csv, MasterPassword, cancelled.Token));

        Assert.Empty(s.Files.Written);
    }

    [Fact]
    public async Task BadArguments_AreErrors_BeforeAnythingIsDone()
    {
        Setup s = await UnlockedAsync(Login("One"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Export.ExportAsync("out.csv", (ExportFormat)9, MasterPassword));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Export.ExportAsync(" ", ExportFormat.Csv, MasterPassword));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.Export.ExportAsync("out.csv", ExportFormat.Csv, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Import.ReadAsync(""));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.Import.PreviewAsync(null!, ImportOptions.Default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.Import.ApplyAsync(null!));

        Assert.Empty(s.Files.Written);
        Assert.Empty(s.Files.HandedOut);
    }

    // ---- Both together ----

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.BitosXJson)]
    public async Task ImportingYourOwnExportIntoTheSameVault_AddsNothing(ExportFormat format)
    {
        Setup s = await UnlockedAsync(
            Login("One", "fake.one", "FAKE-secret-one", "https://one.example"),
            Login("Two \"quoted\", with a comma", "  fake two ", " FAKE,\"two\" ", "https://two.example/?a=1,2"),
            Login("Three", null, null),
            Login("Four", "fake.four", "FAKE-four") with { Notes = "line one\r\nline two", Favorite = true });
        await s.Export.ExportAsync("export.file", format, MasterPassword);
        s.Files.Sources["export.file"] = s.Files.Written[0].Content;
        int writesBefore = s.Storage.WriteCount;

        ImportPlan plan = await s.Import.PreviewAsync(await s.Import.ReadAsync("export.file"), ImportOptions.Default);
        ImportResult result = await s.Import.ApplyAsync(plan);

        Assert.Equal(4, plan.Duplicates);
        Assert.Equal(0, plan.ToAdd);
        Assert.Empty(plan.NotImported);
        Assert.Equal(0, result.Added);
        Assert.Equal(4, s.Vault.Items.Count);
        Assert.Equal(writesBefore, s.Storage.WriteCount);
    }
}
