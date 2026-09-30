using BitosX.Core.Generation;
using BitosX.Core.Security;
using BitosX.Platform.Clipboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The password generator page.</summary>
public sealed partial class GeneratorPageViewModel : ViewModelBase
{
    private readonly IPasswordGenerator _generator;
    private readonly IClipboardService _clipboard;

    [ObservableProperty]
    public partial int Length { get; set; } = 20;

    [ObservableProperty]
    public partial bool IncludeLowercase { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeUppercase { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeDigits { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeSymbols { get; set; } = true;

    [ObservableProperty]
    public partial bool ExcludeAmbiguous { get; set; }

    [ObservableProperty]
    public partial string GeneratedPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StrengthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public GeneratorPageViewModel(IPasswordGenerator generator, IClipboardService clipboard)
    {
        _generator = generator;
        _clipboard = clipboard;
        Generate();
    }

    // The password shown always belongs to the settings shown: changing one makes a new password.
    partial void OnLengthChanged(int value) => Generate();

    partial void OnIncludeLowercaseChanged(bool value) => Generate();

    partial void OnIncludeUppercaseChanged(bool value) => Generate();

    partial void OnIncludeDigitsChanged(bool value) => Generate();

    partial void OnIncludeSymbolsChanged(bool value) => Generate();

    partial void OnExcludeAmbiguousChanged(bool value) => Generate();

    [RelayCommand]
    private void Generate()
    {
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var options = new PasswordGenerationOptions
            {
                Length = Length,
                IncludeLowercase = IncludeLowercase,
                IncludeUppercase = IncludeUppercase,
                IncludeDigits = IncludeDigits,
                IncludeSymbols = IncludeSymbols,
                ExcludeAmbiguous = ExcludeAmbiguous,
            };

            GeneratedPassword = _generator.Generate(options);
            StrengthText = StrengthDisplay.Rating(GeneratedPassword);
        }
        catch (Exception ex)
        {
            GeneratedPassword = string.Empty;
            StrengthText = string.Empty;
            ErrorMessage = UserFacingError.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (!string.IsNullOrEmpty(GeneratedPassword))
        {
            try
            {
                await _clipboard.CopySecretAsync(GeneratedPassword).ConfigureAwait(true);
                StatusMessage = $"Copied. Clipboard clears in {_clipboard.ClearDelay.TotalSeconds:0}s.";
            }
            catch (Exception)
            {
                // The clipboard is shared with every other application and can be busy.
                StatusMessage = "The password could not be copied. Please try again.";
            }
        }
    }
}
