using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>Screen for unlocking an existing vault.</summary>
public sealed partial class UnlockViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public UnlockViewModel(IVaultService vaultService)
    {
        _vaultService = vaultService;
    }

    private bool CanUnlock() => !IsBusy && MasterPassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _vaultService.UnlockAsync(MasterPassword).ConfigureAwait(true);
            MasterPassword = string.Empty; // clear on success (state change drives navigation)
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex);
            MasterPassword = string.Empty; // clear the failed attempt
        }
        finally
        {
            IsBusy = false;
        }
    }
}
