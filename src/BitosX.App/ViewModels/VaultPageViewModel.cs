using System.Globalization;
using BitosX.App.Services;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;
using BitosX.Platform.Clipboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The vault page: search, list, and manage logins and secure notes.</summary>
/// <remarks>
/// Logins with an authenticator key show their current code, which a timer of one second keeps up
/// to date while the vault is unlocked and such a login is listed. Locking — or the page being let go
/// of, which the shell does when the vault locks — stops the timer and clears every code shown, and
/// hides the text of every note that was shown (docs/NOTES.md §3.5).
/// </remarks>
public sealed partial class VaultPageViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan CodeTick = TimeSpan.FromSeconds(1);

    private readonly IVaultService _vaultService;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher? _ui;
    private readonly object _timerLock = new();
    private ITimer? _codeTimer;
    private bool _disposed;
    private readonly IClipboardService _clipboard;
    private readonly IPasswordGenerator _generator;
    private readonly GeneratorSettings _generatorSettings;
    private readonly List<VaultItemViewModel> _all = [];

    /// <summary>
    /// Every login and note of the vault, in the order of the list: what the list builds its cards
    /// from. It changes only when the vault does; a search shows and hides the cards
    /// (<see cref="VaultItemViewModel.IsMatch"/>) instead of building the list again.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<VaultItemViewModel> AllItems { get; private set; } = [];

    /// <summary>The logins and notes the search matches, in the order of the list.</summary>
    public IReadOnlyList<VaultItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatchesText))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The editor of a login, while one is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    [NotifyPropertyChangedFor(nameof(OpenEditor))]
    public partial LoginEditorViewModel? Editor { get; set; }

    /// <summary>The editor of a secure note, while one is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    [NotifyPropertyChangedFor(nameof(OpenEditor))]
    public partial NoteEditorViewModel? NoteEditor { get; set; }

    /// <summary>Whichever editor is open, for the overlay in front of the list; at most one is.</summary>
    public ViewModelBase? OpenEditor => (ViewModelBase?)Editor ?? NoteEditor;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool IsEditorOpen => Editor is not null || NoteEditor is not null;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The vault holds no logins and no notes at all.</summary>
    public bool IsVaultEmpty => _all.Count == 0;

    /// <summary>The words shown while the vault is empty (docs/NOTES.md §3.3).</summary>
    public string EmptyVaultText { get; } = "No logins or notes yet. Click “Add login” or “Add note” to create your first entry.";

    /// <summary>The vault has items, and the search matches none of them.</summary>
    public bool HasNoMatches => _all.Count > 0 && Items.Count == 0;

    public string NoMatchesText => $"No logins or notes match “{SearchText.Trim()}”.";

    /// <summary>Whether a login of the vault has an authenticator key, so that codes are shown.</summary>
    [ObservableProperty]
    public partial bool HasAuthenticatorCodes { get; private set; }

    /// <summary>The note shown with the codes (docs/TOTP.md §4).</summary>
    public string ClockNote { get; } = TotpTexts.ClockNote;

    /// <summary>Whether the timer that keeps the codes up to date is running.</summary>
    public bool IsCodeTimerRunning
    {
        get
        {
            lock (_timerLock)
            {
                return _codeTimer is not null;
            }
        }
    }

    public VaultPageViewModel(
        IVaultService vaultService,
        IClipboardService clipboard,
        IPasswordGenerator generator,
        GeneratorSettings? generatorSettings = null,
        TimeProvider? time = null,
        IUiDispatcher? ui = null)
    {
        _vaultService = vaultService;
        _clipboard = clipboard;
        _generator = generator;
        _generatorSettings = generatorSettings ?? new GeneratorSettings();
        _time = time ?? TimeProvider.System;
        _ui = ui;
        _vaultService.StateChanged += OnVaultStateChanged;
    }

    /// <summary>Names in the order of the user's language (Å, Ä, Ö after Z in Swedish; É with E), ignoring case, as the web vault sorts.</summary>
    private static StringComparer NameOrder => StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);

    public Task RefreshAsync()
    {
        _all.Clear();

        // Logins and notes in one list: favorites first, then by name (docs/NOTES.md §3.3). A card
        // that is built again shows nothing secret until it is asked to.
        foreach (VaultItem item in _vaultService.Items
                     .Where(i => i is LoginItem or NoteItem)
                     .OrderByDescending(i => i.Favorite)
                     .ThenBy(i => i.Name, NameOrder))
        {
            _all.Add(item is NoteItem note ? new NoteItemViewModel(note) : new LoginItemViewModel((LoginItem)item));
        }

        ApplyFilter();
        AllItems = [.. _all];
        HasAuthenticatorCodes = _all.Exists(item => item is LoginItemViewModel { HasTotp: true });
        UpdateCodes();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the current code of every listed login that has a key, and keeps the timer running for
    /// as long as there is one and the vault is unlocked; otherwise stops it and clears the codes.
    /// </summary>
    public void UpdateCodes()
    {
        if (_disposed || _vaultService.State != VaultState.Unlocked || !HasAuthenticatorCodes)
        {
            StopCodes();
            return;
        }

        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (LoginItemViewModel item in _all.OfType<LoginItemViewModel>())
        {
            if (item.Model.Totp is { } key)
            {
                // Only for the cards that are shown; a hidden card shows nothing.
                item.ShowCode(item.IsMatch ? TotpGenerator.Generate(key, now) : null);
            }
        }

        lock (_timerLock)
        {
            _codeTimer ??= _time.CreateTimer(_ => OnCodeTick(), null, CodeTick, CodeTick);
        }
    }

    private void OnCodeTick()
    {
        // The timer calls back on a thread of the pool; the cards change on the UI thread.
        if (_ui is null)
        {
            UpdateCodes();
        }
        else
        {
            _ui.Run(UpdateCodes);
        }
    }

    /// <summary>Stops the timer and clears every code shown.</summary>
    private void StopCodes()
    {
        ITimer? timer;
        lock (_timerLock)
        {
            timer = _codeTimer;
            _codeTimer = null;
        }

        timer?.Dispose();
        foreach (LoginItemViewModel item in _all.OfType<LoginItemViewModel>())
        {
            item.ShowCode(null);
        }
    }

    /// <summary>Hides the text of every note that is shown.</summary>
    private void HideNoteTexts()
    {
        foreach (NoteItemViewModel note in _all.OfType<NoteItemViewModel>())
        {
            note.IsTextVisible = false;
        }
    }

    /// <summary>Stops the codes and hides every note's text: what locking does to the page.</summary>
    private void HideSecrets()
    {
        StopCodes();
        HideNoteTexts();
    }

    private void OnVaultStateChanged(object? sender, VaultState state)
    {
        if (state == VaultState.Unlocked)
        {
            return;
        }

        // Stopped at once, on whatever thread the state changed; the cards are cleared on the UI thread.
        ITimer? timer;
        lock (_timerLock)
        {
            timer = _codeTimer;
            _codeTimer = null;
        }

        timer?.Dispose();
        if (_ui is null)
        {
            HideSecrets();
        }
        else
        {
            _ui.Run(HideSecrets);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vaultService.StateChanged -= OnVaultStateChanged;
        HideSecrets();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        UpdateCodes();
    }

    /// <summary>
    /// Marks each item as matching the search or not, in place. A card whose answer does not change
    /// is not touched, so a keystroke costs a pass over the items and the cards that appear or
    /// disappear, not a new card for every match (which took seconds with a few thousand logins).
    /// A note is matched by its name only, never by its text.
    /// </summary>
    private void ApplyFilter()
    {
        string term = SearchText.Trim();
        var matches = new List<VaultItemViewModel>(_all.Count);
        foreach (VaultItemViewModel item in _all)
        {
            bool match = term.Length == 0 || item.Matches(term);
            item.IsMatch = match;
            if (match)
            {
                matches.Add(item);
            }
        }

        Items = matches;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsVaultEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    [RelayCommand]
    private void AddLogin()
    {
        Editor = new LoginEditorViewModel(
            existing: null,
            _generator,
            onSave: SaveEditorAsync,
            onCancel: CloseEditor,
            _generatorSettings);
    }

    [RelayCommand]
    private void EditLogin(LoginItemViewModel item)
    {
        Editor = new LoginEditorViewModel(
            item.Model,
            _generator,
            onSave: SaveEditorAsync,
            onCancel: CloseEditor,
            _generatorSettings);
    }

    private async Task SaveEditorAsync(LoginItem item)
    {
        if (_vaultService.Items.Any(i => i.Id == item.Id))
        {
            await _vaultService.UpdateItemAsync(item).ConfigureAwait(true);
        }
        else
        {
            await _vaultService.AddLoginAsync(item).ConfigureAwait(true);
        }

        CloseEditor();
        await RefreshAsync().ConfigureAwait(true);
        StatusMessage = "Saved.";
    }

    private void CloseEditor()
    {
        Editor = null;
        NoteEditor = null;
    }

    [RelayCommand]
    private void AddNote() => NoteEditor = new NoteEditorViewModel(existing: null, onSave: SaveNoteEditorAsync, onCancel: CloseEditor);

    [RelayCommand]
    private void EditNote(NoteItemViewModel item)
        => NoteEditor = new NoteEditorViewModel(item.Model, onSave: SaveNoteEditorAsync, onCancel: CloseEditor);

    private async Task SaveNoteEditorAsync(NoteItem item)
    {
        if (_vaultService.Items.Any(i => i.Id == item.Id))
        {
            await _vaultService.UpdateItemAsync(item).ConfigureAwait(true);
        }
        else
        {
            await _vaultService.AddNoteAsync(item).ConfigureAwait(true);
        }

        CloseEditor();
        await RefreshAsync().ConfigureAwait(true);
        StatusMessage = "Saved.";
    }

    /// <summary>Shows the text of a note, or hides it again.</summary>
    [RelayCommand]
    private static void ToggleNoteText(NoteItemViewModel item) => item.IsTextVisible = item.HasText && !item.IsTextVisible;

    /// <summary>Copies the text of a note, exactly as stored, through the clipboard that clears itself.</summary>
    [RelayCommand]
    private async Task CopyNoteTextAsync(NoteItemViewModel item)
    {
        if (item.Model.Notes is { Length: > 0 } text && _vaultService.State == VaultState.Unlocked)
        {
            await CopyAsync(text, "Text").ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Asks for an item to be deleted. Nothing is deleted yet: deleting cannot be undone, so the
    /// item shows a question that has to be answered first.
    /// </summary>
    [RelayCommand]
    private void RequestDelete(VaultItemViewModel item)
    {
        foreach (VaultItemViewModel other in _all)
        {
            other.IsDeletePending = false; // one question at a time
        }

        item.IsDeletePending = true;
    }

    [RelayCommand]
    private static void CancelDelete(VaultItemViewModel item) => item.IsDeletePending = false;

    /// <summary>Deletes a login or a note whose deletion was asked for and confirmed.</summary>
    [RelayCommand]
    private async Task DeleteItemAsync(VaultItemViewModel item)
    {
        // Enforced here and not only by what the view shows: nothing is deleted that was not asked
        // for and then confirmed.
        if (!item.IsDeletePending)
        {
            return;
        }

        try
        {
            await _vaultService.DeleteItemAsync(item.Id).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = "Item deleted.";
        }
        catch (Exception ex)
        {
            // A command that lets an exception escape takes the whole application down.
            StatusMessage = UserFacingError.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(VaultItemViewModel item)
    {
        try
        {
            await _vaultService.ToggleFavoriteAsync(item.Id).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = UserFacingError.Describe(ex);
        }
    }

    [RelayCommand]
    private static void TogglePasswordVisibility(LoginItemViewModel item)
        => item.IsPasswordVisible = !item.IsPasswordVisible;

    [RelayCommand]
    private async Task CopyUsernameAsync(LoginItemViewModel item)
    {
        if (!string.IsNullOrEmpty(item.Username))
        {
            await CopyAsync(item.Username, "Username").ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CopyPasswordAsync(LoginItemViewModel item)
    {
        if (item.Model.Password is { Length: > 0 } password)
        {
            await CopyAsync(password, "Password").ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Copies the code of <paramref name="item"/>'s key as it is at this moment — not the one on
    /// screen, which may be a second old — through the clipboard that clears itself.
    /// </summary>
    [RelayCommand]
    private async Task CopyCodeAsync(LoginItemViewModel item)
    {
        if (item.Model.Totp is not { } key || _vaultService.State != VaultState.Unlocked)
        {
            return;
        }

        string code = TotpGenerator.Generate(key, _time.GetUtcNow()).Code;
        await CopyAsync(code, "Authenticator code").ConfigureAwait(true);
    }

    private async Task CopyAsync(string value, string what)
    {
        try
        {
            await _clipboard.CopySecretAsync(value).ConfigureAwait(true);
            StatusMessage = $"{what} copied. Clipboard clears in {_clipboard.ClearDelay.TotalSeconds:0}s.";
        }
        catch (Exception)
        {
            // The clipboard is shared with every other application and can be busy.
            StatusMessage = $"{what} could not be copied. Please try again.";
        }
    }
}
