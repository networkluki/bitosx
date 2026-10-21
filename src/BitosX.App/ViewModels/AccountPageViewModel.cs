using System.Collections.ObjectModel;
using System.Globalization;
using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>
/// The Account page: sign in to the BitosX server with the account email and password, see the
/// account and its sessions, end sessions, sign out. The account is a different thing from the
/// vault: nothing on this page reads or sends vault contents, and the account password is not
/// the master password. The encrypted sync lives in <see cref="Sync"/>, shown while signed in.
/// </summary>
public sealed partial class AccountPageViewModel : ViewModelBase, IDisposable
{
    /// <summary>The server's policy for the account password, mirrored so the user is told before a round trip.</summary>
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 254;

    private readonly IAccountSession _session;
    private bool _disposed;

    public AccountPageViewModel(IAccountSession session, IVaultSyncService sync)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Sync = new VaultSyncViewModel(sync, session);
        _session.Changed += OnSessionChanged;
        IsAvailable = session.IsAvailable;
        ServerName = session.ServerName ?? string.Empty;
        IsSignedIn = session.IsSignedIn;
        UnavailableNote = "Server integration is disabled for this run of BitosX (--api-server none). "
            + "Nothing is sent to any server.";
    }

    /// <summary>Whether a server is configured at all.</summary>
    public bool IsAvailable { get; }

    public string ServerName { get; }

    public string UnavailableNote { get; }

    /// <summary>The encrypted sync with the server; see <see cref="VaultSyncViewModel"/>.</summary>
    public VaultSyncViewModel Sync { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; private set; }

    public bool IsSignedOut => !IsSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>What the page has to say about the last thing that happened; nothing when all is well.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>A call to the server is in flight. Buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSessionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutEverywhereCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevokeSessionCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string ProfileEmail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileVerified { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileCreated { get; private set; } = string.Empty;

    public ObservableCollection<AccountSessionItemViewModel> Sessions { get; } = [];

    private bool CanSignIn() => !IsBusy && IsAvailable && !IsSignedIn && Email.Length > 0 && Password.Length > 0;

    private bool CanAct() => !IsBusy && IsAvailable && IsSignedIn;

    /// <summary>Loads the profile and the sessions when the page is shown while signed in. Never throws.</summary>
    public async Task RefreshAsync()
    {
        if (!_session.IsSignedIn)
        {
            IsSignedIn = false;
            return;
        }

        await RunAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(true);
            return null;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        string email = Email.Trim();
        string password = Password;
        if (email.Length == 0 || email.Length > MaxEmailLength || !email.Contains('@', StringComparison.Ordinal))
        {
            Status = "Enter the email address of your BitosX account.";
            return;
        }

        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            Status = $"The account password is {MinPasswordLength} to {MaxPasswordLength} characters.";
            return;
        }

        await RunAsync(async ct =>
        {
            await _session.SignInAsync(email, password, ct).ConfigureAwait(true);
            await LoadAsync(ct).ConfigureAwait(true);
            return "Signed in.";
        }, context: "sign in").ConfigureAwait(true);

        // The password has done its job; it is not kept on the page whatever the outcome.
        Password = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RefreshSessionsAsync()
        => RunAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(true);
            return null;
        });

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RevokeSessionAsync(AccountSessionItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async ct =>
        {
            await _session.RevokeSessionAsync(item.Id, ct).ConfigureAwait(true);
            if (!_session.IsSignedIn)
            {
                return "Signed out.";
            }

            await LoadSessionsAsync(ct).ConfigureAwait(true);
            return "Session ended.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SignOutAsync()
        => RunAsync(async ct =>
        {
            await _session.SignOutAsync(ct).ConfigureAwait(true);
            return "Signed out.";
        }, context: "sign out");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SignOutEverywhereAsync()
        => RunAsync(async ct =>
        {
            await _session.SignOutEverywhereAsync(ct).ConfigureAwait(true);
            return "Signed out everywhere. Every device has to sign in again.";
        }, context: "sign out");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        AccountProfile profile = await _session.GetProfileAsync(cancellationToken).ConfigureAwait(true);
        ProfileEmail = profile.Email;
        ProfileVerified = profile.EmailVerified ? "Yes" : "No";
        ProfileCreated = When(profile.CreatedAt);
        await LoadSessionsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountSessionInfo> sessions = await _session.GetSessionsAsync(cancellationToken).ConfigureAwait(true);
        Sessions.Clear();
        foreach (AccountSessionInfo s in sessions.OrderByDescending(s => s.Current).ThenByDescending(s => s.LastSeenAt))
        {
            Sessions.Add(new AccountSessionItemViewModel(s));
        }
    }

    /// <summary>
    /// Runs one operation against the server, with the page marked busy meanwhile, and turns what
    /// goes wrong into a sentence for the user. The sentence never contains anything the server said.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task<string?>> operation, string? context = null)
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
            Status = Describe(ex, context);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "The operation was cancelled.";
        }
        finally
        {
            IsBusy = false;
            IsSignedIn = _session.IsSignedIn;
            if (!IsSignedIn)
            {
                ClearAccount();
            }
        }
    }

    internal static string Describe(ApiException ex, string? context) => ex.Failure switch
    {
        ApiFailure.Unauthenticated when context == "sign in" => "Invalid email or password.",
        ApiFailure.Unauthenticated => "The session has ended. Sign in again.",
        ApiFailure.RateLimited when ex.RetryAfter is { } wait =>
            string.Create(CultureInfo.CurrentCulture, $"Too many attempts. Try again in about {Math.Max(1, Math.Ceiling(wait.TotalSeconds))} seconds."),
        ApiFailure.InvalidRequest when context == "sign in" => "The server did not accept the email or password as given.",
        _ => ex.Message,
    };

    private static string When(DateTimeOffset moment)
        => moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void ClearAccount()
    {
        ProfileEmail = string.Empty;
        ProfileVerified = string.Empty;
        ProfileCreated = string.Empty;
        Sessions.Clear();
    }

    // Signed out from elsewhere — the vault locked, a 401, a sign-out everywhere — while this page exists.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        bool signedIn = _session.IsSignedIn;
        if (signedIn == IsSignedIn)
        {
            return;
        }

        IsSignedIn = signedIn;
        if (!signedIn)
        {
            ClearAccount();
            Status ??= "Signed out.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
        Sync.Dispose();
    }
}

/// <summary>One of the account's sessions, as listed on the Account page.</summary>
public sealed class AccountSessionItemViewModel
{
    public AccountSessionItemViewModel(AccountSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = session.Id;
        Current = session.Current;
        Title = session.Current ? "This device" : "Another device or browser";
        Started = "Started " + Local(session.CreatedAt);
        LastSeen = "Last seen " + Local(session.LastSeenAt);
        Expires = "Expires " + Local(session.ExpiresAt);
        ActionLabel = session.Current ? "Sign out" : "End session";
        ActionDescription = session.Current ? "Sign out this device" : "End this session";
    }

    public Guid Id { get; }

    public bool Current { get; }

    public string Title { get; }

    public string Started { get; }

    public string LastSeen { get; }

    public string Expires { get; }

    public string ActionLabel { get; }

    public string ActionDescription { get; }

    private static string Local(DateTimeOffset moment) => moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
