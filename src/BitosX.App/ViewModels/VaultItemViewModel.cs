using BitosX.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BitosX.App.ViewModels;

/// <summary>
/// What every card of the vault list has, whatever its kind: the name, the favorite mark, whether the
/// search matches it, and the question before it is deleted. <see cref="LoginItemViewModel"/> and
/// <see cref="NoteItemViewModel"/> add what their kind shows.
/// </summary>
/// <remarks>
/// The accessible names made here carry the item's name, never anything secret: a name is shown in
/// the list and found by the search anyway.
/// </remarks>
public abstract partial class VaultItemViewModel : ViewModelBase
{
    /// <summary>What a secret is shown as while it is hidden, whatever its length.</summary>
    internal const string Mask = "••••••••••";

    /// <summary>The item this card shows.</summary>
    public abstract VaultItem Item { get; }

    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public bool Favorite => Item.Favorite;
    public string FavoriteGlyph => Favorite ? "★" : "☆";

    /// <summary>What pressing the star does, in words: for the tooltip and for a screen reader.</summary>
    public string FavoriteLabel => Favorite ? "Remove from favorites" : "Add to favorites";

    // What each action button is called for a screen reader: with the item's name, so that thirty
    // "Edit" buttons can be told apart.
    public string FavoriteDescription => Favorite ? $"Remove {Name} from favorites" : $"Add {Name} to favorites";

    public string EditLabel => $"Edit {Name}";

    public string DeleteLabel => $"Delete {Name}";

    public string KeepLabel => $"Keep {Name}";

    public string ConfirmDeleteLabel => $"Delete {Name} for good";

    /// <summary>The question asked before the item is deleted.</summary>
    public abstract string DeleteQuestion { get; }

    /// <summary>Whether the search matches this item: its card is shown only then.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; internal set; } = true;

    /// <summary>Whether deleting this item has been asked for and is waiting to be confirmed.</summary>
    [ObservableProperty]
    public partial bool IsDeletePending { get; set; }

    /// <summary>
    /// Whether <paramref name="term"/> (trimmed, not empty) finds this item. Every kind is found by a
    /// part of its name, in any letter case; a login also by its username and its addresses.
    /// </summary>
    internal virtual bool Matches(string term) => Name.Contains(term, StringComparison.OrdinalIgnoreCase);
}
