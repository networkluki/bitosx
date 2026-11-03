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
/// <item><b>Second factor.</b> When the server answers the password with a challenge, only that
/// one-step token is held (memory, 5 minutes) until a code from the authenticator app completes
/// the sign-in; there is no session, and nothing authenticated can be called, in between.</item>
/// <item><b>Tied to the vault.</b> When the vault locks — by hand, by inactivity, by the
/// workstation locking, or because a fault locked it — the account is signed out as well: the
/// tokens are dropped at once, and the server is told, best effort, refreshing first when the
/// access token has expired (an inactivity lock always comes after it has). Closing BitosX does
/// the same, with a short wait. An unattended machine then holds neither the vault nor the account.</item>
/// <item><b>A generation per session.</b> Every sign-out, lock and new sign-in starts a new
/// generation. A sign-in, second-factor step or refresh that completes for an older generation —
/// because the vault locked or the user signed out while it was in flight — does not bring tokens
/// back: they are discarded and the server is told to end that session.</item>
/// <item><b>One refresh at a time.</b> The server treats a refresh token presented twice as theft
/// and ends the whole session, so a refresh is never run concurrently: callers that find the access
/// token about to expire wait for the one refresh in flight.</item>
/// <item><b>A 401 gets one refresh.</b> An authenticated call answered with 401 refreshes once and
/// is retried once; if the refresh is refused, or the retry is answered with 401 again, the session
/// is over: the tokens are dropped and the user signs in again. A 403 on a password-confirmed call
/// is not a sign-out.</item>
/// <item><b>Zero-knowledge.</b> Nothing here touches the vault service beyond its lock state. The
/// sync calls pass the encrypted envelope through as the bytes they are given.</item>
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
    private SecondFactorChallenge? _challenge;
    private long _generation;
    private Guid? _currentSessionId;
    private Guid? _accountId;
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

    public ApiServer? Server => _api.Server;

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

    public bool IsAwaitingSecondFactor
    {
        get
        {
            lock (_stateLock)
            {
                return _challenge is not null && _challenge.ExpiresAt > _clock.GetUtcNow();
            }
        }
    }

    public async Task<SignInOutcome> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        EnsureAvailable();

        Forget("signing in again");
        long generation = CurrentGeneration();
        SignInResult result = await _api.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        if (result.Challenge is { } challenge)
        {
            // No session yet: only the one-step challenge token is kept, until a code completes it.
            bool kept;
            lock (_stateLock)
            {
                kept = _generation == generation && _vault.State == VaultState.Unlocked;
                if (kept)
                {
                    _challenge = challenge;
                }
            }

            if (!kept)
            {
                throw new OperationCanceledException("The vault locked while signing in.");
            }

            ApiLog.SecondFactorRequired(_logger);
            return SignInOutcome.SecondFactorRequired;
        }

        Adopt(result.Tokens ?? throw new ApiException(ApiFailure.Unexpected, 200), generation);
        return SignInOutcome.SignedIn;
    }

    public async Task CompleteSignInAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        EnsureAvailable();

        SecondFactorChallenge? challenge;
        long generation;
        lock (_stateLock)
        {
            challenge = _challenge is not null && _challenge.ExpiresAt > _clock.GetUtcNow() ? _challenge : null;
            generation = _generation;
        }

        if (challenge is null)
        {
            CancelSignIn();
            throw new ApiException(ApiFailure.Unauthenticated, 401); // nothing to complete: start over with the password
        }

        // A refused code (401) leaves the challenge in place: the server allows a few tries before it dies,
        // and the user can always start over. Any other failure also keeps it; nothing is half-adopted.
        AccountTokens tokens = await _api.CompleteSecondFactorAsync(challenge.MfaToken, code, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (_generation == generation)
            {
                _challenge = null;
            }
        }

        Adopt(tokens, generation);
    }

    public void CancelSignIn()
    {
        lock (_stateLock)
        {
            if (_challenge is not null)
            {
                _challenge = null;
                _generation++; // a second-factor step still in flight completes for nobody
            }
        }
    }

    /// <summary>Holds <paramref name="tokens"/> — unless the session they were asked for has ended meanwhile; then the server is told to end them too.</summary>
    private void Adopt(AccountTokens tokens, long generation)
    {
        bool adopted;
        lock (_stateLock)
        {
            adopted = _generation == generation && _vault.State == VaultState.Unlocked && !_disposed;
            if (adopted)
            {
                _tokens = tokens;
                _challenge = null;
            }
        }

        if (!adopted)
        {
            // The vault locked (or the user signed out) while the server was answering: these tokens
            // belong to nobody. They are not kept, and the session they open is ended.
            _ = EndOnServerAsync(tokens, CancellationToken.None);
            throw new OperationCanceledException("The vault locked while signing in.");
        }

        ApiLog.SignedIn(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AccountProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        (AccountProfile profile, long generation) = await AuthenticatedAsync((token, ct) => _api.GetProfileAsync(token, ct), cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (_generation == generation)
            {
                _accountId = profile.AccountId;
            }
        }

        return profile;
    }

    public async Task<Guid> GetAccountIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_tokens is not null && _accountId is { } known)
            {
                return known;
            }
        }

        return (await GetProfileAsync(cancellationToken).ConfigureAwait(false)).AccountId;
    }

    public async Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<AccountSessionInfo> sessions, long generation) =
            await AuthenticatedAsync((token, ct) => _api.GetSessionsAsync(token, ct), cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (_generation == generation)
            {
                _currentSessionId = sessions.FirstOrDefault(s => s.Current)?.Id;
            }
        }

        return sessions;
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        (_, long generation) = await AuthenticatedAsync(async (token, ct) =>
        {
            await _api.RevokeSessionAsync(token, sessionId, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        bool wasThisOne;
        lock (_stateLock)
        {
            wasThisOne = _generation == generation && _currentSessionId == sessionId;
        }

        if (wasThisOne)
        {
            // The server has ended this session; its tokens are of no use any more.
            Forget("this session was ended");
        }
    }

    public async Task<TwoFactorStatus?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default)
        => (await AuthenticatedAsync((token, ct) => _api.GetTwoFactorStatusAsync(token, ct), cancellationToken).ConfigureAwait(false)).Result;

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        await AuthenticatedAsync(async (token, ct) =>
        {
            await _api.ChangePasswordAsync(token, currentPassword, newPassword, code, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(string password, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        await AuthenticatedAsync(async (token, ct) =>
        {
            await _api.DeleteAccountAsync(token, password, code, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        Forget("the account was deleted");
    }

    public async Task<SyncStatus?> GetSyncStatusAsync(CancellationToken cancellationToken = default)
        => (await AuthenticatedAsync((token, ct) => _api.GetSyncStatusAsync(token, ct), cancellationToken).ConfigureAwait(false)).Result;

    public async Task<SyncEnvelope?> DownloadEnvelopeAsync(CancellationToken cancellationToken = default)
        => (await AuthenticatedAsync((token, ct) => _api.DownloadEnvelopeAsync(token, ct), cancellationToken).ConfigureAwait(false)).Result;

    public async Task<SyncStatus> UploadEnvelopeAsync(long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
        => (await AuthenticatedAsync((token, ct) => _api.UploadEnvelopeAsync(token, baseRevision, envelope, ct), cancellationToken).ConfigureAwait(false)).Result;

    public async Task DeleteEnvelopeAsync(CancellationToken cancellationToken = default)
        => await AuthenticatedAsync(async (token, ct) =>
        {
            await _api.DeleteEnvelopeAsync(token, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? accessToken = await TryGetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        bool told = false;
        try
        {
            if (accessToken is not null)
            {
                await _api.LogoutAsync(accessToken, cancellationToken).ConfigureAwait(false);
                told = true;
            }
        }
        catch (ApiException ex)
        {
            told = ex.Failure == ApiFailure.Unauthenticated; // the server had already ended it
        }
        finally
        {
            // Whatever the server said — or whether it could be reached — the tokens are gone here.
            Forget("signed out");
        }

        return told;
    }

    public async Task SignOutEverywhereAsync(CancellationToken cancellationToken = default)
    {
        // Through the authenticated path, so an expired access token is refreshed (and a 401 refreshed
        // once) first. If the server cannot be told, this throws and the session is kept: nothing
        // claims that other devices were signed out when they were not.
        await AuthenticatedAsync(async (token, ct) =>
        {
            await _api.LogoutAllAsync(token, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        Forget("signed out everywhere");
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        AccountTokens? held = TakeTokens();
        Forget("BitosX is closing");
        if (held is not null)
        {
            await EndOnServerAsync(held, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> with a usable access token, and returns its result with the
    /// generation it ran in. A 401 refreshes once and retries once; a second 401 ends the session.
    /// </summary>
    private async Task<(T Result, long Generation)> AuthenticatedAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        (string accessToken, long generation) = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await call(accessToken, cancellationToken).ConfigureAwait(false), generation);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.Unauthenticated)
        {
            // Below: one refresh, then one retry (the server may have rotated its signing key, or
            // the clock here may be off). A 401 means the server did not act on the request.
        }

        (string retryToken, long retryGeneration) = await RefreshAsync(accessToken, cancellationToken).ConfigureAwait(false);
        try
        {
            return (await call(retryToken, cancellationToken).ConfigureAwait(false), retryGeneration);
        }
        catch (ApiException ex) when (ex.Failure == ApiFailure.Unauthenticated)
        {
            ForgetIf(retryGeneration, "the server no longer accepts the session");
            throw;
        }
    }

    /// <summary>The access token, refreshed first if it is about to expire. Throws when not signed in.</summary>
    private async Task<(string Token, long Generation)> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetFreshAccessToken(rejected: null) is { } fresh)
        {
            return fresh;
        }

        return await RefreshAsync(rejected: null, cancellationToken).ConfigureAwait(false);
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
            return (await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)).Token;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>The held access token if it has more than <see cref="RefreshMargin"/> left and is not <paramref name="rejected"/>.</summary>
    private (string Token, long Generation)? TryGetFreshAccessToken(string? rejected)
    {
        lock (_stateLock)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (_tokens is null || _tokens.RefreshTokenExpiresAt <= now)
            {
                return null;
            }

            bool fresh = _tokens.AccessTokenExpiresAt - now > RefreshMargin
                && !string.Equals(_tokens.AccessToken, rejected, StringComparison.Ordinal);
            return fresh ? (_tokens.AccessToken, _generation) : null;
        }
    }

    /// <summary>
    /// One refresh at a time: whoever else arrives meanwhile waits for it and uses its result.
    /// <paramref name="rejected"/> is an access token the server just answered with 401: a token that
    /// is still the one held is refreshed even though it has not expired.
    /// </summary>
    private async Task<(string Token, long Generation)> RefreshAsync(string? rejected, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The refresh that was in flight may already have done the work.
            if (TryGetFreshAccessToken(rejected) is { } fresh)
            {
                return fresh;
            }

            string refreshToken;
            long generation;
            bool expired = false;
            lock (_stateLock)
            {
                if (_tokens is null || _tokens.RefreshTokenExpiresAt <= _clock.GetUtcNow())
                {
                    expired = _tokens is not null;
                    refreshToken = string.Empty;
                }
                else
                {
                    refreshToken = _tokens.RefreshToken;
                }

                generation = _generation;
            }

            if (refreshToken.Length == 0)
            {
                if (expired)
                {
                    // Through Forget, like every other way a session ends: logged, and Changed raised.
                    Forget("the refresh token expired");
                }

                throw new ApiException(ApiFailure.Unauthenticated, 401);
            }

            AccountTokens renewed;
            try
            {
                renewed = await _api.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Failure is ApiFailure.Unauthenticated or ApiFailure.InvalidRequest)
            {
                // The refresh token is spent either way; the session is over.
                ForgetIf(generation, "the refresh token was not accepted");
                throw;
            }

            bool stored;
            lock (_stateLock)
            {
                stored = _generation == generation && _tokens is not null && _vault.State == VaultState.Unlocked && !_disposed;
                if (stored)
                {
                    _tokens = renewed;
                }
            }

            if (!stored)
            {
                // The session ended (vault locked, signed out) while the server was answering. The new
                // pair is not kept, and the session it belongs to is ended on the server.
                _ = EndOnServerAsync(renewed, CancellationToken.None);
                throw new ApiException(ApiFailure.Unauthenticated, 401);
            }

            ApiLog.TokensRefreshed(_logger);
            return (renewed.AccessToken, generation);
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

    private long CurrentGeneration()
    {
        lock (_stateLock)
        {
            return _generation;
        }
    }

    /// <summary>The held tokens, if they are still usable for telling the server; nothing is changed.</summary>
    private AccountTokens? TakeTokens()
    {
        lock (_stateLock)
        {
            return _tokens is not null && _tokens.RefreshTokenExpiresAt > _clock.GetUtcNow() ? _tokens : null;
        }
    }

    /// <summary>Drops the tokens (and any half-done sign-in) and starts a new generation. Raises <see cref="Changed"/> only if there were tokens.</summary>
    private void Forget(string reason) => ForgetCore(null, reason);

    /// <summary>As <see cref="Forget"/>, but only if the session is still the one of <paramref name="generation"/>: a failure of an older session must not end a newer one.</summary>
    private void ForgetIf(long generation, string reason) => ForgetCore(generation, reason);

    private void ForgetCore(long? generation, string reason)
    {
        bool had;
        lock (_stateLock)
        {
            if (generation is { } expected && expected != _generation)
            {
                return;
            }

            _challenge = null;
            had = _tokens is not null;
            _tokens = null;
            _currentSessionId = null;
            _accountId = null;
            _generation++;
        }

        if (had)
        {
            ApiLog.SignedOut(_logger, reason);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // The vault locking signs the account out too: the tokens are gone locally first — so a server
    // that cannot be reached cannot keep them alive here — and then the server is told, best effort.
    private void OnVaultStateChanged(object? sender, VaultState state)
    {
        if (state == VaultState.Unlocked)
        {
            return;
        }

        // A half-done sign-in dies with the lock too: an unattended machine holds no challenge token
        // either, and a sign-in still in flight completes for nobody (Forget starts a new generation).
        AccountTokens? held = TakeTokens();
        Forget("the vault was locked");
        if (held is not null)
        {
            _ = EndOnServerAsync(held, CancellationToken.None);
        }
    }

    /// <summary>
    /// Ends the session of <paramref name="tokens"/> on the server, best effort: with the access token
    /// while it is fresh, otherwise after spending the refresh token for a new one (the inactivity lock
    /// always comes after the access token expired). Never throws.
    /// </summary>
    private async Task EndOnServerAsync(AccountTokens tokens, CancellationToken cancellationToken)
    {
        try
        {
            string accessToken = tokens.AccessToken;
            if (tokens.AccessTokenExpiresAt - _clock.GetUtcNow() <= RefreshMargin)
            {
                accessToken = (await _api.RefreshAsync(tokens.RefreshToken, cancellationToken).ConfigureAwait(false)).AccessToken;
            }

            await _api.LogoutAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Best effort: the tokens are already gone here, and the server's session expires on its own.
        }
        catch (OperationCanceledException)
        {
            // Closing BitosX does not wait longer than it said it would.
        }
        catch (InvalidOperationException)
        {
            // No server in this run, or the client was disposed while closing (ObjectDisposedException).
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _vault.StateChanged -= OnVaultStateChanged;
        lock (_stateLock)
        {
            _disposed = true;
            _tokens = null;
            _challenge = null;
            _generation++;
        }

        _refreshLock.Dispose();
    }
}
