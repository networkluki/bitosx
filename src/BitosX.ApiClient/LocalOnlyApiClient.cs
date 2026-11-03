namespace BitosX.ApiClient;

/// <summary>
/// The client of a build, or a run, without a server: no network calls are made and no data
/// leaves the device. Selected with <c>--api-server none</c>.
/// </summary>
public sealed class LocalOnlyApiClient : IBitosXApiClient
{
    private const string Disabled = "Server integration is disabled in this run of BitosX.";

    public bool IsServerIntegrationEnabled => false;

    public ApiServer? Server => null;

    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<SignInResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<AccountTokens> CompleteSecondFactorAsync(string mfaToken, string code, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task LogoutAllAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<AccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<IReadOnlyList<AccountSessionInfo>> GetSessionsAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task RevokeSessionAsync(string accessToken, Guid sessionId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task ChangePasswordAsync(string accessToken, string currentPassword, string newPassword, string? code, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task DeleteAccountAsync(string accessToken, string password, string? code, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<TwoFactorStatus?> GetTwoFactorStatusAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<PasskeyList?> GetPasskeysAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<SyncStatus?> GetSyncStatusAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<SyncEnvelope?> DownloadEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task<SyncStatus> UploadEnvelopeAsync(string accessToken, long baseRevision, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);

    public Task DeleteEnvelopeAsync(string accessToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Disabled);
}
