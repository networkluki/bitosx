using System.Collections.ObjectModel;
using System.Globalization;
using BitosX.App.Services;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using BitosX.Core.Services;
using BitosX.Platform.Clipboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The vault page: search, list, and manage login items.</summary>
public sealed partial class VaultPageViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;
    private readonly IClipboardService _clipboard;
    private readonly IPasswordGenerator _generator;
    private readonly GeneratorSettings _generatorSettings;
    private readonly List<LoginItemViewModel> _all = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsVaultEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial ObservableCollection<LoginItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatchesText))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    public partial LoginEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool IsEditorOpen => Editor is not null;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The vault holds no logins at all.</summary>
    public bool IsVaultEmpty => _all.Count == 0;

    /// <summary>The vault has logins, and the search matches none of them.</summary>
    public bool HasNoMatches => _all.Count > 0 && Items.Count == 0;

    public string NoMatchesText => $"No logins match “{SearchText.Trim()}”.";

    public VaultPageViewModel(IVaultService vaultService, IClipboardService clipboard, IPasswordGenerator generator, GeneratorSettings? generatorSettings = null)
    {
        _vaultService = vaultService;
        _clipboard = clipboard;
        _generator = generator;
        _generatorSettings = generatorSettings ?? new GeneratorSettings();
    }

    /// <summary>Names in the order of the user's language (Å, Ä, Ö after Z in Swedish; É with E), ignoring case, as the web vault sorts.</summary>
    private static StringComparer NameOrder => StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);

    public Task RefreshAsync()
    {
        _all.Clear();
        foreach (LoginItem login in _vaultService.Items.OfType<LoginItem>()
                     .OrderByDescending(i => i.Favorite)
                     .ThenBy(i => i.Name, NameOrder))
        {
            _all.Add(new LoginItemViewModel(login));
        }

        ApplyFilter();
        return Task.CompletedTask;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<LoginItemViewModel> filtered = _all;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            filtered = _all.Where(i =>
                i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (i.Username?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || i.Model.Urls.Any(u => u.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        Items = [.. filtered];
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

    private void CloseEditor() => Editor = null;

    /// <summary>
    /// Asks for an item to be deleted. Nothing is deleted yet: deleting cannot be undone, so the
    /// item shows a question that has to be answered first.
    /// </summary>
    [RelayCommand]
    private void RequestDelete(LoginItemViewModel item)
    {
        foreach (LoginItemViewModel other in _all)
        {
            other.IsDeletePending = false; // one question at a time
        }

        item.IsDeletePending = true;
    }

    [RelayCommand]
    private static void CancelDelete(LoginItemViewModel item) => item.IsDeletePending = false;

    [RelayCommand]
    private async Task DeleteLoginAsync(LoginItemViewModel item)
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
    private async Task ToggleFavoriteAsync(LoginItemViewModel item)
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
