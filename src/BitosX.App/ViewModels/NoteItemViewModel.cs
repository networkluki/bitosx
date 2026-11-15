using BitosX.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BitosX.App.ViewModels;

/// <summary>
/// A secure note in the vault list (docs/NOTES.md §3.2): its name, the label "Secure note", and its
/// text, hidden until it is asked for.
/// </summary>
/// <remarks>
/// The text is as secret as a password. It is never part of an accessible name, a tooltip or a status
/// text; what the card shows of it is <see cref="DisplayedText"/>, which is the mask until the user
/// presses Show. The search does not look at it (<see cref="VaultItemViewModel.Matches"/>).
/// </remarks>
public sealed partial class NoteItemViewModel : VaultItemViewModel
{
    /// <summary>The label that tells a note's card from a login's, on screen and for a screen reader.</summary>
    public const string KindText = "Secure note";

    public NoteItemViewModel(NoteItem model)
    {
        Model = model;
    }

    public NoteItem Model { get; }

    public override VaultItem Item => Model;

    /// <summary>The label of the card: <see cref="KindText"/>.</summary>
    public string KindLabel { get; } = KindText;

    /// <summary>Whether the note has a text. A note with only a name has no Show and no Copy text.</summary>
    public bool HasText => !string.IsNullOrEmpty(Model.Notes);

    /// <summary>Whether the text is shown. Hidden at first, and again whenever the list is built or the vault locks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedText))]
    [NotifyPropertyChangedFor(nameof(TextToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(TextToggleLabel))]
    public partial bool IsTextVisible { get; set; }

    /// <summary>What the card shows of the text: the text itself once asked for, otherwise the mask.</summary>
    public string DisplayedText => IsTextVisible ? (Model.Notes ?? string.Empty) : Mask;

    public string TextToggleGlyph => IsTextVisible ? "Hide" : "Show";

    public string TextToggleLabel => IsTextVisible ? $"Hide the text of {Name}" : $"Show the text of {Name}";

    public string CopyTextLabel => $"Copy the text of {Name}";

    public override string DeleteQuestion => "Delete this note? This cannot be undone.";
}
