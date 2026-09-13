namespace BitosX.Platform.SecureStorage;

/// <summary>
/// Extension point for future OS-backed secret storage (e.g. Windows DPAPI, macOS Keychain,
/// Linux Secret Service) — for example, to optionally protect a cached vault key at rest.
/// </summary>
/// <remarks>
/// Not implemented in this milestone. <see cref="UnavailableSecureStorage"/> is wired up as a
/// safe default that reports the feature as unavailable rather than silently degrading security.
/// </remarks>
public interface ISecureStorage
{
    /// <summary>Whether OS-backed secure storage is available on the current platform.</summary>
    bool IsAvailable { get; }
}

/// <summary>Default implementation used until platform integrations exist. Always unavailable.</summary>
public sealed class UnavailableSecureStorage : ISecureStorage
{
    public bool IsAvailable => false;
}
