using BitosX.Core.Domain;
using BitosX.Core.Generation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>Editor for adding or editing a login item. Save/cancel are delegated to the host page.</summary>
public sealed partial class LoginEditorViewModel : ViewModelBase
{
    private static readonly char[] UrlSeparators = ['\n', '\r', ',', ';', ' ', '\t'];

    private readonly LoginItem? _existing;
    private readonly IPasswordGenerator _generator;
    private readonly Func<LoginItem, Task> _onSave;
    private readonly Action _onCancel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UrlsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Favorite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordToggleText))]
    public partial bool IsPasswordVisible { get; set; }

    /// <summary>What the button next to the password does when pressed.</summary>
    public string PasswordToggleText => IsPasswordVisible ? "Hide" : "Show";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string Title => _existing is null ? "Add login" : "Edit login";

    public LoginEditorViewModel(
        LoginItem? existing,
        IPasswordGenerator generator,
        Func<LoginItem, Task> onSave,
        Action onCancel)
    {
        _existing = existing;
        _generator = generator;
        _onSave = onSave;
        _onCancel = onCancel;

        if (existing is not null)
        {
            Name = existing.Name;
            Username = existing.Username ?? string.Empty;
            Password = existing.Password ?? string.Empty;
            UrlsText = string.Join(Environment.NewLine, existing.Urls);
            Notes = existing.Notes ?? string.Empty;
            Favorite = existing.Favorite;
        }
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        Password = _generator.Generate(new PasswordGenerationOptions { Length = 20 });
        IsPasswordVisible = true;
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            LoginItem item = BuildItem();
            await _onSave(item).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onCancel();

    private LoginItem BuildItem()
    {
        string[] urls = UrlsText
            .Split(UrlSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new LoginItem
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            CreatedAt = _existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Name = Name.Trim(),
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
            Favorite = Favorite,
            Urls = urls,
        };
    }
}
