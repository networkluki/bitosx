using BitosX.Core.Diagnostics;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>
/// Default <see cref="IVaultExportService"/>. The master password is verified by the vault service
/// (<see cref="IVaultService.VerifyMasterPasswordAsync"/>), which also hands back the items the
/// verification was for; those are what is written, through <see cref="IImportExportFiles"/>.
/// </summary>
/// <remarks>
/// The bytes of the export are wiped once they are written. The log says that an unencrypted export
/// was written, in which format and with how many items — as a warning, because it is worth
/// noticing — and never where to, or anything of its content.
/// </remarks>
public sealed class VaultExportService : IVaultExportService
{
    private const string NotUnlockedMessage = "The vault must be unlocked to export.";

    private readonly IVaultService _vault;
    private readonly IImportExportFiles _files;
    private readonly TimeProvider _time;
    private readonly ILogger<VaultExportService> _logger;

    public VaultExportService(
        IVaultService vault,
        IImportExportFiles files,
        TimeProvider time,
        ILogger<VaultExportService> logger)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExportResult> ExportAsync(
        string destinationPath, ExportFormat format, string masterPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(masterPassword);
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        RequireUnlocked();

        byte[]? content = null;
        try
        {
            // First the password, then everything else: with a wrong one nothing is even prepared.
            IReadOnlyList<VaultItem> items =
                await _vault.VerifyMasterPasswordAsync(masterPassword, cancellationToken).ConfigureAwait(false);

            DateTimeOffset exportedAt = _time.GetUtcNow();
            content = await Task.Run(() => VaultExporter.Export(items, format, exportedAt), cancellationToken).ConfigureAwait(false);

            // The vault may have been locked since the password was checked. A lock means "stop".
            RequireUnlocked();

            await _files.WriteExportFileAsync(destinationPath, content, cancellationToken).ConfigureAwait(false);

            VaultLog.UnencryptedExportWritten(_logger, format.ToString(), items.Count);
            return new ExportResult(
                format, items.Count, VaultExporter.CountItemsWithSeveralUrls(items), VaultExporter.CountItemsWithAuthenticatorKeys(items));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VaultLog.UnencryptedExportFailed(_logger);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(content);
        }
    }

    private void RequireUnlocked()
    {
        if (_vault.State != VaultState.Unlocked)
        {
            throw new InvalidOperationException(NotUnlockedMessage);
        }
    }
}
