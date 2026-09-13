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
    public string? PrimaryUrl => Model.Urls.Count > 0 ? Model.Urls[0] : null;
    public bool HasPassword => !string.IsNullOrEmpty(Model.Password);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPassword))]
    [NotifyPropertyChangedFor(nameof(PasswordToggleGlyph))]
    public partial bool IsPasswordVisible { get; set; }

    public string DisplayedPassword => IsPasswordVisible ? (Model.Password ?? string.Empty) : Mask;

    public string PasswordToggleGlyph => IsPasswordVisible ? "Hide" : "Show";
}
