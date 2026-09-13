using BitosX.Core.Domain;

namespace BitosX.Core.Security;

/// <summary>Performs local-only security analysis of a decrypted vault.</summary>
public interface ISecurityAnalyzer
{
    /// <summary>Analyzes the vault and returns a report. No data leaves the device.</summary>
    SecurityReport Analyze(Vault vault);

    /// <summary>Analyzes a set of vault items and returns a report. No data leaves the device.</summary>
    SecurityReport Analyze(IReadOnlyList<VaultItem> items);
}
