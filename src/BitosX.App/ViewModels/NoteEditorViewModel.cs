using BitosX.Core.Domain;
using BitosX.Core.Validation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>
/// Editor for adding or editing a secure note (docs/NOTES.md §3.1): a name, a text and the favorite
/// mark. Save and cancel are delegated to the host page.
/// </summary>
/// <remarks>
/// The text is masked until Show is pressed, for a new note as for an existing one, and the box tells
/// the accessibility interface only what it shows (SecretBox). Nothing remembers the choice. No
/// message of the editor quotes the text.
/// </remarks>
public sealed partial class NoteEditorViewModel : ViewModelBase
{
    /// <summary>What the editor says while the text is longer than a note's text may be.</summary>
    public const string TextTooLongMessage = "The text is longer than 16384 characters.";

    private readonly NoteItem? _existing;
    private readonly Func<NoteItem, Task> _onSave;
    private readonly Action _onCancel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The note's text, exactly as typed. Held only while the editor is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextError))]
    [NotifyPropertyChangedFor(nameof(HasTextError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Favorite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextToggleText))]
    public partial bool IsTextVisible { get; set; }

    /// <summary>What the button next to the text does when pressed.</summary>
    public string TextToggleText => IsTextVisible ? "Hide" : "Show";

    /// <summary>Why the text cannot be saved, in a fixed text that never quotes it; or <see langword="null"/>.</summary>
    public string? TextError => Text.Length > VaultValidation.MaxNotesLength ? TextTooLongMessage : null;

    public bool HasTextError => TextError is not null;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    public string Title => _existing is null ? "Add note" : "Edit note";

    public NoteEditorViewModel(NoteItem? existing, Func<NoteItem, Task> onSave, Action onCancel)
    {
        _existing = existing;
        _onSave = onSave;
        _onCancel = onCancel;

        if (existing is not null)
        {
            Name = existing.Name;
            Text = existing.Notes ?? string.Empty;
            Favorite = existing.Favorite;
        }
    }

    [RelayCommand]
    private void ToggleTextVisibility() => IsTextVisible = !IsTextVisible;

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name) && !HasTextError;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _onSave(BuildItem()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The messages UserFacingError gives are fixed texts or the vault's validation messages,
            // none of which quotes a value.
            ErrorMessage = UserFacingError.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onCancel();

    private NoteItem BuildItem() => new()
    {
        Id = _existing?.Id ?? Guid.NewGuid(),
        CreatedAt = _existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Name = Name.Trim(),

        // Exactly as typed; a text of nothing but white space is no text, as a login's notes.
        Notes = string.IsNullOrWhiteSpace(Text) ? null : Text,
        Favorite = Favorite,
    };
}
