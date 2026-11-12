using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BitosX.App.Controls;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;

namespace BitosX.App.Tests;

/// <summary>
/// Authenticator keys in the desktop (docs/TOTP.md §5): the editor's field, masked and checked as it
/// is typed; the code on the vault page with its seconds, copied through the clipboard that clears
/// itself; and nothing computed or shown once the vault locks. The key never appears in the window
/// other than in the editor's box, and there only while it is shown.
/// </summary>
public sealed class TotpUiTests
{
    // RFC 6238's SHA1 seed; at 59 seconds its 8-digit code is 94287082 (Appendix B), and its
    // 6-digit code 287082.
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string LowerCaseSecret = "gezdgnbvgy3tqojq";

    private static readonly TotpKey Key = new() { Secret = Secret, Algorithm = TotpAlgorithm.Sha1, Digits = 8, Period = 30 };

    /// <summary>A clock that stands still until it is moved, with timers that fire when told to.</summary>
    private sealed class ManualClock(long unixSeconds) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public IEnumerable<ManualTimer> Running => _timers.Where(t => !t.IsDisposed);

        public void Tick(TimeSpan by)
        {
            Now += by;
            foreach (ManualTimer timer in Running.ToArray())
            {
                timer.Fire();
            }
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static (VaultPageViewModel Page, FakeVaultService Vault, FakeClipboardService Clipboard, ManualClock Clock) Page(long unixSeconds = 59)
    {
        var vault = new FakeVaultService();
        vault.Stored.Add(new LoginItem { Name = "With a key", Username = "fake.user", Password = "FAKE-pw", Totp = Key });
        vault.Stored.Add(new LoginItem { Name = "Without", Username = "fake.other", Password = "FAKE-pw-2" });
        var clipboard = new FakeClipboardService();
        var clock = new ManualClock(unixSeconds);
        var page = new VaultPageViewModel(vault, clipboard, new PasswordGenerator(), time: clock);
        page.RefreshAsync();
        return (page, vault, clipboard, clock);
    }

    private static LoginItemViewModel WithKey(VaultPageViewModel page) => page.AllItems.Single(i => i.Name == "With a key");

    private static LoginItemViewModel Without(VaultPageViewModel page) => page.AllItems.Single(i => i.Name == "Without");

    // ---- The editor ----

    private static LoginEditorViewModel Editor(LoginItem? existing, Func<LoginItem, Task> onSave)
        => new(existing, new PasswordGenerator(), onSave, () => { });

    [Fact]
    public async Task Editor_AKey_IsSavedWithTheLogin()
    {
        LoginItem? saved = null;
        LoginEditorViewModel editor = Editor(null, item => { saved = item; return Task.CompletedTask; });
        editor.Name = "Example";
        editor.TotpText = "otpauth://totp/x?secret=" + LowerCaseSecret + "&digits=8&algorithm=SHA256&period=60";

        Assert.Null(editor.TotpError);
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new TotpKey { Secret = "GEZDGNBVGY3TQOJQ", Algorithm = TotpAlgorithm.Sha256, Digits = 8, Period = 60 }, saved!.Totp);
    }

