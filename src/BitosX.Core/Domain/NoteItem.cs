using System.Text.Json.Serialization;

namespace BitosX.Core.Domain;

/// <summary>
/// A secure note: a name and a text, nothing else (docs/NOTES.md). The text is
/// <see cref="VaultItem.Notes"/>, and is as secret as a password: it only ever exists in memory while
/// the vault is unlocked, is persisted only inside the encrypted vault ciphertext, and is never
/// logged, searched or quoted in an error. A vault that holds a note is written with content schema
/// version 3 (<see cref="Vault.SchemaVersionWithNotes"/>).
/// </summary>
public sealed record NoteItem : VaultItem
{
    [JsonIgnore]
    public override VaultItemType ItemType => VaultItemType.Note;
}
