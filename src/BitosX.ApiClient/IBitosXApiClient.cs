namespace BitosX.ApiClient;

/// <summary>
/// Abstraction for future communication with the BitosX server (sync, account, etc.).
/// </summary>
/// <remarks>
/// This is an abstraction only for this milestone: there is no production API, no server
/// authentication, and no synchronization. When implemented, BitosX must remain zero-knowledge:
/// the master password, plaintext secrets, the raw vault key, and decrypted vault contents
/// MUST never be sent to any server. See docs/SECURITY_MODEL.md.
/// </remarks>
public interface IBitosXApiClient
{
    /// <summary>Whether server integration is configured and enabled. Always <see langword="false"/> for now.</summary>
    bool IsServerIntegrationEnabled { get; }

    /// <summary>Checks server reachability. Returns <see langword="false"/> in local-only mode.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
