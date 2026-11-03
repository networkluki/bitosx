using System.Collections.ObjectModel;
using System.Globalization;
using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace BitosX.App.ViewModels;

/// <summary>
/// The Account page: sign in to the BitosX server with the account email and password (and, when the
/// account has one, a second-factor code), see the account, its sessions and its two-factor state,
/// end sessions, sign out, change the account password, delete the account. The account is a
/// different thing from the vault: nothing on this page reads or sends vault contents, and the
/// account password is not the master password. The encrypted sync lives in <see cref="Sync"/>,
/// shown while signed in.
/// </summary>
public sealed partial class AccountPageViewModel : ViewModelBase, IDisposable
{
    /// <summary>The server's policy for the account password, mirrored so the user is told before a round trip.</summary>
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;

    /// <summary>The server's limit for an email address.</summary>
    public const int MaxEmailLength = 320;

    /// <summary>Where a forgotten account password is reset: the web vault of the production server.</summary>
    public static readonly Uri ForgotPasswordAddress = new("https://vault.bitosx.se/forgot-password");

    /// <summary>The characters of a recovery code (no 0, 1, i, l, o), as the server issues them.</summary>
    private const string RecoveryCodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly IAccountSession _session;
    private readonly IUiDispatcher _ui;
    private readonly ILinkOpener _links;
    private bool _disposed;

