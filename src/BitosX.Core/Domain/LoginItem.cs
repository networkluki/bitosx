using System.Text.Json.Serialization;
using BitosX.Core.Serialization;

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

    /// <summary>
    /// The authenticator key (TOTP), or <see langword="null"/> if the login has none. Stored as the
    /// member <c>Totp</c>, which is absent when there is no key; a vault that holds one is written
    /// with content schema version 2 (docs/TOTP.md §2).
    /// </summary>
    [JsonConverter(typeof(TotpKeyJsonConverter))]
    public TotpKey? Totp { get; init; }

    [JsonIgnore]
    public override VaultItemType ItemType => VaultItemType.Login;
}
