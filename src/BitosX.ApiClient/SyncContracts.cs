namespace BitosX.ApiClient;

/// <summary>
/// What the server holds for the account, as <c>GET /v1/sync/status</c> reports it: the revision
/// it will accept as a base for the next upload, the SHA-256 of the stored envelope (lowercase
/// hex), its size, and when it was stored. Public metadata about ciphertext; nothing here can
/// open the vault.
/// </summary>
public sealed record SyncStatus(long Revision, string Fingerprint, int SizeBytes, DateTimeOffset UpdatedAt);

/// <summary>
/// The stored envelope as <c>GET /v1/sync/envelope</c> returns it, already checked against the
/// fingerprint and size the server states for it. <see cref="Envelope"/> is the vault file as a
/// device uploaded it: ciphertext and public key-derivation parameters, opened only with the
/// master password on the device.
/// </summary>
public sealed record SyncEnvelope(SyncStatus Status, byte[] Envelope);
