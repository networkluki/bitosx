using BitosX.Core.Security;
using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>Screen for creating a brand new vault with a master password.</summary>
public sealed partial class CreateVaultViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StrengthText { get; set; } = string.Empty;

    public CreateVaultViewModel(IVaultService vaultService)
    {
        _vaultService = vaultService;
    }

    partial void OnMasterPasswordChanged(string value)
    {
        StrengthText = StrengthDisplay.Rating(value);
    }

    private bool CanCreate()
        => !IsBusy
           && MasterPassword.Length >= VaultService.MinMasterPasswordLength
           && ConfirmPassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        ErrorMessage = null;

        if (!string.Equals(MasterPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "The passwords do not match.";
            return;
        }

        IsBusy = true;
        try
        {
            await _vaultService.CreateVaultAsync(MasterPassword).ConfigureAwait(true);
            // Success transitions the shell via IVaultService.StateChanged; clear sensitive fields.
            MasterPassword = string.Empty;
            ConfirmPassword = string.Empty;
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
}
