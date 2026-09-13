using System.Text.Json.Serialization;

namespace BitosX.Core.Domain;

/// <summary>
/// Base type for all secrets stored in a vault. Immutable: edits produce a new instance
/// (via <c>with</c>) so that update semantics and timestamps are handled explicitly by
/// <see cref="Vault"/>. Instances only exist in memory while the vault is unlocked.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoginItem), typeDiscriminator: "login")]
public abstract record VaultItem
{
    /// <summary>Stable unique identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Human-readable display name (required).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional free-form notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Whether the user marked this item as a favorite.</summary>
    public bool Favorite { get; init; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last-modification timestamp (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Discriminator describing the concrete item type. Not serialized directly.</summary>
    [JsonIgnore]
    public abstract VaultItemType ItemType { get; }
}
