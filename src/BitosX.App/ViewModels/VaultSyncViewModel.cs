using System.Globalization;
using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.App.Services;
using BitosX.Core.Services;
using BitosX.Core.Domain;
using BitosX.Crypto;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>
/// The Sync card of the Account page: sync now; when the server is ahead or the two sides conflict,
/// take the server's vault (after its master password has opened it, and after an explicit
/// acknowledgement that the local vault is replaced), or — in a conflict — replace the server's
/// vault with this device's, again after an acknowledgement. Nothing happens by itself: every
/// transfer is a button, and every replacement is a checkbox and a button.
/// </summary>
public sealed partial class VaultSyncViewModel : ViewModelBase, IDisposable
{
    private readonly IVaultSyncService _sync;
    private readonly IAccountSession _session;
    private readonly IUiDispatcher _ui;
    private readonly RestoreConfirmationState _download = new();
    private SyncDownload? _pendingDownload;
    private bool _disposed;

    public VaultSyncViewModel(IVaultSyncService sync, IAccountSession session, IUiDispatcher ui)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _session.Changed += OnSessionChanged;
        IsSignedIn = session.IsSignedIn;
    }

    /// <summary>Follows the account: the card is shown, and its buttons work, only while signed in.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrepareDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromServerCommand))]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>A call is in flight. Buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrepareDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromServerCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What the server holds and when this device last synced; nothing until the first sync of this run.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>What the last action had to say.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>The server has a newer vault and this device's has not changed: the download is offered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTakeServerVault))]
    [NotifyCanExecuteChangedFor(nameof(PrepareDownloadCommand))]
    public partial bool IsServerAhead { get; private set; }

    /// <summary>Both sides changed (or this device never synced): both ways out are offered, neither taken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTakeServerVault))]
    [NotifyCanExecuteChangedFor(nameof(PrepareDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteServerCommand))]
    public partial bool IsConflict { get; private set; }

    public bool CanTakeServerVault => IsServerAhead || IsConflict;

    /// <summary>The master password of the server's vault, typed to open it. Cleared after every attempt.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrepareDownloadCommand))]
    public partial string DownloadMasterPassword { get; set; } = string.Empty;

    /// <summary>The validated download awaiting acknowledgement; <see langword="null"/> when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadPending))]
    public partial string? DownloadSummary { get; private set; }

    public bool IsDownloadPending => DownloadSummary is not null;

    /// <summary>The user's acknowledgement that the local vault is replaced. Cannot be set while nothing is pending.</summary>
    public bool DownloadAcknowledged
    {
        get => _download.Acknowledged;
        set
        {
            if (_download.Acknowledged == value)
            {
                return;
            }

            _download.SetAcknowledged(value);
            OnPropertyChanged();
            ConfirmDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The user's acknowledgement for the two operations that change the server for every device.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OverwriteServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromServerCommand))]
    public partial bool ServerChangeAcknowledged { get; set; }

    private bool CanAct() => !IsBusy && IsSignedIn;

    private bool CanPrepareDownload() => CanAct() && CanTakeServerVault && DownloadMasterPassword.Length > 0;

    private bool CanConfirmDownload() => CanAct() && _download.CanCommit;

    private bool CanOverwriteServer() => CanAct() && IsConflict && ServerChangeAcknowledged;

    private bool CanRemoveFromServer() => CanAct() && ServerChangeAcknowledged;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SyncNowAsync()
        => RunAsync(async ct =>
        {
            ClearPending();
            SyncResult result = await _sync.SyncAsync(ct).ConfigureAwait(true);
            return Show(result);
        });

    [RelayCommand(CanExecute = nameof(CanPrepareDownload))]
    private async Task PrepareDownloadAsync()
    {
        string password = DownloadMasterPassword;
        try
        {
            await RunAsync(async ct =>
            {
                ClearPending();
                SyncDownload download = await _sync.PrepareDownloadAsync(password, ct).ConfigureAwait(true);
                _pendingDownload = download;
                _download.SetPrepared();
                DownloadSummary = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Server revision {download.Server.Revision} · format v{download.Preview.FormatVersion} · {download.Preview.ItemCount} item(s) · {download.Preview.FileSizeBytes} bytes · stored {When(download.Server.UpdatedAt)}");
                OnPropertyChanged(nameof(DownloadAcknowledged));
                ConfirmDownloadCommand.NotifyCanExecuteChanged();
                return "The server's vault opens with that master password. Confirm below to replace the vault on this device.";
            }).ConfigureAwait(true);
        }
        finally
        {
            // The password has done its job; it is not kept on the page whatever the outcome.
            DownloadMasterPassword = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDownload))]
    private async Task ConfirmDownloadAsync()
    {
        // Commit-side guard (defense in depth): the destructive commit enforces its own invariant and
        // does not rely on CanExecute. Fail closed if the state is not exactly right.
        if (_pendingDownload is null || !_download.CanCommit)
        {
            return;
        }

        SyncDownload download = _pendingDownload;
        await RunAsync(async ct =>
        {
            await _sync.CommitDownloadAsync(download, ct).ConfigureAwait(true);
            // On success the vault locks and the shell shows the Unlock screen; the next unlock uses the
            // downloaded vault's master password.
            return "The vault on this device was replaced with the server's and locked. Unlock it with that vault's master password.";
        }).ConfigureAwait(true);
        ClearPending();
    }

    [RelayCommand]
    private void CancelDownload()
    {
        ClearPending();
        Status = null;
    }

    [RelayCommand(CanExecute = nameof(CanOverwriteServer))]
    private Task OverwriteServerAsync()
        => RunAsync(async ct =>
        {
            ClearPending();
            SyncResult result = await _sync.OverwriteServerAsync(ct).ConfigureAwait(true);
            ServerChangeAcknowledged = false;
            return result.Outcome == SyncOutcome.Uploaded
                ? Show(result, "The server now holds this device's vault. Every other device will be asked which vault to keep.")
                : Show(result);
        });

    [RelayCommand(CanExecute = nameof(CanRemoveFromServer))]
    private Task RemoveFromServerAsync()
        => RunAsync(async ct =>
        {
            ClearPending();
            await _sync.DeleteFromServerAsync(ct).ConfigureAwait(true);
            ServerChangeAcknowledged = false;
            IsServerAhead = false;
            IsConflict = false;
            Summary = "The server holds no vault for this account.";
            return "The vault was removed from the server. This device's vault is unchanged.";
        });

    /// <summary>Puts a sync result on the page: the flags that open the ways out, the summary, and a sentence.</summary>
    private string Show(SyncResult result, string? sentence = null)
    {
        IsServerAhead = result.Outcome == SyncOutcome.ServerAhead;
        IsConflict = result.Outcome == SyncOutcome.Conflict;
        Summary = result.Server is { } server
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Server: revision {server.Revision}, {server.SizeBytes} bytes, stored {When(server.UpdatedAt)}.")
              + (result.LastSyncedAt is { } synced ? string.Create(CultureInfo.CurrentCulture, $" This device last synced {When(synced)}.") : string.Empty)
            : "The server holds no vault for this account.";

        return sentence ?? result.Outcome switch
        {
            SyncOutcome.UpToDate => "Up to date: the server holds the same vault as this device.",
            SyncOutcome.Uploaded => "This device's vault was uploaded.",
            SyncOutcome.ServerAhead => "The server has a newer vault than this device. To take it, enter its master password below; the vault on this device is replaced only after you confirm.",
            SyncOutcome.Conflict => "Both this device and the server have changed since this device last synced, so nothing was changed. "
                + "Choose: take the server's vault (enter its master password below), or replace the server's vault with this device's.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Runs one operation with the card marked busy, and turns what goes wrong into a sentence that
    /// never contains anything the server said and never claims more than happened.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task<string?>> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = null;
        try
        {
            Status = await operation(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            Status = AccountPageViewModel.Describe(ex, "sync");
        }
        catch (VaultAuthenticationException)
        {
            Status = "That master password does not open the server's vault. Nothing was changed.";
        }
        catch (UnsupportedVaultFormatException)
        {
            Status = "The server's vault is not a vault this version of BitosX can open. Nothing was changed.";
        }
        catch (KeyDerivationResourceException ex)
        {
            Status = ex.Message + " Nothing was changed.";
        }
        catch (BitosXCryptographicException)
        {
            Status = "The server's vault could not be validated. Nothing was changed.";
        }
        catch (VaultValidationException)
        {
            Status = "The server's vault has invalid contents. Nothing was changed.";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            Status = "BitosX does not have permission to write its files. Nothing was changed.";
        }
        catch (IOException)
        {
            // Not "nothing was changed": the write that failed may come after an upload or a replace
            // that succeeded. The next sync compares both sides again and says where things stand.
            Status = "A file could not be read or written. Run Sync now again to see where things stand.";
        }
        catch (OperationCanceledException)
        {
            Status = "The operation was cancelled.";
        }
        finally
        {
            IsBusy = false;
            IsSignedIn = _session.IsSignedIn;
        }
    }

    private void ClearPending()
    {
        _pendingDownload = null;
        _download.Reset();
        DownloadSummary = null;
        OnPropertyChanged(nameof(DownloadAcknowledged));
        ConfirmDownloadCommand.NotifyCanExecuteChanged();
    }

    private static string When(DateTimeOffset moment) => moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    // The session may say so from any thread; the card changes on the UI thread.
    private void OnSessionChanged(object? sender, EventArgs e) => _ui.Run(ApplySessionState);

    private void ApplySessionState()
    {
        if (_disposed)
        {
            return;
        }

        bool signedIn = _session.IsSignedIn;
        if (signedIn == IsSignedIn)
        {
            return;
        }

        IsSignedIn = signedIn;

        // Either way, nothing from the previous session carries over: not a prepared download, not an
        // acknowledgement, not what the server held for that account.
        ClearPending();
        IsServerAhead = false;
        IsConflict = false;
        ServerChangeAcknowledged = false;
        DownloadMasterPassword = string.Empty;
        Summary = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
    }
}