    [Theory]
    [InlineData("otpauth://hotp/x?secret=GEZDGNBVGY3TQOJQ", TotpRefusal.HotpNotSupported)]
    [InlineData("GEZDGNBV", TotpRefusal.SecretTooShort)]
    [InlineData("GEZDGNBVGY3TQOJQ-1", TotpRefusal.InvalidSecret)]
    [InlineData("steam://GEZDGNBVGY3TQOJQ", TotpRefusal.SteamNotSupported)]
    public void Editor_AKeyThatIsNotOne_SaysWhy_AndCannotBeSaved(string text, TotpRefusal refusal)
    {
        LoginEditorViewModel editor = Editor(null, _ => throw new InvalidOperationException("must not be saved"));
        editor.Name = "Example";
        editor.TotpText = text;

        Assert.Equal(TotpTexts.Text(refusal), editor.TotpError);
        Assert.True(editor.HasTotpError);
        Assert.DoesNotContain("GEZDGNBV", editor.TotpError, StringComparison.OrdinalIgnoreCase);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.TotpText = string.Empty;
        Assert.Null(editor.TotpError);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Editor_AnExistingKey_IsShownSoThatSavingKeepsIt_AndRemovingTheTextRemovesIt()
    {
        var existing = new LoginItem
        {
            Name = "Example",
            Totp = new TotpKey { Secret = Secret, Algorithm = TotpAlgorithm.Sha512, Digits = 7, Period = 45 },
        };
        LoginItem? saved = null;
        LoginEditorViewModel editor = Editor(existing, item => { saved = item; return Task.CompletedTask; });

        Assert.False(editor.IsTotpVisible); // masked by default
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(existing.Totp, saved!.Totp);

        editor.TotpText = "   ";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(saved.Totp);
    }

    [Fact]
    public void Editor_TheKeyToggle_ShowsAndHidesIt()
    {
        LoginEditorViewModel editor = Editor(null, _ => Task.CompletedTask);
        Assert.Equal("Show", editor.TotpToggleText);
        editor.ToggleTotpVisibilityCommand.Execute(null);
        Assert.True(editor.IsTotpVisible);
        Assert.Equal("Hide", editor.TotpToggleText);
        Assert.Equal(TotpTexts.FieldLabel, editor.TotpLabel);
        Assert.Equal(TotpTexts.FieldHint, editor.TotpHint);
    }

    // ---- The code on the vault page ----

    [Fact]
    public void ThePage_ShowsTheCodeAndTheSecondsLeft_ForALoginWithAKeyOnly()
    {
        (VaultPageViewModel page, _, _, _) = Page(59);

        Assert.True(page.HasAuthenticatorCodes);
        Assert.Equal(TotpTexts.ClockNote, page.ClockNote);
        Assert.Equal("9428 7082", WithKey(page).CodeDisplay);
        Assert.Equal("1 s left", WithKey(page).CodeSecondsLeft);
        Assert.True(WithKey(page).HasTotp);
        Assert.False(Without(page).HasTotp);
        Assert.Null(Without(page).CodeDisplay);
        Assert.True(page.IsCodeTimerRunning);
    }

    [Fact]
    public void TheTimer_KeepsTheCodeUpToDate()
    {
        (VaultPageViewModel page, _, _, ManualClock clock) = Page(59);

        clock.Tick(TimeSpan.FromSeconds(1)); // 60: a new step
        Assert.Equal(TotpGenerator.Generate(Key, 60).Display, WithKey(page).CodeDisplay);
        Assert.NotEqual("9428 7082", WithKey(page).CodeDisplay);
        Assert.Equal("30 s left", WithKey(page).CodeSecondsLeft);

        clock.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("29 s left", WithKey(page).CodeSecondsLeft);
    }

    [Fact]
    public void AVaultWithoutKeys_RunsNoTimer()
    {
        var vault = new FakeVaultService();
        vault.Stored.Add(new LoginItem { Name = "Without", Password = "FAKE-pw" });
        var clock = new ManualClock(59);
        var page = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator(), time: clock);
        page.RefreshAsync();

        Assert.False(page.HasAuthenticatorCodes);
        Assert.False(page.IsCodeTimerRunning);
        Assert.Empty(clock.Running);
    }

    [Fact]
    public void Locking_StopsTheTimer_AndClearsTheCode()
    {
        (VaultPageViewModel page, FakeVaultService vault, _, ManualClock clock) = Page(59);
        Assert.NotNull(WithKey(page).CodeDisplay);

        vault.Lock();

        Assert.False(page.IsCodeTimerRunning);
        Assert.Empty(clock.Running);
        Assert.Null(WithKey(page).CodeDisplay);
        Assert.Null(WithKey(page).CodeSecondsLeft);
        Assert.False(WithKey(page).IsCodeShown);

        // Nothing comes back by itself, nor when the page is asked to show codes while locked.
        page.UpdateCodes();
        Assert.Null(WithKey(page).CodeDisplay);
        Assert.False(page.IsCodeTimerRunning);
    }

    [Fact]
    public void LettingGoOfThePage_StopsTheTimer_AndClearsTheCode()
    {
        (VaultPageViewModel page, _, _, ManualClock clock) = Page(59);

        page.Dispose();

        Assert.Empty(clock.Running);
        Assert.Null(WithKey(page).CodeDisplay);
    }

    [Fact]
    public void ACardTheSearchHides_ShowsNoCode()
    {
        (VaultPageViewModel page, _, _, _) = Page(59);
        page.SearchText = "Without";
        Assert.Null(WithKey(page).CodeDisplay);
        page.SearchText = string.Empty;
        Assert.Equal("9428 7082", WithKey(page).CodeDisplay);
    }

