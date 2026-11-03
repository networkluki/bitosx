using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>Screen for unlocking an existing vault.</summary>
public sealed partial class UnlockViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;
    private readonly string? _vaultFilePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The vault file was not where it was when BitosX started (a drive unplugged, the file moved).</summary>
    [ObservableProperty]
    public partial bool IsVaultMissing { get; private set; }

    public UnlockViewModel(IVaultService vaultService, AppLocation? location = null)
    {
        _vaultService = vaultService;
        _vaultFilePath = location?.VaultFilePath;
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
        catch (FileNotFoundException)
        {
            // Not a wrong password: there is nothing to unlock. Say where it was looked for, and offer
            // a way forward that is not a restart — without creating anything over a drive that is
            // merely not connected right now.
            IsVaultMissing = true;
            ErrorMessage = _vaultFilePath is null
                ? "The vault file is no longer there. If it is on a drive that is not connected, connect it and unlock again."
                : $"The vault file is no longer at {_vaultFilePath}. If it is on a drive that is not connected, connect it and unlock again.";
            MasterPassword = string.Empty;
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

    /// <summary>
    /// Looks at the disk again. If the vault file is back, this screen stays; if it is still gone,
    /// BitosX shows the screen for creating a vault (nothing is created until the user does so).
    /// </summary>
    [RelayCommand]
    private async Task CheckAgainAsync()
    {
        try
        {
            VaultState state = await _vaultService.RefreshStateAsync().ConfigureAwait(true);
            if (state == VaultState.Locked)
            {
                IsVaultMissing = false;
                ErrorMessage = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = UserFacingError.Describe(ex);
        }
    }
}