    [ActivatorUtilitiesConstructor]
    public AccountPageViewModel(IAccountSession session, IVaultSyncService sync, IUiDispatcher ui, ILinkOpener links)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        Sync = new VaultSyncViewModel(sync, session, ui);
        _session.Changed += OnSessionChanged;
        IsAvailable = session.IsAvailable;
        ServerName = session.ServerName ?? string.Empty;
        IsProductionServer = session.Server?.IsProduction == true;
        IsSignedIn = session.IsSignedIn;
        UnavailableNote = "Server integration is disabled for this run of BitosX (--api-server none). "
            + "Nothing is sent to any server.";
        TwoFactorNote = IsProductionServer
            ? "Two-factor authentication is set up, and turned off, at vault.bitosx.se. It protects the account sign-in only; your vault master password is not affected."
            : "Two-factor authentication is set up, and turned off, in the web vault of this server. It protects the account sign-in only; your vault master password is not affected.";
    }

    /// <summary>Whether a server is configured at all.</summary>
    public bool IsAvailable { get; }

    public string ServerName { get; }

    /// <summary>
    /// Whether this is the production server. The "Forgot your password?" link goes to vault.bitosx.se,
    /// which resets the account of that server only; for any other server it is not shown.
    /// </summary>
    public bool IsProductionServer { get; }

    public string UnavailableNote { get; }

    public string TwoFactorNote { get; }

    /// <summary>The encrypted sync with the server; see <see cref="VaultSyncViewModel"/>.</summary>
    public VaultSyncViewModel Sync { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand))]
    public partial bool IsSignedIn { get; private set; }

    public bool IsSignedOut => !IsSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>The password was accepted and the server waits for a code from the authenticator app (or a recovery code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnteringCredentials))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCodeCommand))]
    public partial bool IsAwaitingCode { get; private set; }

    /// <summary>The email and password boxes: shown while signed out and not waiting for a code.</summary>
    public bool IsEnteringCredentials => !IsAwaitingCode;

    /// <summary>The second-factor code as typed: 6 to 8 digits from the app, or a recovery code.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    public partial string Code { get; set; } = string.Empty;

    /// <summary>What the page has to say about the last thing that happened; nothing when all is well.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>A call to the server is in flight. Buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSessionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutEverywhereCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevokeSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string ProfileEmail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileVerified { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileCreated { get; private set; } = string.Empty;

    public ObservableCollection<AccountSessionItemViewModel> Sessions { get; } = [];

    // ---- two-factor state (read-only here) ----

    /// <summary>The account's two-factor state as last read; <see cref="TwoFactorState.Unknown"/> until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TwoFactorSummary))]
    [NotifyPropertyChangedFor(nameof(AsksForCode))]
    [NotifyPropertyChangedFor(nameof(CodeHint))]
    public partial TwoFactorState TwoFactor { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TwoFactorSummary))]
    public partial TwoFactorStatus? TwoFactorDetails { get; private set; }

    public string TwoFactorSummary => TwoFactor switch
    {
        TwoFactorState.On when TwoFactorDetails is { } details => string.Create(
            CultureInfo.CurrentCulture,
            $"On{(details.ConfirmedAt is { } since ? " since " + When(since) : string.Empty)}. {details.RecoveryCodesRemaining} recovery code(s) left.")
            + (details.RecoveryCodesRemaining <= 2 ? " Make new recovery codes on the web vault soon." : string.Empty),
        TwoFactorState.On => "On.",
        TwoFactorState.Off => "Off. Signing in asks only for the account password.",
        TwoFactorState.NotAvailable => "Not available on this server.",
        _ => "Could not be read right now.",
    };

    /// <summary>The password-confirmed forms ask for a code when the account has two-factor on — and, to be safe, when that could not be read.</summary>
    public bool AsksForCode => TwoFactor is TwoFactorState.On or TwoFactorState.Unknown;

    public string CodeHint => TwoFactor == TwoFactorState.On
        ? "Code from your authenticator app, or a recovery code"
        : "Code from your authenticator app, if your account has two-factor authentication";

    // ---- change the account password ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    public partial string CurrentAccountPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    public partial string NewAccountPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    public partial string RepeatAccountPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangePasswordCode { get; set; } = string.Empty;

    /// <summary>What the password change had to say.</summary>
    [ObservableProperty]
    public partial string? ChangePasswordStatus { get; private set; }

    // ---- delete the account ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand))]
    public partial string DeletePassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteCode { get; set; } = string.Empty;

    /// <summary>The user's acknowledgement that the account and the vault stored on the server are gone for good.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand))]
    public partial bool DeleteAcknowledged { get; set; }

    [ObservableProperty]
    public partial string? DeleteStatus { get; private set; }

    private bool CanSignIn() => !IsBusy && IsAvailable && !IsSignedIn && !IsAwaitingCode && Email.Length > 0 && Password.Length > 0;

    private bool CanSubmitCode() => !IsBusy && IsAvailable && !IsSignedIn && IsAwaitingCode && Code.Trim().Length > 0;

    private bool CanCancelCode() => !IsBusy && IsAwaitingCode;

    private bool CanAct() => !IsBusy && IsAvailable && IsSignedIn;

    private bool CanChangePassword()
        => CanAct() && CurrentAccountPassword.Length > 0 && NewAccountPassword.Length > 0 && RepeatAccountPassword.Length > 0;

    private bool CanDeleteAccount() => CanAct() && DeletePassword.Length > 0 && DeleteAcknowledged;

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
            SignInOutcome outcome = await _session.SignInAsync(email, password, ct).ConfigureAwait(true);
            if (outcome == SignInOutcome.SecondFactorRequired)
            {
                IsAwaitingCode = true;
                Code = string.Empty;
                return "Enter the code from your authenticator app, or one of your recovery codes.";
            }

            await LoadAsync(ct).ConfigureAwait(true);
            return "Signed in.";
        }, context: "sign in").ConfigureAwait(true);

        // The password has done its job; it is not kept on the page whatever the outcome.
        Password = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSubmitCode))]
    private async Task SubmitCodeAsync()
    {
        string code = Code.Trim();
        if (!LooksLikeACode(code))
        {
            Status = "Enter the 6- to 8-digit code from your authenticator app, or a recovery code (xxxxx-xxxxx).";
            return;
        }

        await RunAsync(async ct =>
        {
            await _session.CompleteSignInAsync(code, ct).ConfigureAwait(true);
            IsAwaitingCode = false;
            await LoadAsync(ct).ConfigureAwait(true);
            return "Signed in.";
        }, context: "code").ConfigureAwait(true);

        // A code is single use either way; it never stays on the page.
        Code = string.Empty;
        if (!_session.IsAwaitingSecondFactor && !IsSignedIn && IsAwaitingCode)
        {
            // The challenge is gone (expired, used up, or the vault locked): back to the password, and say so.
            IsAwaitingCode = false;
            Status = "The sign-in step has expired. Sign in again with your email and account password.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelCode))]
    private void CancelCode()
    {
        _session.CancelSignIn();
        Code = string.Empty;
        IsAwaitingCode = false;
        Status = null;
    }

    [RelayCommand]
    private async Task ForgotPasswordAsync()
    {
        if (!IsProductionServer)
        {
            return; // the reset page belongs to the production server; a custom server's address is not guessed
        }

        bool opened = await _links.OpenAsync(ForgotPasswordAddress).ConfigureAwait(true);
        Status = opened
            ? "The password reset page opened in your browser. A reset changes the account password only; your vault master password is not affected."
            : "The browser could not be opened. Go to vault.bitosx.se/forgot-password to reset the account password.";
    }

    /// <summary>
    /// 6 to 8 digits (the server's TOTP length), or a recovery code: 10 characters of the recovery
    /// alphabet, with an optional hyphen or space after the fifth, in any case. The server decides;
    /// this only saves a round trip.
    /// </summary>
    public static bool LooksLikeACode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length is >= 6 and <= 8 && code.All(char.IsAsciiDigit))
        {
            return true;
        }

        string compact = code.Length == 11 && code[5] is '-' or ' ' ? code.Remove(5, 1) : code;
        return compact.Length == 10
            && compact.All(c => RecoveryCodeAlphabet.Contains(char.ToLowerInvariant(c), StringComparison.Ordinal));
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
            // Ending the current session signs out here as well (the session knows which one it is).
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
            bool told = await _session.SignOutAsync(ct).ConfigureAwait(true);
            return told
                ? "Signed out."
                : "Signed out on this device. The server could not be reached, so it still lists this session until it expires; you can end it from another device.";
        }, context: "sign out");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SignOutEverywhereAsync()
        => RunAsync(async ct =>
        {
            try
            {
                await _session.SignOutEverywhereAsync(ct).ConfigureAwait(true);
            }
            catch (ApiException ex)
            {
                // Never claim more than happened: the server was not told, so nobody else was signed out.
                return _session.IsSignedIn
                    ? Describe(ex, "sign out everywhere") + " No session was ended, and you are still signed in. Try again."
                    : "This device is signed out, but the server did not confirm that the other sessions ended. Sign in again and use “Sign out everywhere” once more.";
            }

            return "Signed out everywhere. Every device has to sign in again.";
        }, context: "sign out");

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private async Task ChangePasswordAsync()
    {
        string current = CurrentAccountPassword;
        string next = NewAccountPassword;
        string repeat = RepeatAccountPassword;
        string code = ChangePasswordCode.Trim();
        try
        {
            ChangePasswordStatus = null;
            if (next.Length < MinPasswordLength || next.Length > MaxPasswordLength)
            {
                ChangePasswordStatus = $"The new account password must be {MinPasswordLength} to {MaxPasswordLength} characters.";
                return;
            }

            if (!string.Equals(next, repeat, StringComparison.Ordinal))
            {
                ChangePasswordStatus = "The new account password and its repetition do not match.";
                return;
            }

            if (!CheckCode(code, out string? problem))
            {
                ChangePasswordStatus = problem;
                return;
            }

            string? result = null;
            await RunAsync(async ct =>
            {
                try
                {
                    await _session.ChangePasswordAsync(current, next, code.Length > 0 ? code : null, ct).ConfigureAwait(true);
                }
                catch (ApiException ex) when (ex.Failure is not ApiFailure.Unauthenticated)
                {
                    result = Describe(ex, "change password");
                    return null;
                }

                result = "The account password was changed. Every other device and browser was signed out; this device stays signed in. "
                    + "Your vault master password is unchanged.";
                await LoadSessionsAsync(ct).ConfigureAwait(true);
                return null;
            }).ConfigureAwait(true);
            ChangePasswordStatus = result ?? Status;
        }
        finally
        {
            // Passwords and a single-use code are never kept on the page, whatever the outcome.
            CurrentAccountPassword = string.Empty;
            NewAccountPassword = string.Empty;
            RepeatAccountPassword = string.Empty;
            ChangePasswordCode = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteAccount))]
    private async Task DeleteAccountAsync()
    {
        string password = DeletePassword;
        string code = DeleteCode.Trim();
        try
        {
            DeleteStatus = null;
            if (!DeleteAcknowledged)
            {
                return; // the commit enforces its own precondition, not only CanExecute
            }

            if (!CheckCode(code, out string? problem))
            {
                DeleteStatus = problem;
                return;
            }

            string? refused = null;
            await RunAsync(async ct =>
            {
                try
                {
                    await _session.DeleteAccountAsync(password, code.Length > 0 ? code : null, ct).ConfigureAwait(true);
                }
                catch (ApiException ex) when (ex.Failure is not ApiFailure.Unauthenticated)
                {
                    refused = Describe(ex, "delete account");
                    return null;
                }

                return "Your BitosX account was deleted, together with the encrypted vault stored on the server. "
                    + "The vault on this device was not touched: it stays here and opens with your master password as before.";
            }).ConfigureAwait(true);
            DeleteStatus = refused;
        }
        finally
        {
            DeletePassword = string.Empty;
            DeleteCode = string.Empty;
            DeleteAcknowledged = false;
        }
    }

    /// <summary>The code is optional unless the account is known to have two-factor on; when given, it must look like one.</summary>
    private bool CheckCode(string code, out string? problem)
    {
        problem = null;
        if (code.Length == 0)
        {
            if (TwoFactor == TwoFactorState.On)
            {
                problem = "This account has two-factor authentication: enter the code from your authenticator app, or a recovery code.";
                return false;
            }

            return true;
        }

        if (!LooksLikeACode(code))
        {
            problem = "Enter the 6- to 8-digit code from your authenticator app, or a recovery code (xxxxx-xxxxx).";
            return false;
        }

        return true;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        AccountProfile profile = await _session.GetProfileAsync(cancellationToken).ConfigureAwait(true);
        ProfileEmail = profile.Email;
        ProfileVerified = profile.EmailVerified ? "Yes" : "No";
        ProfileCreated = When(profile.CreatedAt);
        await LoadSessionsAsync(cancellationToken).ConfigureAwait(true);
        await LoadTwoFactorAsync(cancellationToken).ConfigureAwait(true);
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

    /// <summary>The two-factor state is informative: failing to read it does not fail the page.</summary>
    private async Task LoadTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            TwoFactorStatus? status = await _session.GetTwoFactorStatusAsync(cancellationToken).ConfigureAwait(true);
            TwoFactorDetails = status;
            TwoFactor = status is null ? TwoFactorState.NotAvailable : status.Enabled ? TwoFactorState.On : TwoFactorState.Off;
        }
        catch (ApiException ex) when (ex.Failure is not ApiFailure.Unauthenticated)
        {
            TwoFactorDetails = null;
            TwoFactor = TwoFactorState.Unknown;
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

    public static string Describe(ApiException ex, string? context) => ex.Failure switch
    {
        ApiFailure.Unauthenticated when context == "sign in" => "Invalid email or password.",
        ApiFailure.Unauthenticated when context == "code" => "The code was not accepted. Try the next code from your app, or start over with your password.",

        // Only the second step is about two-factor. A 503 on the password step is the server's
        // capacity (or a proxy in front of it), not two-factor.
        ApiFailure.Unavailable when context == "code" => "Two-factor sign-in is temporarily unavailable on the server. Try again shortly.",
        ApiFailure.NotFound when context is "sign in" or "code" => "Signing in is not available on this server.",
        ApiFailure.Unauthenticated => "The session has ended. Sign in again.",
        ApiFailure.RateLimited when ex.RetryAfter is { } wait =>
            string.Create(CultureInfo.CurrentCulture, $"Too many attempts. Try again in about {Math.Max(1, Math.Ceiling(wait.TotalSeconds))} seconds."),
        ApiFailure.InvalidRequest when context == "sign in" => "The server did not accept the email or password as given.",
        ApiFailure.InvalidRequest when context == "change password" =>
            $"The server did not accept the new account password. It must be {MinPasswordLength} to {MaxPasswordLength} characters. Nothing was changed.",
        ApiFailure.Forbidden when context == "change password" =>
            "The current account password or the code was not accepted. Nothing was changed, and you are still signed in.",
        ApiFailure.Forbidden when context == "delete account" =>
            "The account password or the code was not accepted. The account was not deleted, and you are still signed in.",
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
        TwoFactor = TwoFactorState.Unknown;
        TwoFactorDetails = null;
        ChangePasswordStatus = null;
        CurrentAccountPassword = string.Empty;
        NewAccountPassword = string.Empty;
        RepeatAccountPassword = string.Empty;
        ChangePasswordCode = string.Empty;
        DeletePassword = string.Empty;
        DeleteCode = string.Empty;
        DeleteAcknowledged = false;
    }

    // Signed in or out from elsewhere — the vault locked, a 401, a sign-out everywhere — while this
    // page exists. The session may say so from any thread; the page changes on the UI thread.
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
        if (!signedIn)
        {
            ClearAccount();
            Status ??= "Signed out.";
        }

        if (signedIn)
        {
            IsAwaitingCode = false;
            Code = string.Empty;
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

/// <summary>What the page knows about the account's two-factor authentication.</summary>
public enum TwoFactorState
{
    /// <summary>Not read yet, or the server could not be asked.</summary>
    Unknown,

    /// <summary>The server does not offer two-factor authentication.</summary>
    NotAvailable,

    Off,

    On,
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
        ActionDescription = session.Current
            ? "Sign out this device"
            : "End the session of another device or browser, last seen " + Local(session.LastSeenAt);
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