    [Fact]
    public async Task CopyingTheCode_PutsTheDigitsOnTheSensitiveClipboard_AsTheyAreNow()
    {
        (VaultPageViewModel page, _, FakeClipboardService clipboard, ManualClock clock) = Page(59);
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1111111109); // the screen still shows the code of 59

        await page.CopyCodeCommand.ExecuteAsync(WithKey(page));

        Assert.Equal("07081804", clipboard.LastCopied);
        Assert.Equal("Authenticator code copied. Clipboard clears in 30s.", page.StatusMessage);
        Assert.DoesNotContain(Secret, page.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingTheCode_WhileLocked_CopiesNothing()
    {
        (VaultPageViewModel page, FakeVaultService vault, FakeClipboardService clipboard, _) = Page(59);
        LoginItemViewModel item = WithKey(page);
        vault.Lock();

        await page.CopyCodeCommand.ExecuteAsync(item);

        Assert.Null(clipboard.LastCopied);
    }

    [Fact]
    public void TheLabels_NameTheLogin_AndNeverTheKey()
    {
        (VaultPageViewModel page, _, _, _) = Page(59);
        LoginItemViewModel item = WithKey(page);
        Assert.Equal("Copy the authenticator code of With a key", item.CopyCodeLabel);
        Assert.DoesNotContain(Secret, item.Model.ToString(), StringComparison.Ordinal);
    }

    // ---- As rendered ----

    private static List<string> EverythingInTheWindow(Window window)
    {
        var texts = new List<string>();
        foreach (Control control in window.GetVisualDescendants().OfType<Control>())
        {
            if (control is TextBlock { Text: { Length: > 0 } block })
            {
                texts.Add(block);
            }

            if (control is ContentControl { Content: string content })
            {
                texts.Add(content);
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

    [Fact]
    public Task TheEditor_MasksTheKey_AndSaysWhyItIsNotOne() => HeadlessUi.RunAsync(() =>
    {
        LoginEditorViewModel editor = Editor(new LoginItem { Name = "Example", Totp = Key with { Digits = 6 } }, _ => Task.CompletedTask);
        var view = new LoginEditorView { DataContext = editor };
        Window window = HeadlessUi.Show(view);

        SecretBox box = HeadlessUi.Find<SecretBox>(view, "TotpBox");
        Assert.Equal(Secret, box.Text);
        Assert.False(box.RevealPassword);
        Assert.Equal(new string(SecretBox.DefaultPasswordChar, Secret.Length), box.DisplayedText);
        Assert.Equal(TotpTexts.FieldHint, HeadlessUi.Find<TextBlock>(view, "TotpHint").Text);
        Assert.False(HeadlessUi.IsShown(HeadlessUi.Find<TextBlock>(view, "TotpError")));

        // Nothing but the box holds the key: not a label, a tooltip or a name a screen reader hears.
        Assert.DoesNotContain(EverythingInTheWindow(window), text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase));

        HeadlessUi.Retype(window, box, "otpauth://hotp/x?secret=" + Secret);
        HeadlessUi.Settle();
        TextBlock error = HeadlessUi.Find<TextBlock>(view, "TotpError");
        Assert.True(HeadlessUi.IsShown(error));
        Assert.Equal(TotpTexts.Text(TotpRefusal.HotpNotSupported), error.Text);
        Assert.False(HeadlessUi.Find<Button>(view, "SaveButton").IsEffectivelyEnabled);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "RevealTotpButton"));
        Assert.True(box.RevealPassword);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ThePage_ShowsTheCode_AndClearsItWhenTheVaultLocks() => HeadlessUi.RunAsync(() =>
    {
        (VaultPageViewModel page, FakeVaultService vault, FakeClipboardService clipboard, _) = Page(59);
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);

        Assert.True(HeadlessUi.IsShown(HeadlessUi.Find<TextBlock>(view, "ClockNote")));
        List<string> texts = EverythingInTheWindow(window);
        Assert.Contains("9428 7082", texts);
        Assert.Contains("1 s left", texts);
        Assert.Contains("Copy the authenticator code of With a key", texts);
        Assert.DoesNotContain(texts, text => text.Contains(Secret, StringComparison.OrdinalIgnoreCase));

        Button copy = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "CopyCodeButton" && b.IsEffectivelyVisible);
        HeadlessUi.Press(window, copy);
        HeadlessUi.Settle();
        Assert.Equal("94287082", clipboard.LastCopied);

        vault.Lock();
        HeadlessUi.Settle();
        Assert.DoesNotContain("9428 7082", EverythingInTheWindow(window));
        return Task.CompletedTask;
    });
}
