using BitosX.Core.Domain;

namespace BitosX.Core.Security;

/// <summary>
/// Local security analysis: detects reused passwords, weak passwords, likely duplicate
/// entries, and insecure (HTTP) URLs. All analysis happens in memory on the device; no
/// passwords or vault contents are transmitted anywhere. External breach monitoring is
/// intentionally out of scope for this milestone.
/// </summary>
public sealed class SecurityAnalyzer : ISecurityAnalyzer
{
    /// <summary>Passwords below this strength level are reported as weak.</summary>
    public const PasswordStrengthLevel WeakThreshold = PasswordStrengthLevel.Fair;

    public SecurityReport Analyze(Vault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return Analyze(vault.Items);
    }

    public SecurityReport Analyze(IReadOnlyList<VaultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        LoginItem[] logins = [.. items.OfType<LoginItem>()];

        return new SecurityReport(
            FindReusedPasswords(logins),
            FindWeakPasswords(logins),
            FindDuplicateEntries(logins),
            FindInsecureUrls(logins));
    }

    private static VaultItemReference Ref(VaultItem item) => new(item.Id, item.Name);

    private static List<ReusedPasswordFinding> FindReusedPasswords(IReadOnlyList<LoginItem> logins)
    {
        // Group by exact password value using an ordinal comparer. The password value is used
        // only as a grouping key here and never leaves this method or appears in the report.
        return logins
            .Where(l => !string.IsNullOrEmpty(l.Password))
            .GroupBy(l => l.Password!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new ReusedPasswordFinding([.. g.Select(Ref)]))
            .ToList();
    }

    private static List<WeakPasswordFinding> FindWeakPasswords(IReadOnlyList<LoginItem> logins)
    {
        var findings = new List<WeakPasswordFinding>();
        foreach (LoginItem login in logins)
        {
            if (string.IsNullOrEmpty(login.Password))
            {
                continue;
            }

            PasswordStrengthLevel strength = PasswordStrength.Estimate(login.Password);
            if (strength < WeakThreshold)
            {
                findings.Add(new WeakPasswordFinding(Ref(login), strength));
            }
        }

        return findings;
    }

    private static List<DuplicateEntryFinding> FindDuplicateEntries(IReadOnlyList<LoginItem> logins)
    {
        return logins
            .GroupBy(
                l => (Name: l.Name.Trim(), Username: (l.Username ?? string.Empty).Trim()),
                DuplicateKeyComparer.Instance)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateEntryFinding([.. g.Select(Ref)]))
            .ToList();
    }

    private static List<InsecureUrlFinding> FindInsecureUrls(IReadOnlyList<LoginItem> logins)
    {
        var findings = new List<InsecureUrlFinding>();
        foreach (LoginItem login in logins)
        {
            foreach (string url in login.Urls)
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new InsecureUrlFinding(Ref(login), url));
                }
            }
        }

        return findings;
    }

    private sealed class DuplicateKeyComparer : IEqualityComparer<(string Name, string Username)>
    {
        public static readonly DuplicateKeyComparer Instance = new();

        public bool Equals((string Name, string Username) x, (string Name, string Username) y)
            => string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Username, y.Username, StringComparison.OrdinalIgnoreCase);

        // Hashed with the same comparison as Equals, so that keys Equals calls equal always hash alike.
        public int GetHashCode((string Name, string Username) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Username));
    }
}
