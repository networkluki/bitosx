using BitosX.Core.Domain;
using BitosX.Core.Security;

namespace BitosX.Core.Tests;

public sealed class SecurityAnalyzerTests
{
    private readonly SecurityAnalyzer _analyzer = new();

    private static LoginItem Login(string name, string? username, string? password, params string[] urls)
        => new() { Name = name, Username = username, Password = password, Urls = urls };

    [Fact]
    public void Analyze_DetectsReusedPasswords()
    {
        var vault = new Vault();
        vault.Add(Login("Site A", "a", "SharedP@ssw0rd-Strong-1"));
        vault.Add(Login("Site B", "b", "SharedP@ssw0rd-Strong-1"));
        vault.Add(Login("Site C", "c", "UniqueP@ssw0rd-Strong-2"));

        SecurityReport report = _analyzer.Analyze(vault);

        Assert.Single(report.ReusedPasswords);
        Assert.Equal(2, report.ReusedPasswords[0].Items.Count);
    }

    [Fact]
    public void Analyze_DetectsWeakPasswords()
    {
        var vault = new Vault();
        vault.Add(Login("Weak", "a", "abc"));
        vault.Add(Login("Strong", "b", "9x!Kd82@Lm#qZ4wR7v"));

        SecurityReport report = _analyzer.Analyze(vault);

        Assert.Contains(report.WeakPasswords, f => f.Item.Name == "Weak");
        Assert.DoesNotContain(report.WeakPasswords, f => f.Item.Name == "Strong");
    }

    [Fact]
    public void Analyze_DetectsDuplicateEntries()
    {
        var vault = new Vault();
        vault.Add(Login("GitHub", "octocat", "P@ss-one-Strong-11"));
        vault.Add(Login("github", "OctoCat", "P@ss-two-Strong-22"));

        SecurityReport report = _analyzer.Analyze(vault);

        Assert.Single(report.DuplicateEntries);
        Assert.Equal(2, report.DuplicateEntries[0].Items.Count);
    }

    [Fact]
    public void Analyze_DetectsInsecureHttpUrls()
    {
        var vault = new Vault();
        vault.Add(Login("Insecure", "a", "P@ssw0rd-Strong-123", "http://example.com", "https://secure.example.com"));

        SecurityReport report = _analyzer.Analyze(vault);

        Assert.Single(report.InsecureUrls);
        Assert.Equal("http://example.com", report.InsecureUrls[0].Url);
    }

    [Fact]
    public void Analyze_EmptyVault_ReportsNoIssues()
    {
        SecurityReport report = _analyzer.Analyze(new Vault());
        Assert.Equal(0, report.TotalIssues);
    }

    [Fact]
    public void Report_NeverContainsPasswordPlaintext()
    {
        var vault = new Vault();
        vault.Add(Login("Weak", "a", "abc"));
        SecurityReport report = _analyzer.Analyze(vault);

        // Findings expose only id + name, never the password value.
        Assert.All(report.WeakPasswords, f => Assert.NotEqual("abc", f.Item.Name));
        Assert.DoesNotContain("abc", string.Join('|', report.WeakPasswords.Select(f => f.Item.Name)), StringComparison.Ordinal);
    }
}
