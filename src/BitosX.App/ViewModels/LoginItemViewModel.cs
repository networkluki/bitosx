using BitosX.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BitosX.App.ViewModels;

/// <summary>Display wrapper for a <see cref="LoginItem"/> in the vault list.</summary>
public sealed partial class LoginItemViewModel : ViewModelBase
{
    private const string Mask = "••••••••••";

    public LoginItemViewModel(LoginItem model)
    {
        Model = model;
    }

    public LoginItem Model { get; }

    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string? Username => Model.Username;
    public bool Favorite => Model.Favorite;
    public string FavoriteGlyph => Favorite ? "★" : "☆";

    /// <summary>What pressing the star does, in words: for the tooltip and for a screen reader.</summary>
    public string FavoriteLabel => Favorite ? "Remove from favorites" : "Add to favorites";

    // What each action button is called for a screen reader: with the item's name, so that thirty
    // "Edit" buttons can be told apart.
    public string FavoriteDescription => Favorite ? $"Remove {Name} from favorites" : $"Add {Name} to favorites";

    public string PasswordToggleLabel => IsPasswordVisible ? $"Hide the password of {Name}" : $"Show the password of {Name}";

    public string CopyUsernameLabel => $"Copy the username of {Name}";

    public string CopyPasswordLabel => $"Copy the password of {Name}";

    public string EditLabel => $"Edit {Name}";

    public string DeleteLabel => $"Delete {Name}";

    public string KeepLabel => $"Keep {Name}";

    public string ConfirmDeleteLabel => $"Delete {Name} for good";
    public string? PrimaryUrl => Model.Urls.Count > 0 ? Model.Urls[0] : null;
    public bool HasPassword => !string.IsNullOrEmpty(Model.Password);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPassword))]
    [NotifyPropertyChangedFor(nameof(PasswordToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(PasswordToggleLabel))]
    public partial bool IsPasswordVisible { get; set; }

    public string DisplayedPassword => IsPasswordVisible ? (Model.Password ?? string.Empty) : Mask;

    public string PasswordToggleGlyph => IsPasswordVisible ? "Hide" : "Show";

    /// <summary>Whether the search matches this item: its card is shown only then.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; internal set; } = true;

    /// <summary>Whether deleting this item has been asked for and is waiting to be confirmed.</summary>
    [ObservableProperty]
    public partial bool IsDeletePending { get; set; }
}
