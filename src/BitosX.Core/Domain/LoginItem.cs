using System.Text.Json.Serialization;

namespace BitosX.Core.Domain;

/// <summary>
/// A website or application login. The <see cref="Password"/> is plaintext and only ever
/// exists in memory while the vault is unlocked; it is persisted only inside the encrypted
/// vault ciphertext, never on its own.
/// </summary>
public sealed record LoginItem : VaultItem
{
    /// <summary>The account username or email.</summary>
    public string? Username { get; init; }

    /// <summary>The account password (plaintext in memory only).</summary>
    public string? Password { get; init; }

    /// <summary>Associated URLs (e.g. login pages).</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    [JsonIgnore]
    public override VaultItemType ItemType => VaultItemType.Login;
}
