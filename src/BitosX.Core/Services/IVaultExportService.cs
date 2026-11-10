using BitosX.Core.ImportExport;

namespace BitosX.Core.Services;

/// <summary>
/// Writing the vault out unencrypted, for another password manager to read. This is the costliest
/// single thing someone at an unlocked vault can do, so the unlocked session alone does not
/// authorize it: the master password has to be given again, and is verified before anything is
/// written.
/// </summary>
public interface IVaultExportService
{
    /// <summary>
    /// Verifies <paramref name="masterPassword"/> and, only if it is the master password of the
    /// unlocked vault, writes the vault's items to <paramref name="destinationPath"/> in
    /// <paramref name="format"/>. With a wrong password nothing is written, and an existing file at
    /// that path is left as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vault is not unlocked.</exception>
    /// <exception cref="BitosX.Crypto.VaultAuthenticationException">The password is not the master password.</exception>
    Task<ExportResult> ExportAsync(
        string destinationPath, ExportFormat format, string masterPassword, CancellationToken cancellationToken = default);
}

/// <summary>What an export wrote.</summary>
/// <param name="Format">The format.</param>
/// <param name="ItemCount">How many items are in the file.</param>
/// <param name="ItemsWithSeveralUrls">
/// How many of them have more than one address. In a CSV export only the first address of each is
/// in the file.
/// </param>
public sealed record ExportResult(ExportFormat Format, int ItemCount, int ItemsWithSeveralUrls);
