using BitosX.App.Services;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>Editor for adding or editing a login item. Save/cancel are delegated to the host page.</summary>
public sealed partial class LoginEditorViewModel : ViewModelBase
{
    // One URL per line, as the field says and as the web vault reads it. A URL may contain a comma,
    // a semicolon or even a space; splitting on those would break it, or silently turn it into two.
    private static readonly string[] UrlSeparators = ["\r\n", "\n", "\r"];

    private readonly LoginItem? _existing;
    private readonly IPasswordGenerator _generator;
    private readonly GeneratorSettings _generatorSettings;
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

    /// <summary>
    /// The authenticator key as typed: base32, or an <c>otpauth://</c> link (docs/TOTP.md §3). Empty
    /// means no key. It is held only while the editor is open, and is masked unless shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpError))]
    [NotifyPropertyChangedFor(nameof(HasTotpError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string TotpText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpToggleText))]
    public partial bool IsTotpVisible { get; set; }

    /// <summary>What the button next to the authenticator key does when pressed.</summary>
    public string TotpToggleText => IsTotpVisible ? "Hide" : "Show";

    /// <summary>The label of the authenticator key's field.</summary>
    public string TotpLabel { get; } = TotpTexts.FieldLabel;

    /// <summary>The hint under the authenticator key's field.</summary>
    public string TotpHint { get; } = TotpTexts.FieldHint;

    /// <summary>
    /// Why the authenticator key as typed is not one, in the fixed words of docs/TOTP.md §6; or
    /// <see langword="null"/> when it is one, or is empty. Never quotes what was typed. While it is
    /// set, the login cannot be saved.
    /// </summary>
    public string? TotpError => TotpKeyParser.Parse(TotpText).Refusal is { } refusal ? TotpTexts.Text(refusal) : null;

    public bool HasTotpError => TotpError is not null;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string Title => _existing is null ? "Add login" : "Edit login";

    public LoginEditorViewModel(
        LoginItem? existing,
        IPasswordGenerator generator,
        Func<LoginItem, Task> onSave,
        Action onCancel,
        GeneratorSettings? generatorSettings = null)
    {
        _existing = existing;
        _generator = generator;
        _generatorSettings = generatorSettings ?? new GeneratorSettings();
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
            TotpText = existing.Totp is { } key ? TotpKeyParser.ToText(key) : string.Empty;
        }
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        // With the Generator page's settings, so a site's rules are set up once.
        Password = _generator.Generate(_generatorSettings.Current);
        IsPasswordVisible = true;
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand]
    private void ToggleTotpVisibility() => IsTotpVisible = !IsTotpVisible;

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name) && !HasTotpError;

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

            // Empty text is no key: removing the text removes the key.
            Totp = TotpKeyParser.Parse(TotpText).Key,
        };
    }
}
