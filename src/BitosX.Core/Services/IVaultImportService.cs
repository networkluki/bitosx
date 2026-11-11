using BitosX.Core.ImportExport;

namespace BitosX.Core.Services;

/// <summary>
/// Importing logins from the export of another password manager, in three steps: read the file,
/// show what importing it would do, and — only when the user has confirmed — add the items.
/// Nothing is written before the last step, and that step is one write: every item is added, or
/// none is. Every step requires the vault to be unlocked.
/// </summary>
public interface IVaultImportService
{
    /// <summary>
    /// Reads and parses a file. The format is detected from its content. Changes nothing.
    /// </summary>
    /// <exception cref="ImportRefusedException">The file is not read: see <see cref="ImportRefusal"/>.</exception>
    Task<ParsedImport> ReadAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// What importing <paramref name="file"/> into the unlocked vault would do, with the given
    /// choices: what a preview shows. Changes nothing; it can be asked again with other choices.
    /// </summary>
    Task<ImportPlan> PreviewAsync(ParsedImport file, ImportOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the items of <paramref name="plan"/> to the vault, in one write. The file is compared
    /// with the vault again at that moment, under the same gate every change to the vault goes
    /// through: if the vault changed since the preview, what is added is what is right for the vault
    /// as it is now, and the result says what that was.
    /// </summary>
    /// <exception cref="ImportRefusedException">
    /// The vault would hold more items than a vault may (<see cref="ImportRefusal.VaultItemLimit"/>).
    /// Nothing was added.
    /// </exception>
    Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>What an import did.</summary>
/// <param name="Applied">
/// The plan as it was carried out: made against the vault as it was at the moment of the write.
/// It equals the previewed plan unless the vault changed in between.
/// </param>
/// <param name="Added">How many items were added.</param>
public sealed record ImportResult(ImportPlan Applied, int Added);
