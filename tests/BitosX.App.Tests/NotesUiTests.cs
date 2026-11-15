using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using BitosX.App.Controls;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Security;
using BitosX.Core.Services;
using BitosX.Core.Validation;
using BitosX.Crypto;
using BitosX.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Secure notes in the desktop (docs/NOTES.md §3): created and edited in their own editor, whose text
/// is masked until Show; listed with logins under the label "Secure note", with the text hidden until
/// Show and copied through the clipboard that clears itself; found by name only; left alone by the
/// Security Center; and hidden again when the vault locks. The text is never in a label, a tooltip, an
/// accessible name or a status text.
/// </summary>
public sealed class NotesUiTests : IDisposable
{
    private const string Secret = "FAKE-NOTE-SECRET";
    private const string Text = "Recovery codes: " + Secret + "\r\nline two <b>not markup</b> räksmörgås";

    private static readonly Argon2Parameters Small = new(8192, 2, 4, 32);

    private readonly string _directory;

    public NotesUiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BitosX.NotesUiTests", Guid.NewGuid().ToString("N"));
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
    }

    private static (VaultPageViewModel Page, FakeVaultService Vault, FakeClipboardService Clipboard) Page()
    {
        var vault = new FakeVaultService();
        vault.Stored.Add(new LoginItem { Name = "Bank", Username = "fake.banker", Password = "FAKE-pw" });
        vault.Stored.Add(new NoteItem { Name = "Codes", Notes = Text });
        vault.Stored.Add(new NoteItem { Name = "Only a name" });
        vault.Stored.Add(new NoteItem { Name = "Zebra favorite", Notes = "FAKE zebra", Favorite = true });
        var clipboard = new FakeClipboardService();
        var page = new VaultPageViewModel(vault, clipboard, new PasswordGenerator());
        page.RefreshAsync();
        return (page, vault, clipboard);
    }

    private static NoteItemViewModel Note(VaultPageViewModel page, string name)
        => page.AllItems.OfType<NoteItemViewModel>().Single(n => n.Name == name);

    private async Task<VaultService> UnlockedVaultAsync()
    {
        var vault = new VaultService(
            VaultCryptor.CreateDefault(), new FileLocalVaultStorage(Path.Combine(_directory, "vault.bitosx")), Small, NullLogger<VaultService>.Instance);
        await vault.CreateVaultAsync("correct horse battery");
        return vault;
    }

    // ---- The list ----

    [Fact]
    public void NotesAndLogins_AreOneList_FavoritesFirst_ThenByName()
    {
        (VaultPageViewModel page, _, _) = Page();

        Assert.Equal(["Zebra favorite", "Bank", "Codes", "Only a name"], page.Items.Select(i => i.Name));
        Assert.IsType<LoginItemViewModel>(page.Items[1]);
        NoteItemViewModel codes = Note(page, "Codes");
        Assert.Equal("Secure note", codes.KindLabel);
        Assert.Equal("Delete this note? This cannot be undone.", codes.DeleteQuestion);
        Assert.False(page.IsVaultEmpty);
    }

    [Fact]
    public void TheText_IsHiddenUntilShown_AndTheLabelsNameTheNote_NeverItsText()
    {
        (VaultPageViewModel page, _, _) = Page();
        NoteItemViewModel codes = Note(page, "Codes");

        Assert.True(codes.HasText);
        Assert.False(codes.IsTextVisible);
        Assert.Equal("••••••••••", codes.DisplayedText);
        Assert.Equal("Show", codes.TextToggleGlyph);
        Assert.Equal("Show the text of Codes", codes.TextToggleLabel);
        Assert.Equal("Copy the text of Codes", codes.CopyTextLabel);

        page.ToggleNoteTextCommand.Execute(codes);
        Assert.Equal(Text, codes.DisplayedText);
        Assert.Equal("Hide", codes.TextToggleGlyph);
        Assert.Equal("Hide the text of Codes", codes.TextToggleLabel);

        string[] labels =
        [
            codes.TextToggleLabel, codes.CopyTextLabel, codes.EditLabel, codes.DeleteLabel, codes.KeepLabel,
            codes.ConfirmDeleteLabel, codes.FavoriteDescription, codes.FavoriteLabel, codes.KindLabel, codes.DeleteQuestion,
        ];
        Assert.DoesNotContain(labels, label => label.Contains(Secret, StringComparison.Ordinal));

        page.ToggleNoteTextCommand.Execute(codes);
        Assert.Equal("••••••••••", codes.DisplayedText);
    }

    [Fact]
    public void ANoteWithOnlyAName_HasNothingToShowOrCopy()
    {
        (VaultPageViewModel page, _, _) = Page();
        NoteItemViewModel note = Note(page, "Only a name");

        Assert.False(note.HasText);
        page.ToggleNoteTextCommand.Execute(note);
        Assert.False(note.IsTextVisible);
    }

    [Fact]
    public async Task CopyingTheText_PutsItOnTheSensitiveClipboard_Exactly_AndSaysSoWithoutIt()
    {
        (VaultPageViewModel page, _, FakeClipboardService clipboard) = Page();

        await page.CopyNoteTextCommand.ExecuteAsync(Note(page, "Codes"));

        Assert.Equal(Text, clipboard.LastCopied);
        Assert.Equal("Text copied. Clipboard clears in 30s.", page.StatusMessage);
    }

    [Fact]
    public async Task CopyingTheText_WhileLocked_CopiesNothing()
    {
        (VaultPageViewModel page, FakeVaultService vault, FakeClipboardService clipboard) = Page();
        NoteItemViewModel codes = Note(page, "Codes");
        vault.Lock();

        await page.CopyNoteTextCommand.ExecuteAsync(codes);

        Assert.Null(clipboard.LastCopied);
    }

    [Fact]
    public void TheSearch_FindsANoteByItsName_NeverByItsText()
    {
        (VaultPageViewModel page, _, _) = Page();

        page.SearchText = "codes"; // the name, in another case
        Assert.Equal(["Codes"], page.Items.Select(i => i.Name));

        page.SearchText = Secret; // in the text only
        Assert.Empty(page.Items);
        Assert.True(page.HasNoMatches);
        Assert.Equal("No logins or notes match “" + Secret + "”.", page.NoMatchesText);

        page.SearchText = "zebra"; // "FAKE zebra" is the text of the note named "Zebra favorite": found by its name
        Assert.Equal(["Zebra favorite"], page.Items.Select(i => i.Name));
    }

    [Fact]
    public void Locking_HidesEveryTextThatWasShown()
    {
        (VaultPageViewModel page, FakeVaultService vault, _) = Page();
        NoteItemViewModel codes = Note(page, "Codes");
        NoteItemViewModel zebra = Note(page, "Zebra favorite");
        page.ToggleNoteTextCommand.Execute(codes);
        page.ToggleNoteTextCommand.Execute(zebra);

        vault.Lock();

        Assert.False(codes.IsTextVisible);
        Assert.False(zebra.IsTextVisible);
        Assert.Equal("••••••••••", codes.DisplayedText);
    }

    [Fact]
    public void LettingGoOfThePage_HidesEveryText()
    {
        (VaultPageViewModel page, _, _) = Page();
        NoteItemViewModel codes = Note(page, "Codes");
        page.ToggleNoteTextCommand.Execute(codes);

        page.Dispose();

        Assert.False(codes.IsTextVisible);
    }

    [Fact]
    public async Task ANote_IsMadeAFavorite_AndDeleted_LikeALogin_AndTheRebuiltListShowsNoText()
    {
        (VaultPageViewModel page, FakeVaultService vault, _) = Page();
        NoteItemViewModel codes = Note(page, "Codes");
        page.ToggleNoteTextCommand.Execute(codes);

        await page.ToggleFavoriteCommand.ExecuteAsync(codes);
        Assert.False(Note(page, "Codes").IsTextVisible);

        page.RequestDeleteCommand.Execute(Note(page, "Codes"));
        Assert.True(Note(page, "Codes").IsDeletePending);
        await page.DeleteItemCommand.ExecuteAsync(Note(page, "Codes"));

        Assert.DoesNotContain(vault.Stored, i => i.Name == "Codes");
        Assert.DoesNotContain(page.AllItems, i => i.Name == "Codes");
        Assert.Equal("Item deleted.", page.StatusMessage);
    }

    [Fact]
    public void TheSecurityCenter_LeavesNotesAlone()
    {
        var vault = new FakeVaultService();
        vault.Stored.Add(new NoteItem { Name = "Same", Notes = "a" });
        vault.Stored.Add(new NoteItem { Name = "Same", Notes = "a" });
        vault.Stored.Add(new NoteItem { Name = "http://insecure.example", Notes = "http://insecure.example 123" });
        var security = new SecurityPageViewModel(vault, new SecurityAnalyzer());

        security.Analyze();

        Assert.Equal(0, security.TotalIssues);
        Assert.Equal("No issues found in the local checks.", security.SummaryText);
    }

    // ---- The editor ----

    [Fact]
    public async Task TheEditor_OfANewNote_IsMasked_NeedsAName_AndSavesTheTextExactly()
    {
        NoteItem? saved = null;
        var editor = new NoteEditorViewModel(null, item => { saved = item; return Task.CompletedTask; }, () => { });

        Assert.Equal("Add note", editor.Title);
        Assert.False(editor.IsTextVisible);
        Assert.Equal("Show", editor.TextToggleText);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Name = "  Codes  ";
        editor.Text = "  " + Text + "\n";
        editor.Favorite = true;
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Codes", saved!.Name);
        Assert.Equal("  " + Text + "\n", saved.Notes); // nothing trimmed
        Assert.True(saved.Favorite);

        editor.ToggleTextVisibilityCommand.Execute(null);
        Assert.True(editor.IsTextVisible);
        Assert.Equal("Hide", editor.TextToggleText);
    }

    [Fact]
    public async Task TheEditor_ABlankText_IsNoText()
    {
        NoteItem? saved = null;
        var editor = new NoteEditorViewModel(null, item => { saved = item; return Task.CompletedTask; }, () => { })
        {
            Name = "Only a name",
            Text = " \r\n\t ",
        };

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved!.Notes);
    }

    [Fact]
    public void TheEditor_ATextTooLong_SaysSoWithoutQuotingIt_AndCannotBeSaved()
    {
        var editor = new NoteEditorViewModel(null, _ => throw new InvalidOperationException("must not be saved"), () => { })
        {
            Name = "Long",
            Text = Secret + new string('x', VaultValidation.MaxNotesLength - Secret.Length + 1),
        };

        Assert.Equal("The text is longer than 16384 characters.", editor.TextError);
        Assert.True(editor.HasTextError);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Text = new string('x', VaultValidation.MaxNotesLength);
        Assert.Null(editor.TextError);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEditor_OfAnExistingNote_KeepsItsIdentity_AndStartsMasked()
    {
        var existing = new NoteItem { Name = "Codes", Notes = Text, Favorite = true, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
        NoteItem? saved = null;
        var editor = new NoteEditorViewModel(existing, item => { saved = item; return Task.CompletedTask; }, () => { });

        Assert.Equal("Edit note", editor.Title);
        Assert.Equal((existing.Name, Text, true), (editor.Name, editor.Text, editor.Favorite));
        Assert.False(editor.IsTextVisible);

        editor.Text = "FAKE new text";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(existing.Id, saved!.Id);
        Assert.Equal(existing.CreatedAt, saved.CreatedAt);
        Assert.Equal("FAKE new text", saved.Notes);
    }

    [Fact]
    public async Task AddingAndEditingANote_OnTheRealVault_IsSaved_AndMakesItVersion3()
    {
        using VaultService vault = await UnlockedVaultAsync();
        var page = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        await page.RefreshAsync();
        Assert.True(page.IsVaultEmpty);
        Assert.Equal("No logins or notes yet. Click “Add login” or “Add note” to create your first entry.", page.EmptyVaultText);

        page.AddNoteCommand.Execute(null);
        Assert.True(page.IsEditorOpen);
        Assert.Same(page.NoteEditor, page.OpenEditor);
        page.NoteEditor!.Name = "Codes";
        page.NoteEditor.Text = Text;
        await page.NoteEditor.SaveCommand.ExecuteAsync(null);

        Assert.False(page.IsEditorOpen);
        Assert.Equal("Saved.", page.StatusMessage);
        NoteItem stored = Assert.IsType<NoteItem>(Assert.Single(vault.Items));
        Assert.Equal(Text, stored.Notes);

        page.EditNoteCommand.Execute(Note(page, "Codes"));
        page.NoteEditor!.Name = "Codes, renamed";
        await page.NoteEditor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Codes, renamed", Assert.Single(vault.Items).Name);
        Assert.Equal(stored.Id, vault.Items[0].Id);
        Assert.Equal(Text, vault.Items[0].Notes);

        // The page behind the editor offers no way in while an editor is open, and Cancel closes it.
        page.AddNoteCommand.Execute(null);
        page.NoteEditor!.CancelCommand.Execute(null);
        Assert.False(page.IsEditorOpen);
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

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
            if (peer.GetName() is { Length: > 0 } spoken)
            {
                texts.Add(spoken);
            }

            if (peer is Avalonia.Automation.Provider.IValueProvider { Value: { Length: > 0 } value })
            {
                texts.Add(value);
            }
        }

        return texts;
    }

    private static bool Holds(IEnumerable<string> texts, string secret)
        => texts.Any(text => text.Contains(secret, StringComparison.Ordinal));

    [Fact]
    public Task ThePage_ShowsANoteAsANote_ItsTextOnlyWhenAsked_AndNoneOnceLocked() => HeadlessUi.RunAsync(() =>
    {
        (VaultPageViewModel page, FakeVaultService vault, FakeClipboardService clipboard) = Page();
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);

        Border card = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("card") && b.DataContext is NoteItemViewModel { Name: "Codes" });
        Assert.Equal("Secure note", HeadlessUi.Find<TextBlock>(card, "ItemKind").Text);
        Assert.Equal("Codes", HeadlessUi.Find<TextBlock>(card, "ItemName").Text);
        Assert.Equal("••••••••••", HeadlessUi.Find<TextBlock>(card, "ItemNoteText").Text);
        Assert.False(Holds(EverythingInTheWindow(window), Secret));

        // A login's card has no label; a note without a text has no Show and no Copy text.
        Border login = view.GetVisualDescendants().OfType<Border>().Single(b => b.DataContext is LoginItemViewModel);
        Assert.Empty(login.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ItemKind"));
        Border bare = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("card") && b.DataContext is NoteItemViewModel { Name: "Only a name" });
        Assert.False(HeadlessUi.IsShown(HeadlessUi.Find<Button>(bare, "ShowNoteTextButton")));
        Assert.False(HeadlessUi.IsShown(HeadlessUi.Find<Button>(bare, "CopyNoteTextButton")));

        Button show = HeadlessUi.Find<Button>(card, "ShowNoteTextButton");
        Assert.Equal("Show the text of Codes", AutomationProperties.GetName(show));
        HeadlessUi.Press(window, show);
        HeadlessUi.Settle();
        Assert.Equal(Text, HeadlessUi.Find<TextBlock>(card, "ItemNoteText").Text);
        Assert.Equal("Hide the text of Codes", AutomationProperties.GetName(show));

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(card, "CopyNoteTextButton"));
        HeadlessUi.Settle();
        Assert.Equal(Text, clipboard.LastCopied);
        Assert.Equal("Text copied. Clipboard clears in 30s.", HeadlessUi.Find<TextBlock>(view, "StatusMessage").Text);

        // Nothing but the text block of the card holds the text: no tooltip, no accessible name.
        List<string> texts = EverythingInTheWindow(window);
        texts.Remove(Text); // the text block itself
        texts.Remove(Text); // and what it tells the accessibility interface, which is what it shows
        Assert.False(Holds(texts, Secret));

        vault.Lock();
        HeadlessUi.Settle();
        Assert.False(Holds(EverythingInTheWindow(window), Secret));
        return Task.CompletedTask;
    });

    [Fact]
    public Task DeletingANote_AsksFirst_InTheWordsOfANote() => HeadlessUi.RunAsync(async () =>
    {
        (VaultPageViewModel page, FakeVaultService vault, _) = Page();
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);
        Border card = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("card") && b.DataContext is NoteItemViewModel { Name: "Codes" });

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(card, "DeleteButton"));
        HeadlessUi.Settle();
        Assert.Equal("Delete this note? This cannot be undone.", HeadlessUi.Find<TextBlock>(card, "DeleteQuestion").Text);
        Assert.True(HeadlessUi.IsShown(HeadlessUi.Find<Button>(card, "KeepButton")));
        Assert.True(HeadlessUi.Find<Button>(card, "KeepButton").IsFocused);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(card, "ConfirmDeleteButton"));
        await HeadlessUi.WaitUntilAsync(() => !vault.Stored.Any(i => i.Name == "Codes"), "the note is deleted");
        HeadlessUi.Settle();
        Assert.DoesNotContain("Codes", view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ItemName").Select(t => t.Text));
    });

    [Fact]
    public Task TheEditor_MasksTheText_TakesSeveralLines_AndShowsItOnlyWhenAsked() => HeadlessUi.RunAsync(() =>
    {
        var editor = new NoteEditorViewModel(new NoteItem { Name = "Codes", Notes = Text }, _ => Task.CompletedTask, () => { });
        var view = new NoteEditorView { DataContext = editor };
        Window window = HeadlessUi.Show(view);

        Assert.Equal("Edit note", HeadlessUi.Find<TextBlock>(view, "EditorTitle").Text);
        SecretBox box = HeadlessUi.Find<SecretBox>(view, "NoteTextBox");
        Assert.Equal("Text", AutomationProperties.GetName(box));
        Assert.True(box.AcceptsReturn);
        Assert.False(box.RevealPassword);
        Assert.Equal(new string(SecretBox.DefaultPasswordChar, Text.Length), box.DisplayedText);
        Assert.False(Holds(EverythingInTheWindow(window), Secret));
        Assert.True(HeadlessUi.Find<TextBox>(view, "NameBox").IsFocused);

        // Enter starts a new line in the text; it does not save.
        box.Focus();
        box.CaretIndex = box.Text!.Length;
        HeadlessUi.Settle();
        HeadlessUi.Key(window, PhysicalKey.Enter);
        window.KeyTextInput("FAKE third line");
        HeadlessUi.Settle();
        Assert.EndsWith("\nFAKE third line", editor.Text, StringComparison.Ordinal);
        Assert.StartsWith(Text, editor.Text, StringComparison.Ordinal);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "RevealNoteTextButton"));
        Assert.True(box.RevealPassword);
        Assert.Equal(editor.Text, box.DisplayedText);
        Assert.Equal("Hide", HeadlessUi.Find<Button>(view, "RevealNoteTextButton").Content);

        editor.Text = new string('x', VaultValidation.MaxNotesLength + 1);
        HeadlessUi.Settle();
        Assert.True(HeadlessUi.IsShown(HeadlessUi.Find<TextBlock>(view, "NoteTextError")));
        Assert.False(HeadlessUi.Find<Button>(view, "SaveButton").IsEffectivelyEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AddingANote_ThroughThePage_OpensItsEditor_AndListsIt() => HeadlessUi.RunAsync(async () =>
    {
        using VaultService vault = await UnlockedVaultAsync();
        var page = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        await page.RefreshAsync();
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);
        Assert.Equal(
            "No logins or notes yet. Click “Add login” or “Add note” to create your first entry.",
            HeadlessUi.Find<TextBlock>(view, "EmptyVaultNote").Text);

        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "AddNoteButton"));
        HeadlessUi.Settle();
        Assert.Equal("Add note", HeadlessUi.Find<TextBlock>(view, "EditorTitle").Text);
        Assert.False(HeadlessUi.Find<Button>(view, "AddLoginButton").IsEffectivelyEnabled); // the page behind is out of reach

        HeadlessUi.Type(window, HeadlessUi.Find<TextBox>(view, "NameBox"), "Codes");
        HeadlessUi.Type(window, HeadlessUi.Find<SecretBox>(view, "NoteTextBox"), Secret);
        HeadlessUi.Press(window, HeadlessUi.Find<Button>(view, "SaveButton"));
        await HeadlessUi.WaitUntilAsync(() => !page.IsEditorOpen, "the editor closes");
        HeadlessUi.Settle();

        Assert.Equal(Secret, Assert.IsType<NoteItem>(Assert.Single(vault.Items)).Notes);
        Assert.Equal("Codes", HeadlessUi.Find<TextBlock>(view, "ItemName").Text);
        Assert.Equal("Secure note", HeadlessUi.Find<TextBlock>(view, "ItemKind").Text);
        Assert.False(Holds(EverythingInTheWindow(window), Secret));
    });
}
