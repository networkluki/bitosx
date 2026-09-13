namespace BitosX.Core.Security;

/// <summary>A lightweight, non-sensitive reference to a vault item (id + display name only).</summary>
public sealed record VaultItemReference(Guid Id, string Name);

/// <summary>A group of items that share the same password.</summary>
public sealed record ReusedPasswordFinding(IReadOnlyList<VaultItemReference> Items);

/// <summary>An item whose password is below the recommended strength.</summary>
public sealed record WeakPasswordFinding(VaultItemReference Item, PasswordStrengthLevel Strength);

/// <summary>A group of items that appear to be duplicates (same name and username).</summary>
public sealed record DuplicateEntryFinding(IReadOnlyList<VaultItemReference> Items);

/// <summary>An item that stores an insecure (plain HTTP) URL.</summary>
public sealed record InsecureUrlFinding(VaultItemReference Item, string Url);

/// <summary>
/// The result of a local security analysis. Contains only item references and metadata;
/// it NEVER contains password plaintext, and nothing here is uploaded anywhere.
/// </summary>
public sealed record SecurityReport(
    IReadOnlyList<ReusedPasswordFinding> ReusedPasswords,
    IReadOnlyList<WeakPasswordFinding> WeakPasswords,
    IReadOnlyList<DuplicateEntryFinding> DuplicateEntries,
    IReadOnlyList<InsecureUrlFinding> InsecureUrls)
{
    /// <summary>Total number of individual findings across all categories.</summary>
    public int TotalIssues =>
        ReusedPasswords.Count + WeakPasswords.Count + DuplicateEntries.Count + InsecureUrls.Count;

    /// <summary>An empty report (no issues found).</summary>
    public static SecurityReport Empty { get; } = new([], [], [], []);
}
