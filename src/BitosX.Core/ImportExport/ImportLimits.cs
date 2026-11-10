namespace BitosX.Core.ImportExport;

/// <summary>
/// The limits an import is held to. They are the same in the desktop application and in the web
/// vault (docs/IMPORT_EXPORT.md); the limits on a single item are the vault's own
/// (<see cref="Validation.VaultValidation"/>).
/// </summary>
public static class ImportLimits
{
    /// <summary>The largest file that is read: 16 MiB.</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>The most entries one file may hold. A file with more is refused, not read in part.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>
    /// The most items a vault may hold after an import (the web vault's limit on a vault). An import
    /// that would pass it is refused as a whole.
    /// </summary>
    public const int MaxVaultItems = 10_000;

    /// <summary>How deep JSON may be nested: 64 arrays and objects inside one another.</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>How deep the groups of a KeePass file may be nested, the root group being the first level.</summary>
    public const int MaxGroupDepth = 64;

    /// <summary>How deep any element of an XML file may be nested, the root element being the first level.</summary>
    public const int MaxXmlDepth = 100;
}
