namespace BitosX.ApiClient;

/// <summary>
/// The local-only API client for this milestone. Server integration is disabled; no network
/// calls are made and no data ever leaves the device.
/// </summary>
public sealed class LocalOnlyApiClient : IBitosXApiClient
{
    public bool IsServerIntegrationEnabled => false;

    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
