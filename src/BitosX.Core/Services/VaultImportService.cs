using BitosX.Core.Diagnostics;
using BitosX.Core.Domain;
using BitosX.Core.ImportExport;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.Logging;

namespace BitosX.Core.Services;

/// <summary>
/// Default <see cref="IVaultImportService"/>: reads the file through <see cref="IImportExportFiles"/>,
/// parses and plans with the import library, and adds the items through
/// <see cref="IVaultService.AddLoginsAsync"/>.
/// </summary>
/// <remarks>
/// The file's bytes are wiped once they are parsed. What was parsed (<see cref="ParsedImport"/>, and
/// the plans made from it) holds the file's passwords as text until the caller lets go of it, which
/// it has to do when the vault locks; text cannot be wiped (docs/SECURITY_MODEL.md).
/// Nothing from a file is ever logged: only how many items were added, and the kind of a refusal.
/// </remarks>
public sealed class VaultImportService : IVaultImportService
{
    private const string NotUnlockedMessage = "The vault must be unlocked to import.";

    private readonly IVaultService _vault;
    private readonly IImportExportFiles _files;
    private readonly TimeProvider _time;
    private readonly ILogger<VaultImportService> _logger;

    public VaultImportService(
        IVaultService vault,
        IImportExportFiles files,
        TimeProvider time,
        ILogger<VaultImportService> logger)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ParsedImport> ReadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        RequireUnlocked();

        byte[]? bytes = null;
        try
        {
            bytes = await _files.ReadImportFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);

            // Up to 16 MiB to decode and parse: not on the caller's thread, which is the UI thread.
            byte[] file = bytes;
            return await Task.Run(() => ImportParser.Parse(file), cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException refused)
        {
            VaultLog.ImportRefused(_logger, refused.Code);
            throw;
        }
        finally
        {
            CryptoBuffers.Wipe(bytes);
        }
    }

    public Task<ImportPlan> PreviewAsync(ParsedImport file, ImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        RequireUnlocked();

        IReadOnlyList<VaultItem> items = _vault.Items;
        return Task.Run(() => ImportPlanner.Plan(file, items, options), cancellationToken);
    }

    public async Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireUnlocked();

        DateTimeOffset importedAt = _time.GetUtcNow();
        ImportPlan? applied = null;
        IReadOnlyList<LoginItem> added;
        try
        {
            // Off the caller's thread: planning, and then serializing and encrypting the whole vault.
            added = await Task.Run(
                () => _vault.AddLoginsAsync(
                    existing =>
                    {
                        // Decided again on what the vault holds now, where nothing else can change it
                        // until the items are stored: never on what it held when the preview was made.
                        ImportPlan current = ImportPlanner.Plan(plan.Source, existing, plan.Options);
                        if (current.ExceedsVaultItemLimit)
                        {
                            throw new ImportRefusedException(ImportRefusal.VaultItemLimit);
                        }

                        applied = current;
                        return current.CreateItems(importedAt);
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException refused)
        {
            VaultLog.ImportRefused(_logger, refused.Code);
            throw;
        }

        VaultLog.ImportApplied(_logger, added.Count);
        return new ImportResult(applied!, added.Count);
    }

    private void RequireUnlocked()
    {
        if (_vault.State != VaultState.Unlocked)
        {
            throw new InvalidOperationException(NotUnlockedMessage);
        }
    }
}
