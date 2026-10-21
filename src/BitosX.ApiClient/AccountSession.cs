using BitosX.Core.Services;
using Microsoft.Extensions.Logging;

namespace BitosX.ApiClient;

/// <summary>
/// The account session of the running application: the tokens of the signed-in account, held in
/// memory only, and the calls made with them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Memory only.</b> Nothing is written to disk. Closing BitosX, locking the vault, or
/// signing out forgets the tokens; the next run signs in again. OS-protected storage for the
/// refresh token is a later milestone (<c>ISecureStorage</c>).</item>
/// <item><b>Tied to the vault.</b> When the vault locks — by hand, by inactivity, by the
/// workstation locking, or because a fault locked it — the account is signed out as well: the
/// server is told, best effort, and the tokens are dropped either way. An unattended machine
/// then holds neither the vault nor the account.</item>
/// <item><b>One refresh at a time.</b> The server treats a refresh token presented twice as theft
/// and ends the whole session, so a refresh is never run concurrently: callers that find the access
/// token about to expire wait for the one refresh in flight.</item>
/// <item><b>A 401 ends the session.</b> The server never says why; the tokens are dropped and the
/// user signs in again.</item>
/// <item><b>Zero-knowledge.</b> Nothing here touches the vault service beyond its lock state.</item>
/// </list>
/// Managed strings cannot be wiped reliably (see docs/SECURITY_MODEL.md); the tokens are
/// short-lived and dropped as early as possible, which is what can be done.
/// </remarks>
public sealed class AccountSession : IAccountSession, IDisposable
{
    /// <summary>An access token with less than this left is refreshed before it is used.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly IBitosXApiClient _api;
    private readonly IVaultService _vault;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccountSession> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _stateLock = new();
    private AccountTokens? _tokens;
    private bool _disposed;

    public AccountSession(IBitosXApiClient api, IVaultService vault, TimeProvider clock, ILogger<AccountSession> logger)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _vault.StateChanged += OnVaultStateChanged;
    }

    public bool IsAvailable => _api.IsServerIntegrationEnabled;

    public string? ServerName => _api.Server?.DisplayName;

    public bool IsSignedIn
    {
        get
        {
            lock (_stateLock)
            {
                return _tokens is not null && _tokens.RefreshTokenExpiresAt > _clock.GetUtcNow();
            }
        }
    }

    public event EventHandler? Changed;

    public async Task SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        EnsureAvailable();

        Forget("signing in again");
        AccountTokens tokens = await _api.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            _tokens = tokens;
        }

        ApiLog.SignedIn(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<AccountProfile> GetProfileAsync(CancellationToken cancellationToken = default)
        => AuthenticatedAsync((token, ct) => _api.GetProfileAsync(token, ct), cancellationToken);

    public Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
        => AuthenticatedAsync((token, ct) => _api.GetSessionsAsync(token, ct), cancellationToken);

    public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => AuthenticatedAsync(async (token, ct) =>
        {
            await _api.RevokeSessionAsync(token, sessionId, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default)
        => SignOutAsync((token, ct) => _api.LogoutAsync(token, ct), "signed out", cancellationToken);

    public Task SignOutEverywhereAsync(CancellationToken cancellationToken = default)
        => SignOutAsync((token, ct) => _api.LogoutAllAsync(token, ct), "signed out everywhere", cancellationToken);

    private async Task SignOutAsync(Func<string, CancellationToken, Task> tellServer, string reason, CancellationToken cancellationToken)
    {
        string? accessToken = await TryGetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (accessToken is not null)
            {
                await tellServer(accessToken, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever the server said — or whether it could be reached — the tokens are gone here.
            Forget(reason);
        }
    }

    /// <summary>Runs <paramref name="call"/> with a usable access token. A 401 ends the session.</summary>
    private async Task<T> AuthenticatedAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        string accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.Unauthenticated)
        {
            Forget("the server no longer accepts the session");
            throw;
        }
    }

    /// <summary>The access token, refreshed first if it is about to expire. Throws when not signed in.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetFreshAccessToken() is { } fresh)
        {
            return fresh;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As <see cref="GetAccessTokenAsync"/>, but <see langword="null"/> instead of an exception when there is no session or the refresh fails.</summary>
    private async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!IsSignedIn)
        {
            return null;
        }

        try
        {
            return await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    private string? TryGetFreshAccessToken()
    {
        lock (_stateLock)
        {
            if (_tokens is null || _tokens.RefreshTokenExpiresAt <= _clock.GetUtcNow())
            {
                return null;
            }

            return _tokens.AccessTokenExpiresAt - _clock.GetUtcNow() > RefreshMargin ? _tokens.AccessToken : null;
        }
    }

    /// <summary>One refresh at a time: whoever else arrives meanwhile waits for it and uses its result.</summary>
    private async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The refresh that was in flight may already have done the work.
            if (TryGetFreshAccessToken() is { } fresh)
            {
                return fresh;
            }

            string refreshToken;
            lock (_stateLock)
            {
                if (_tokens is null || _tokens.RefreshTokenExpiresAt <= _clock.GetUtcNow())
                {
                    _tokens = null;
                    throw new ApiException(ApiFailure.Unauthenticated, 401);
                }

                refreshToken = _tokens.RefreshToken;
            }

            AccountTokens renewed;
            try
            {
                renewed = await _api.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Failure is ApiFailure.Unauthenticated or ApiFailure.InvalidRequest)
            {
                // The refresh token is spent either way; the session is over.
                Forget("the refresh token was not accepted");
                throw;
            }

            lock (_stateLock)
            {
                _tokens = renewed;
            }

            ApiLog.TokensRefreshed(_logger);
            return renewed.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_api.IsServerIntegrationEnabled)
        {
            throw new InvalidOperationException("Server integration is disabled in this run of BitosX.");
        }
    }

    /// <summary>Drops the tokens. Raises <see cref="Changed"/> only if there were any.</summary>
    private void Forget(string reason)
    {
        bool had;
        lock (_stateLock)
        {
            had = _tokens is not null;
            _tokens = null;
        }

        if (had)
        {
            ApiLog.SignedOut(_logger, reason);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // The vault locking signs the account out too: the server is told, best effort, after the
    // tokens are already gone locally — so a server that cannot be reached cannot keep them alive.
    private void OnVaultStateChanged(object? sender, VaultState state)
    {
        if (state == VaultState.Unlocked || !IsSignedIn)
        {
            return;
        }

        string? accessToken;
        lock (_stateLock)
        {
            accessToken = _tokens?.AccessTokenExpiresAt > _clock.GetUtcNow() ? _tokens.AccessToken : null;
        }

        Forget("the vault was locked");
        if (accessToken is not null)
        {
            _ = TellServerAsync(accessToken);
        }
    }

    private async Task TellServerAsync(string accessToken)
    {
        try
        {
            await _api.LogoutAsync(accessToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Best effort: the tokens are already gone here, and the server's session expires on its own.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vault.StateChanged -= OnVaultStateChanged;
        lock (_stateLock)
        {
            _tokens = null;
        }

        _refreshLock.Dispose();
    }
}
