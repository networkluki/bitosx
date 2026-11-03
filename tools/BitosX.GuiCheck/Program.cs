using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using BitosX.ClipboardProbe;
using BitosX.Crypto;

namespace BitosX.GuiCheck;

/// <summary>
/// Carries out the manual checklists of docs/VERIFICATION.md on the real application: the 17
/// steps, and the A-, H- and R-series. The application is started as a process of its own, in a
/// real window, on a data directory of its own, and operated through the accessibility interface
/// of Windows. What is on disk is compared byte for byte; the clipboard is read the way any other
/// application reads it.
/// </summary>
/// <remarks>
/// <para>
/// Usage: <c>dotnet run -c Release --project tools/BitosX.GuiCheck</c>, after the application has
/// been built in Release. <c>--app &lt;path&gt;</c> names another executable; <c>--quick</c> leaves
/// out what takes minutes (the clipboard timeout and the inactivity lock); <c>--no-clipboard</c>
/// leaves the clipboard alone.
/// </para>
/// <para>
/// While it runs (about six minutes) a BitosX window is on screen and the clipboard is in use. The
/// tool only ever places texts of its own ("BITOSX-TEST-…") there, prints nothing else it finds,
/// and puts the text that was on the clipboard back. It never opens the vault at the default
/// location, and checks at the end that it is byte for byte what it was.
/// </para>
/// <para>
/// Not a person: nobody looked at the window. What is checked is what the application shows and
/// does, in a real window, with the real clipboard and the real file dialogs. The workstation is
/// not locked and the system not put to sleep: the application is sent the messages Windows
/// sends for them.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Prefix = "BITOSX-TEST-";
    private const string MasterPassword = Prefix + "correct horse battery staple";
    private const string NewMasterPassword = Prefix + "a new and different master password";
    private const string WrongPasswordMessage = "Incorrect master password, or the vault file has been modified.";

    private const string Name = Prefix + "Example Bank";
    private const string Username = Prefix + "alice@example.com";
    private const string Password = Prefix + "S3cret-Pass-Phrase!";
    private const string Url = "https://bitosx-test-bank.example.com";
    private const string Notes = Prefix + "periwinkle";
    private const string Other = Prefix + "Alpha";
    private const string UserContent = Prefix + "USER-CONTENT";

    private const int Enter = 0x0D;
    private const int Escape = 0x1B;
    private const int Space = 0x20;

    private static readonly string[] WindowsMarkers =
    [
        "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "Clipboard Viewer Ignore",
    ];

    private static readonly Report Report = new();
    private static string _executable = string.Empty;
    private static string _directory = string.Empty;
    private static bool _quick;
    private static bool _useClipboard = true;

    private static string VaultPath => Path.Combine(_directory, "vault.bitosx");

    public static int Main(string[] args)
    {
        _executable = Path.GetFullPath(@"src\BitosX.App\bin\Release\net10.0\BitosX.App.exe");
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app" when i + 1 < args.Length:
                    _executable = Path.GetFullPath(args[++i]);
                    break;
                case "--quick":
                    _quick = true;
                    break;
                case "--no-clipboard":
                    _useClipboard = false;
                    break;
                default:
                    Console.Error.WriteLine("Usage: BitosX.GuiCheck [--app <path to BitosX.App.exe>] [--quick] [--no-clipboard]");
                    return 2;
            }
        }

        Console.WriteLine("BitosX checklist on the real application (diagnostic only)");
        Console.WriteLine("----------------------------------------------------------");
        if (!File.Exists(_executable))
        {
            Console.Error.WriteLine("The application was not found at " + _executable + ". Build it first: dotnet build -c Release");
            return 2;
        }

        _directory = Path.Combine(Path.GetTempPath(), "BitosX.GuiCheck", Guid.NewGuid().ToString("N"));
        Console.WriteLine("Application    : " + _executable);
        Console.WriteLine("Data directory : " + _directory);

        // The vault at the default location is never opened. That it was not touched is checked.
        string realVault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BitosX", "vault.bitosx");
        string realSettings = Path.Combine(Path.GetDirectoryName(realVault)!, "settings.json");
        string? vaultBefore = Fingerprint(realVault);
        string? settingsBefore = Fingerprint(realSettings);

        string? clipboardBefore = null;
        if (_useClipboard)
        {
            clipboardBefore = NativeClipboard.Text();
            if (clipboardBefore is null && NativeClipboard.FormatNames().Count > 0)
            {
                // Something that is not text, which could not be put back.
                Console.WriteLine("The clipboard holds something that is not text; it is left alone.");
                _useClipboard = false;
            }
        }

        try
        {
            Report.Run("Starting", Starting);
            Report.Run("The 17 steps", TheSeventeenSteps);
            Report.Run("What the password fields tell other programs", PasswordFields);
            Report.Run("A-series: automatic locking", AutomaticLocking);
            Report.Run("Changing the master password", ChangingTheMasterPassword);
            Report.Run("R-series: rotating the vault key", RotatingTheVaultKey);
            Report.Run("Strengthening the key derivation", StrengtheningTheKeyDerivation);
            Report.Run("H-series: the confirmation before a restore", RestoreConfirmation);
            Report.Run("A device without the memory for a new vault", DeviceWithoutTheMemory);
        }
        finally
        {
            if (_useClipboard)
            {
                if (clipboardBefore is not null)
                {
                    NativeClipboard.SetTextOutsideHistory(clipboardBefore);
                }
                else
                {
                    NativeClipboard.Clear();
                }
            }

            Report.Section("Afterwards");
            Report.Check(
                "The vault at the default location is byte for byte what it was",
                Fingerprint(realVault) == vaultBefore && Fingerprint(realSettings) == settingsBefore,
                vaultBefore is null ? "there is none" : string.Empty);
            Report.Check("No BitosX process of this run is left", Process.GetProcessesByName("BitosX.App").All(p => !IsOurs(p)));
            TryDelete(_directory);
        }

        Report.Summarize();
        return Report.Failed == 0 ? 0 : 1;
    }

    // ---- Starting ----

    private static void Starting()
    {
        foreach (string[] arguments in new[]
                 {
                     new[] { "--data-directory" },
                     ["--data-directory", @"relative\path"],
                     ["--data-dir", _directory],
                     ["--data-directory", _directory, "--data-directory", _directory],
                 })
        {
            int code = RunningApp.Run(_executable, arguments, TimeSpan.FromSeconds(20), out string errors);
            Report.Check(
                $"An option that cannot be used ends the application with code 64: {string.Join(' ', arguments.Select(Shorten))}",
                code == 64 && errors.Length > 0 && !Directory.Exists(_directory),
                $"code {code}; \"{errors}\"");
        }

        using RunningApp app = RunningApp.Start(_executable, _directory);
        Report.Check("The window title names the data directory", app.Title == "BitosX — " + _directory, app.Title);
        Report.Check("1. First launch, no vault: the Create Vault screen", app.Screen() == "create" && !File.Exists(VaultPath));
        Report.Check("Starting wrote nothing", !Directory.Exists(_directory) || !Directory.EnumerateFileSystemEntries(_directory).Any());

        int second = RunningApp.Run(_executable, ["--data-directory", _directory], TimeSpan.FromSeconds(20), out _);
        Report.Check("A second instance on the same data directory ends by itself, with code 2", second == 2, "code " + second);

        string elsewhere = _directory + "-other";
        using (RunningApp other = RunningApp.Start(_executable, elsewhere))
        {
            Report.Check("An instance on another data directory starts next to it", other.Screen() == "create");
            Report.Check("and closes normally", other.Close() == 0);
        }

        TryDelete(elsewhere);
        Report.Check("The window closes normally", app.Close() == 0);
    }

    // ---- The 17 steps ----

    private static void TheSeventeenSteps()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);

            // 2. Create vault.
            app.Type("MasterPasswordBox", MasterPassword);
            app.Type("ConfirmPasswordBox", MasterPassword);
            Report.Check("The password is rated while it is typed", app.TextOrNull("StrengthText")?.StartsWith("Strength: ", StringComparison.Ordinal) ?? false, app.TextOrNull("StrengthText") ?? "nothing shown");
            app.Press("CreateButton");
            app.WaitForScreen("main");
            Report.Check(
                "2. Create vault: the main screen with an empty vault, and a vault file",
                app.Shows("EmptyVaultNote") && app.FindAll("ItemName").Count == 0 && File.Exists(VaultPath));
            Report.Check("The master password is nowhere in the file", !FileContains(VaultPath, MasterPassword));

            // 3. Restart.
            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            Report.Check("3. Restart: the Unlock screen", app.Screen() == "unlock");

            // 4. Wrong master password.
            string before = Fingerprint(VaultPath)!;
            app.Type("MasterPasswordBox", Prefix + "not the master password");
            app.Press("UnlockButton");
            app.WaitForText("ErrorMessage", WrongPasswordMessage);
            Report.Check(
                "4. Wrong master password: one generic error, still locked, the file untouched",
                app.Text("ErrorMessage") == WrongPasswordMessage && app.Screen() == "unlock" && Fingerprint(VaultPath) == before,
                app.Text("ErrorMessage"));
            Report.Check("The failed attempt is not kept in the field", app.Value("MasterPasswordBox").Length == 0);

            // 5. Correct master password.
            Unlock(app, MasterPassword);
            Report.Check("5. Correct master password: unlocked", app.Screen() == "main");

            // 6. Add a login.
            before = Fingerprint(VaultPath)!;
            AddLogin(app, Name, Username, Password, Url, Notes);
            Report.Check(
                "6. Add a login: listed with its user name and address, and the file changes",
                ItemNames(app).SequenceEqual([Name]) && app.Text("ItemUsername") == Username && app.Text("ItemUrl") == Url
                && Fingerprint(VaultPath) != before);
            Report.Check(
                "Nothing of it is readable in the file",
                !new[] { MasterPassword, Name, Username, Password, "bitosx-test-bank", Notes }.Any(text => FileContains(VaultPath, text)));
            Report.Check("The password is shown as dots", !app.EverythingShown().Any(text => text.Contains(Password, StringComparison.Ordinal)));
            app.Press("ShowPasswordButton");
            app.WaitForText("ItemPassword", Password);
            Report.Check("until it is asked for", app.Text("ItemPassword") == Password);
            app.Press("ShowPasswordButton");
            app.WaitUntil(() => app.Text("ItemPassword") != Password, "the password is hidden again");

            // 7. Edit the login, and 8. generate a password.
            const string newUsername = Prefix + "alice.cooper@example.com";
            before = Fingerprint(VaultPath)!;
            app.Press("EditButton");
            app.WaitForText("EditorTitle", "Edit login");
            Report.Check(
                "The editor shows what was saved",
                app.Value("NameBox") == Name && app.Value("UsernameBox") == Username && app.Value("NotesBox") == Notes);
            Report.Check("with the password as dots", app.Value("PasswordBox") == new string('●', Password.Length), app.Value("PasswordBox").Length + " characters");
            Report.Check("The page behind the editor is out of reach", !app.IsEnabled("AddLoginButton"));
            app.Type("UsernameBox", newUsername);

            app.Press("GeneratePasswordButton");
            string first = app.Value("PasswordBox");
            app.Press("GeneratePasswordButton");
            string second = app.Value("PasswordBox");
            Report.Check(
                "8. Generate a password: random, shown, and different the next time",
                first.Length == 20 && second.Length == 20 && first != second && !first.Contains('●', StringComparison.Ordinal),
                $"{first.Length} and {second.Length} characters");

            app.Type("PasswordBox", Password); // the known one, for the steps below
            app.Press("SaveButton");
            app.WaitUntil(() => !app.Shows("NameBox"), "the editor closes");
            Report.Check(
                "7. Edit the login: the change is shown and saved",
                app.Text("ItemUsername") == newUsername && Fingerprint(VaultPath) != before);

            // 9, 10, 11. The clipboard.
            app = Clipboard(app, newUsername);

            // 12. Favourite.
            AddLogin(app, Other, Prefix + "first@example.com", Prefix + "another-password-1", null, null);
            Report.Check("Items are listed by name", ItemNames(app).SequenceEqual([Other, Name]), string.Join(", ", ItemNames(app)));
            app.Press("FavoriteButton", ItemNames(app).ToList().IndexOf(Name));
            app.WaitUntil(() => ItemNames(app) is [Name, ..], "the favourite is first");
            Report.Check(
                "12. Favourite: marked, and sorted to the top",
                app.Find("FavoriteButton", 0).Current.Name == $"Remove {Name} from favorites"
                && app.Find("FavoriteButton", 1).Current.Name == $"Add {Other} to favorites");

            // 13. Lock, and 14. the secrets are gone.
            app.Press("LockButton");
            app.WaitForScreen("unlock");
            Report.Check("13. Lock the vault: the Unlock screen", app.Screen() == "unlock");
            IReadOnlyList<string> shown = app.EverythingShown();
            string[] secrets = [Name, Other, newUsername, Password, Url, Notes, MasterPassword];
            Report.Check(
                "14. The secrets are gone: nothing of the vault is on screen, and no way in but the password",
                !secrets.Any(secret => shown.Any(text => text.Contains(secret, StringComparison.Ordinal)))
                && !app.Shows("VaultNavButton") && !app.Shows("SettingsNavButton") && !app.Shows("ItemName"));

            // 15. Unlock again.
            Unlock(app, MasterPassword);
            Report.Check(
                "15. Unlock again: everything is back, the favourite still first",
                ItemNames(app).SequenceEqual([Name, Other]) && app.Find("FavoriteButton", 0).Current.Name == $"Remove {Name} from favorites",
                string.Join(", ", ItemNames(app)));

            // 16. Delete.
            before = Fingerprint(VaultPath)!;
            app.Press("DeleteButton", 1);
            app.Find("ConfirmDeleteButton");
            Report.Check("Deleting is asked for first, and deletes nothing yet", Fingerprint(VaultPath) == before && ItemNames(app).Count == 2);
            app.Press("KeepButton");
            app.WaitUntil(() => !app.Shows("ConfirmDeleteButton"), "the question is withdrawn");
            Report.Check("and can be called off", Fingerprint(VaultPath) == before && ItemNames(app).Count == 2);
            app.Press("DeleteButton", 1);
            app.Press("ConfirmDeleteButton");
            app.WaitForText("StatusMessage", "Item deleted.");
            Report.Check("16. Delete an item: gone", ItemNames(app).SequenceEqual([Name]));

            // 17. Restart.
            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            Unlock(app, MasterPassword);
            app.Press("ShowPasswordButton");
            app.WaitForText("ItemPassword", Password);
            Report.Check(
                "17. Restart: what was added and edited is there, what was deleted is not",
                ItemNames(app).SequenceEqual([Name]) && app.Text("ItemUsername") == newUsername && app.Text("ItemPassword") == Password);
            Report.Check(
                "Nothing of the vault is readable in the file, and no temporary file is left",
                !new[] { MasterPassword, Name, Other, newUsername, Password, "bitosx-test-bank", Notes }.Any(text => FileContains(VaultPath, text))
                && !Directory.EnumerateFiles(_directory, "*.tmp").Any());
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    private static RunningApp Clipboard(RunningApp app, string username)
    {
        if (!_useClipboard)
        {
            Report.Skip("9-11. Copying to the clipboard, and its clearing", "the clipboard was to be left alone");
            return app;
        }

        app.Press("CopyUsernameButton");
        app.WaitForText("StatusMessage", "Username copied. Clipboard clears in 30s.");
        Report.Check("9. Copy the user name: confirmed with the countdown, and on the clipboard", NativeClipboard.Text() == username);

        app.Press("CopyPasswordButton");
        app.WaitForText("StatusMessage", "Password copied. Clipboard clears in 30s.");
        IReadOnlyList<string> formats = NativeClipboard.FormatNames();
        Report.Check("10. Copy the password: confirmed with the countdown, and on the clipboard", NativeClipboard.Text() == Password);
        Report.Check(
            "It carries the markers that keep it out of the clipboard history and the cloud clipboard",
            WindowsMarkers
                .All(marker => formats.Contains(marker, StringComparer.Ordinal)),
            string.Join(", ", formats));

        if (_quick)
        {
            Report.Skip("11. The clipboard is cleared after 30 seconds", "left out by --quick");
        }
        else
        {
            var watch = Stopwatch.StartNew();
            while (NativeClipboard.Text() == Password && watch.Elapsed < TimeSpan.FromSeconds(40))
            {
                Thread.Sleep(500);
            }

            Report.Check(
                "11. After its 30 seconds the password is gone from the clipboard",
                NativeClipboard.Text() != Password && watch.Elapsed > TimeSpan.FromSeconds(20),
                $"after {watch.Elapsed.TotalSeconds:0} s");

            app.Press("CopyPasswordButton");
            app.WaitUntil(() => NativeClipboard.Text() == Password, "the password is on the clipboard");
            NativeClipboard.SetTextOutsideHistory(UserContent); // the user copies something else
            Thread.Sleep(TimeSpan.FromSeconds(34));
            Report.Check("but what was copied by someone else afterwards is not cleared", NativeClipboard.Text() == UserContent);
        }

        // Closing the window shortly after copying.
        app.Press("CopyPasswordButton");
        app.WaitUntil(() => NativeClipboard.Text() == Password, "the password is on the clipboard"); // also: it was pasted once
        int code = app.Close();
        Report.Check("Closing the window clears a password that was just copied", NativeClipboard.Text() != Password && code == 0, "code " + code);
        app.Dispose();

        // The steps go on in a window opened after the one that was closed.
        RunningApp again = RunningApp.Start(_executable, _directory);
        Unlock(again, MasterPassword);
        return again;
    }

    // ---- The password fields ----

    private static void PasswordFields()
    {
        using RunningApp app = RunningApp.Start(_executable, _directory);
        RequireOwnWindow(app);
        app.Type("MasterPasswordBox", MasterPassword);
        Report.Check(
            "A typed master password is handed out as dots, not as text",
            app.Value("MasterPasswordBox") == new string('●', MasterPassword.Length),
            app.Value("MasterPasswordBox").Length + " characters");
        Report.Check(
            "and is nowhere in what the window tells other programs",
            !app.EverythingShown().Any(text => text.Contains(MasterPassword, StringComparison.Ordinal)));
        Report.Check("The field says what it is for", app.Find("MasterPasswordBox").Current.Name == "Master password", app.Find("MasterPasswordBox").Current.Name);

        app.Press("UnlockButton");
        app.WaitForScreen("main");
        app.Press("SettingsNavButton");
        app.Find("LockNowButton");
        string[] fields = ["CurrentPasswordBox", "NewPasswordBox", "ConfirmPasswordBox", "RotatePasswordBox", "StrengthenPasswordBox"];
        foreach (string field in fields)
        {
            app.Type(field, MasterPassword);
        }

        Report.Check(
            "The same holds for every password field in Settings",
            fields.All(field => app.Value(field) == new string('●', MasterPassword.Length))
            && !app.EverythingShown().Any(text => text.Contains(MasterPassword, StringComparison.Ordinal)));
        Report.Check("The window closes normally", app.Close() == 0);
    }

    // ---- A-series ----

    private static void AutomaticLocking()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);
            Unlock(app, MasterPassword);

            // A8. Lock now.
            app.Press("SettingsNavButton");
            app.Press("LockNowButton");
            app.WaitForScreen("unlock");
            Report.Check("A8. \"Lock now\" locks at once", app.Screen() == "unlock");

            // A4. Minimizing, with "lock when minimized" off.
            Unlock(app, MasterPassword);
            app.Minimize();
            Thread.Sleep(1500);
            app.Restore();
            Thread.Sleep(500);
            Report.Check("A4. Minimized and restored, the vault stays open", app.Screen() == "main");

            // A3. With it on.
            app.Press("SettingsNavButton");
            app.SetChecked("LockOnMinimizeCheckBox", true);
            app.Minimize();
            Thread.Sleep(1500);
            app.Restore();
            app.WaitForScreen("unlock");
            Report.Check("A3. With \"lock when minimized\" on, minimizing locks", app.Screen() == "unlock");

            // A7. The settings are kept.
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            ((RangeValuePattern)app.Find("InactivityMinutesBox").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(1);
            string settings = Path.Combine(_directory, "settings.json");
            app.WaitUntil(
                () => File.Exists(settings) && File.ReadAllText(settings).Contains("\"inactivityTimeoutSeconds\": 60", StringComparison.Ordinal),
                "the settings are saved");
            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            double minutes = ((RangeValuePattern)app.Find("InactivityMinutesBox").GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value;
            Report.Check(
                "A7. The settings are kept across a restart",
                Math.Abs(minutes - 1) < 0.001 && app.IsChecked("LockOnMinimizeCheckBox") && app.IsChecked("AutoLockCheckBox"),
                $"{minutes.ToString(CultureInfo.InvariantCulture)} minute(s)");
            app.SetChecked("LockOnMinimizeCheckBox", false);

            // A5 and A6. The user leaves.
            foreach (bool suspend in new[] { false, true })
            {
                string what = suspend ? "A6. The system goes to sleep" : "A5. The workstation is locked";
                if (app.Screen() != "main")
                {
                    Unlock(app, MasterPassword);
                }

                app.Press("VaultNavButton");
                if (_useClipboard)
                {
                    app.Press("CopyPasswordButton");
                    app.WaitUntil(() => NativeClipboard.Text() == Password, "the password is on the clipboard");
                }

                bool sent = suspend ? app.SignalSuspending() : app.SignalWorkstationLocked();
                app.WaitForScreen("unlock");
                Report.Check(what + ": the vault locks", sent && app.Screen() == "unlock", "message Windows sends for it; the system itself was left alone");
                if (_useClipboard)
                {
                    app.WaitUntil(() => NativeClipboard.Text() != Password, "the clipboard is cleared");
                    Report.Check("and a password that was just copied is cleared from the clipboard", NativeClipboard.Text() != Password);
                }
                else
                {
                    Report.Skip("and a password that was just copied is cleared from the clipboard", "the clipboard was to be left alone");
                }
            }

            // Locking the vault leaves a copied value for the rest of its time.
            if (_useClipboard)
            {
                Unlock(app, MasterPassword);
                app.Press("CopyPasswordButton");
                app.WaitUntil(() => NativeClipboard.Text() == Password, "the password is on the clipboard");
                app.Press("LockButton");
                app.WaitForScreen("unlock");
                Thread.Sleep(1000);
                Report.Check("Locking the vault by hand leaves a copied password for the rest of its time", NativeClipboard.Text() == Password);
                NativeClipboard.Clear();
            }

            // A1 and A2. Inactivity, with the timeout at one minute.
            if (_quick)
            {
                Report.Skip("A1, A2. Locking after a minute of inactivity", "left out by --quick");
            }
            else
            {
                Unlock(app, MasterPassword);
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(85))
                {
                    app.MoveMouse(200 + (int)(watch.Elapsed.TotalSeconds % 50), 200);
                    Thread.Sleep(3000);
                }

                Report.Check("A2. While the mouse is moved in the window, the vault stays open", app.Screen() == "main", $"for {watch.Elapsed.TotalSeconds:0} s, with the timeout at 60 s");

                // Somebody may be sitting at the machine. If they move the mouse over the window,
                // that is activity, the application is right to count it, and the minute starts
                // again. (A first version of this check mistook that for a vault that never locks.)
                var leftAlone = Stopwatch.StartNew();
                var waited = Stopwatch.StartNew();
                (int X, int Y)? last = app.MouseOfTheMachine();
                bool touched = false;
                while (app.Screen() != "unlock" && waited.Elapsed < TimeSpan.FromMinutes(8))
                {
                    (int X, int Y)? now = app.MouseOfTheMachine();
                    if (now is not null && now != last)
                    {
                        touched = true;
                        leftAlone.Restart();
                    }

                    last = now;
                    Thread.Sleep(250);
                }

                Report.Check(
                    "A1. Left alone, it locks after a minute",
                    app.Screen() == "unlock" && leftAlone.Elapsed > TimeSpan.FromSeconds(55) && leftAlone.Elapsed < TimeSpan.FromSeconds(85),
                    $"{leftAlone.Elapsed.TotalSeconds:0} s after it was last touched"
                    + (touched ? "; meanwhile the mouse of the machine was moved over the window, which is activity" : string.Empty));
            }

            // Back to the default, for the parts that follow.
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            ((RangeValuePattern)app.Find("InactivityMinutesBox").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(15);
            Thread.Sleep(500);
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    // ---- Changing the master password ----

    private static void ChangingTheMasterPassword()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            string before = Fingerprint(VaultPath)!;
            EncryptedVault old = Envelope();

            app.Press("ChangePasswordButton");
            app.WaitForText("ChangePasswordStatus", "Enter your current master password.");
            Report.Check("An empty form is refused in words, and the application goes on", Fingerprint(VaultPath) == before, app.Text("ChangePasswordStatus"));

            app.Type("CurrentPasswordBox", Prefix + "not the master password");
            app.Type("NewPasswordBox", NewMasterPassword);
            app.Type("ConfirmPasswordBox", NewMasterPassword);
            Report.Check("The new password is rated", app.TextOrNull("NewPasswordStrength")?.StartsWith("Strength: ", StringComparison.Ordinal) ?? false, app.TextOrNull("NewPasswordStrength") ?? "nothing shown");
            app.Press("ChangePasswordButton");
            app.WaitForText("ChangePasswordStatus", "The current password is incorrect.");
            Report.Check("A wrong current password changes nothing", Fingerprint(VaultPath) == before);

            app.Type("CurrentPasswordBox", MasterPassword);
            app.Type("ConfirmPasswordBox", NewMasterPassword + "x");
            app.Press("ChangePasswordButton");
            app.WaitForText("ChangePasswordStatus", "do not match");
            Report.Check("A confirmation that does not match changes nothing", Fingerprint(VaultPath) == before);

            app.Type("ConfirmPasswordBox", NewMasterPassword);
            app.Press("ChangePasswordButton");
            app.WaitForText("ChangePasswordStatus", "Master password changed.");
            EncryptedVault changed = Envelope();
            Report.Check(
                "The right one does: new salt and wrapped key, the content not re-encrypted, the form emptied",
                !changed.Salt.SequenceEqual(old.Salt) && !changed.EncryptedVaultKey.SequenceEqual(old.EncryptedVaultKey)
                && changed.Ciphertext.SequenceEqual(old.Ciphertext) && changed.ContentNonce.SequenceEqual(old.ContentNonce)
                && app.Value("CurrentPasswordBox").Length == 0 && app.Value("NewPasswordBox").Length == 0 && app.Value("ConfirmPasswordBox").Length == 0);

            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            app.Type("MasterPasswordBox", MasterPassword);
            app.Press("UnlockButton");
            app.WaitForText("ErrorMessage", WrongPasswordMessage);
            Unlock(app, NewMasterPassword);
            Report.Check("After a restart the old password is refused and the new one opens the vault", ItemNames(app).SequenceEqual([Name]));

            // And back, so that the parts that follow have one password to use.
            app.Press("SettingsNavButton");
            app.Type("CurrentPasswordBox", NewMasterPassword);
            app.Type("NewPasswordBox", MasterPassword);
            app.Type("ConfirmPasswordBox", MasterPassword);
            app.Press("ChangePasswordButton");
            app.WaitForText("ChangePasswordStatus", "Master password changed.");
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    // ---- R-series ----

    private static void RotatingTheVaultKey()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);
            Unlock(app, MasterPassword);
            AddLogin(app, Other, Prefix + "first@example.com", Prefix + "another-password-1", null, null);
            app.Press("SettingsNavButton");

            // A backup from before the rotation.
            string backup = Path.Combine(_directory, "before-rotation.bitosx-backup");
            app.Press("ExportBackupButton");
            FileDialog.Answer(app.ProcessId, backup, TimeSpan.FromSeconds(30));
            app.WaitForText("BackupStatus", "Encrypted backup exported.");
            Report.Check(
                "A backup is an exact copy of the vault file, and exporting leaves the vault alone",
                File.Exists(backup) && Fingerprint(backup) == Fingerprint(VaultPath));

            string before = Fingerprint(VaultPath)!;
            EncryptedVault old = Envelope();

            app.Press("RotateVaultKeyButton");
            app.WaitForText("RotateStatus", "Enter your current master password to rotate the vault key.");
            Report.Check("With nothing typed: refused in words, the application goes on, the file untouched", Fingerprint(VaultPath) == before && app.Screen() == "main");

            app.Type("RotatePasswordBox", Prefix + "not the master password");
            app.Press("RotateVaultKeyButton");
            app.WaitForText("RotateStatus", "The current password is incorrect. The vault key was not rotated.");
            Report.Check(
                "Wrong current password: refused, the file byte for byte what it was",
                Fingerprint(VaultPath) == before && !Directory.EnumerateFiles(_directory, "*.tmp").Any());

            // Abandoned.
            app.Type("RotatePasswordBox", MasterPassword);
            app.Press("VaultNavButton");
            app.Find("AddLoginButton");
            app.Press("LockButton");
            app.WaitForScreen("unlock");
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            Report.Check(
                "Abandoned: the password typed and the page left — nothing written, the field empty on return",
                Fingerprint(VaultPath) == before && app.Value("RotatePasswordBox").Length == 0);

            // Valid rotation.
            app.Type("RotatePasswordBox", MasterPassword);
            app.Press("RotateVaultKeyButton");
            app.WaitForText("RotateStatus", "Vault key rotated.");
            EncryptedVault rotated = Envelope();
            Report.Check(
                "Valid rotation: salt, nonces, wrapped key and ciphertext all change; format and settings do not",
                Fingerprint(VaultPath) != before
                && !rotated.Salt.SequenceEqual(old.Salt) && !rotated.KeyWrapNonce.SequenceEqual(old.KeyWrapNonce)
                && !rotated.EncryptedVaultKey.SequenceEqual(old.EncryptedVaultKey) && !rotated.ContentNonce.SequenceEqual(old.ContentNonce)
                && !rotated.Ciphertext.SequenceEqual(old.Ciphertext) && rotated.Ciphertext.Length == old.Ciphertext.Length
                && rotated.Version == 1 && rotated.KdfParameters == old.KdfParameters
                && app.Value("RotatePasswordBox").Length == 0);

            // Lock and unlock with the same password.
            app.Press("LockButton");
            app.WaitForScreen("unlock");
            Unlock(app, MasterPassword);
            Report.Check(
                "Locked and unlocked with the same password: the items are intact",
                ItemNames(app).Order(StringComparer.Ordinal).SequenceEqual([Other, Name]),
                string.Join(", ", ItemNames(app).Select(Shorten)));

            // A save after the rotation.
            AddLogin(app, Prefix + "Beta", Prefix + "later@example.com", Prefix + "another-password-2", null, null);
            EncryptedVault saved = Envelope();
            Report.Check(
                "A save after the rotation is written under the rotated key",
                saved.Salt.SequenceEqual(rotated.Salt) && saved.EncryptedVaultKey.SequenceEqual(rotated.EncryptedVaultKey)
                && !saved.Ciphertext.SequenceEqual(rotated.Ciphertext) && ItemNames(app).Count == 3);

            // A restart.
            string beforeRestart = Fingerprint(VaultPath)!;
            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            Unlock(app, MasterPassword);
            Report.Check(
                "A restart: nothing is rewritten by closing or starting, and the vault opens",
                Fingerprint(VaultPath) == beforeRestart && ItemNames(app).Count == 3);

            // The backup from before.
            app.Press("SettingsNavButton");
            Validate(app, backup, MasterPassword);
            Report.Check(
                "The backup from before the rotation still validates, shows the older two items, and changes nothing",
                app.Text("RestoreSummary").Contains("2 item(s)", StringComparison.Ordinal)
                && Fingerprint(VaultPath) == beforeRestart && Fingerprint(backup) == before,
                app.Text("RestoreSummary"));
            app.Press("RestoreCancelButton");
            app.WaitUntil(() => !app.Shows("RestoreCancelButton"), "the preview closes");

            // Locked.
            app.Press("LockButton");
            app.WaitForScreen("unlock");
            Report.Check(
                "While locked, nothing of Settings is within reach",
                !app.Shows("RotateVaultKeyButton") && !app.Shows("ChangePasswordButton") && !app.Shows("ExportBackupButton") && !app.Shows("SettingsNavButton"));
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    // ---- Strengthening the key derivation ----

    private static void StrengtheningTheKeyDerivation()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");
            string before = Fingerprint(VaultPath)!;
            EncryptedVault old = Envelope();
            Report.Check(
                "A new vault is created with 256 MiB, and Settings describes the vault that is open",
                old.KdfParameters.MemoryKiB == 262144 && app.Text("KdfDescription") == "Argon2id — 256 MiB, 3 iterations, parallelism 4",
                app.Text("KdfDescription"));
            Report.Check("The button is disabled until a password is typed", !app.IsEnabled("StrengthenKeyDerivationButton"));

            app.Press("TestKeyDerivationButton");
            app.WaitForText("KeyDerivationTestResult", "uses 1024 MiB of memory", TimeSpan.FromSeconds(120));
            Report.Check("Measuring shows time and memory, and changes nothing", Fingerprint(VaultPath) == before, app.Text("KeyDerivationTestResult"));

            app.Type("StrengthenPasswordBox", Prefix + "not the master password");
            app.Press("StrengthenKeyDerivationButton");
            app.WaitForText("StrengthenStatus", "The current password is incorrect. Nothing was changed.");
            Report.Check("A wrong password changes nothing", Fingerprint(VaultPath) == before);

            app.Type("StrengthenPasswordBox", MasterPassword);
            app.Press("StrengthenKeyDerivationButton");
            app.WaitForText("StrengthenStatus", "Key derivation strengthened to Maximum (1024 MiB).", TimeSpan.FromSeconds(120));
            EncryptedVault strengthened = Envelope();
            Report.Check(
                "The right one moves the vault to 1 GiB: same content, new wrapping, and the page updated",
                strengthened.KdfParameters.MemoryKiB == 1048576 && strengthened.KdfParameters.Iterations == old.KdfParameters.Iterations
                && strengthened.Ciphertext.SequenceEqual(old.Ciphertext) && !strengthened.Salt.SequenceEqual(old.Salt)
                && app.Text("KdfDescription") == "Argon2id — 1024 MiB, 3 iterations, parallelism 4",
                app.Text("KdfDescription"));

            // With the strongest settings there is nothing more to offer, and no field left with a
            // password in it.
            Report.Check(
                "At the strongest settings the page says so, and offers nothing more",
                app.Shows("KeyDerivationAtStrongest") && !app.Shows("StrengthenPasswordBox") && !app.Shows("StrengthenKeyDerivationButton"));

            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();
            app = RunningApp.Start(_executable, _directory);
            Unlock(app, MasterPassword);
            Report.Check("After a restart the vault opens with the same password", ItemNames(app).Count == 3);
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    // ---- H-series ----

    private static void RestoreConfirmation()
    {
        RunningApp app = RunningApp.Start(_executable, _directory);
        try
        {
            RequireOwnWindow(app);
            Unlock(app, MasterPassword);
            app.Press("SettingsNavButton");

            string backup = Path.Combine(_directory, "for-restore.bitosx-backup");
            app.Press("ExportBackupButton");
            FileDialog.Answer(app.ProcessId, backup, TimeSpan.FromSeconds(30));
            app.WaitForText("BackupStatus", "Encrypted backup exported.");

            app.Press("VaultNavButton");
            AddLogin(app, Prefix + "Gamma", Prefix + "after@example.com", Prefix + "another-password-3", null, null);
            app.Press("SettingsNavButton");
            string live = Fingerprint(VaultPath)!;

            // A wrong password, and none.
            Choose(app, backup);
            app.Press("ValidateBackupButton");
            app.WaitForText("RestoreStatus", "Enter the master password of the backup.");
            Report.Check("With no password typed: refused in words, the application goes on", app.Screen() == "main" && !app.Shows("ReplaceVaultButton"));
            app.Type("RestorePasswordBox", Prefix + "not the master password");
            app.Press("ValidateBackupButton");
            app.WaitForText("RestoreStatus", "The backup password is incorrect, or the backup is corrupted.");
            Report.Check("A wrong backup password offers nothing", !app.Shows("ReplaceVaultButton") && Fingerprint(VaultPath) == live);

            // H3.
            app.Type("RestorePasswordBox", MasterPassword);
            app.Press("ValidateBackupButton");
            app.Find("ReplaceVaultButton");
            Thread.Sleep(500);
            Report.Check(
                "H3. The preview starts unacknowledged, with \"Replace my vault\" disabled",
                !app.IsChecked("RestoreAcknowledgeCheckBox") && !app.IsEnabled("ReplaceVaultButton")
                && app.Text("RestoreSummary").Contains("3 item(s)", StringComparison.Ordinal),
                app.Text("RestoreSummary"));
            Report.Check("and with Cancel in focus", app.HasFocus("RestoreCancelButton"));
            Report.Check("Validating wrote nothing", Fingerprint(VaultPath) == live && !Directory.EnumerateFiles(_directory, "*.tmp").Any());

            // H1.
            app.Key(Enter);
            Thread.Sleep(800);
            Report.Check("H1. Enter at the preview restores nothing", Fingerprint(VaultPath) == live && app.Screen() == "main");

            // H2.
            if (!app.Shows("ReplaceVaultButton"))
            {
                Validate(app, backup, MasterPassword);
            }

            app.Key(Space);
            Thread.Sleep(800);
            Report.Check("H2. Space at the preview restores nothing", Fingerprint(VaultPath) == live && app.Screen() == "main");

            // H5, Escape.
            if (!app.Shows("ReplaceVaultButton"))
            {
                Validate(app, backup, MasterPassword);
            }

            app.SetChecked("RestoreAcknowledgeCheckBox", true);
            Report.Check("Acknowledging enables \"Replace my vault\" and writes nothing", app.IsEnabled("ReplaceVaultButton") && Fingerprint(VaultPath) == live);
            app.Key(Escape);
            app.WaitUntil(() => !app.Shows("ReplaceVaultButton"), "the preview closes");
            Report.Check("H5. Escape cancels, and restores nothing", Fingerprint(VaultPath) == live && !app.Shows("RestorePasswordBox"));

            Validate(app, backup, MasterPassword);
            Report.Check("Coming back, the acknowledgement is not kept", !app.IsChecked("RestoreAcknowledgeCheckBox") && !app.IsEnabled("ReplaceVaultButton"));

            // H5, Cancel.
            app.SetChecked("RestoreAcknowledgeCheckBox", true);
            app.Press("RestoreCancelButton");
            app.WaitUntil(() => !app.Shows("ReplaceVaultButton"), "the preview closes");
            Validate(app, backup, MasterPassword);
            Report.Check(
                "H5. Cancel does the same",
                !app.IsChecked("RestoreAcknowledgeCheckBox") && !app.IsEnabled("ReplaceVaultButton") && Fingerprint(VaultPath) == live);

            // H6.
            app.SetChecked("RestoreAcknowledgeCheckBox", true);
            app.Type("RestorePasswordBox", MasterPassword + "x");
            app.WaitUntil(() => !app.Shows("ReplaceVaultButton"), "the offer is withdrawn");
            Report.Check("H6. Changing the password after validating withdraws the offer", Fingerprint(VaultPath) == live);

            // H4.
            app.Type("RestorePasswordBox", MasterPassword);
            app.Press("ValidateBackupButton");
            app.Find("ReplaceVaultButton");
            app.SetChecked("RestoreAcknowledgeCheckBox", true);
            Thread.Sleep(500);
            Report.Check("H4. Nothing is written until the button is pressed", Fingerprint(VaultPath) == live);
            app.Press("ReplaceVaultButton");
            app.WaitForScreen("unlock");
            string recovery = VaultPath + ".pre-restore";
            Report.Check(
                "Pressing it replaces the vault with the backup, locks, and keeps what was replaced as a recovery copy",
                Fingerprint(VaultPath) == Fingerprint(backup) && File.Exists(recovery) && Fingerprint(recovery) == live
                && !Directory.EnumerateFiles(_directory, "*.tmp").Any());

            Unlock(app, MasterPassword);
            Report.Check("The restored vault opens, with the items of the backup", ItemNames(app).Count == 3, string.Join(", ", ItemNames(app).Select(Shorten)));
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
        }
    }

    // ---- A device without the memory ----

    private static void DeviceWithoutTheMemory()
    {
        // The application, with the memory it may use limited to 200 MiB: 256 MiB cannot be had for
        // deriving a key, 64 MiB can.
        string directory = _directory + "-small";
        var limited = new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = "0xC800000" };
        RunningApp app = RunningApp.Start(_executable, directory, limited);
        try
        {
            app.Type("MasterPasswordBox", MasterPassword);
            app.Type("ConfirmPasswordBox", MasterPassword);
            app.Press("CreateButton");
            app.WaitForScreen("main", TimeSpan.FromSeconds(60));
            string vault = Path.Combine(directory, "vault.bitosx");
            int memory = EncryptedVaultSerializer.Deserialize(File.ReadAllBytes(vault)).KdfParameters.MemoryKiB;
            Report.Check(
                "The vault is created all the same, with 64 MiB",
                memory == 65536,
                $"{memory / 1024} MiB");

            app.Press("SettingsNavButton");
            Report.Check(
                "Settings says what the vault was created with, and that it can be strengthened",
                app.Text("KdfDescription") == "Argon2id — 64 MiB, 3 iterations, parallelism 4"
                && (app.TextOrNull("KdfWarning")?.Contains("Strengthen key derivation", StringComparison.Ordinal) ?? false),
                app.Text("KdfDescription"));

            app.Press("TestKeyDerivationButton");
            app.WaitForText("KeyDerivationTestResult", "This device does not have 256 MiB of free memory.", TimeSpan.FromSeconds(60));
            Report.Check("Measuring the stronger settings says that the device cannot afford them", app.Screen() == "main", app.Text("KeyDerivationTestResult"));

            app.Type("StrengthenPasswordBox", MasterPassword);
            app.Press("StrengthenKeyDerivationButton");
            app.WaitForText("StrengthenStatus", "Nothing was changed.", TimeSpan.FromSeconds(60));
            Report.Check(
                "Strengthening is refused there, and nothing is written",
                EncryptedVaultSerializer.Deserialize(File.ReadAllBytes(vault)).KdfParameters.MemoryKiB == 65536,
                app.Text("StrengthenStatus"));

            Report.Check("The window closes normally", app.Close() == 0);
            app.Dispose();

            // The vault opens there, and anywhere else.
            app = RunningApp.Start(_executable, directory, limited);
            Unlock(app, MasterPassword);
            Report.Check("The vault opens on the device it was created on", app.Screen() == "main");
            Report.Check("The window closes normally", app.Close() == 0);
        }
        finally
        {
            app.Dispose();
            TryDelete(directory);
        }
    }

    // ---- What is done more than once ----

    /// <summary>Never operate a window that is not the one this run started on its own data directory.</summary>
    private static void RequireOwnWindow(RunningApp app)
    {
        if (app.Title != "BitosX — " + _directory)
        {
            throw new InvalidOperationException("The window does not name the data directory of this run: " + app.Title);
        }
    }

    private static void Unlock(RunningApp app, string password)
    {
        app.WaitForScreen("unlock");
        app.Type("MasterPasswordBox", password);
        app.Press("UnlockButton");
        app.WaitForScreen("main", TimeSpan.FromSeconds(60));
    }

    private static void AddLogin(RunningApp app, string name, string username, string password, string? url, string? notes)
    {
        app.Press("AddLoginButton");
        app.Find("NameBox");
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
        app.WaitUntil(() => !app.Shows("NameBox"), "the editor closes");
        app.WaitUntil(() => ItemNames(app).Contains(name, StringComparer.Ordinal), "the item is listed");
    }

    private static void Choose(RunningApp app, string backup)
    {
        app.Press("ChooseBackupButton");
        FileDialog.Answer(app.ProcessId, backup, TimeSpan.FromSeconds(30));
        app.Find("RestorePasswordBox");
    }

    private static void Validate(RunningApp app, string backup, string password)
    {
        Choose(app, backup);
        app.Type("RestorePasswordBox", password);
        app.Press("ValidateBackupButton");
        app.Find("ReplaceVaultButton");
        Thread.Sleep(500);
    }

    private static IReadOnlyList<string> ItemNames(RunningApp app)
        => [.. app.FindAll("ItemName").Select(item => item.Current.Name)];

    private static EncryptedVault Envelope() => EncryptedVaultSerializer.Deserialize(File.ReadAllBytes(VaultPath));

    private static string? Fingerprint(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static bool FileContains(string path, string text)
        => Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text, StringComparison.Ordinal);

    private static string Shorten(string text)
        => text.Replace(_directory, "<data directory>", StringComparison.Ordinal).Replace(Prefix, string.Empty, StringComparison.Ordinal);

    private static bool IsOurs(Process process)
    {
        try
        {
            return process.MainWindowTitle.Contains(_directory, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
