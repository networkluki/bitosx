using System.Collections.ObjectModel;
using BitosX.Core.Security;
using BitosX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>The Security Center page: local-only checks over the unlocked vault.</summary>
public sealed partial class SecurityPageViewModel : ViewModelBase
{
    private readonly IVaultService _vaultService;
    private readonly ISecurityAnalyzer _analyzer;

    [ObservableProperty]
    public partial int TotalIssues { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<string> ReusedPasswords { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<string> WeakPasswords { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<string> DuplicateEntries { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<string> InsecureUrls { get; set; } = [];

    public SecurityPageViewModel(IVaultService vaultService, ISecurityAnalyzer analyzer)
    {
        _vaultService = vaultService;
        _analyzer = analyzer;
    }

    [RelayCommand]
    public void Analyze()
    {
        SecurityReport report = _analyzer.Analyze(_vaultService.Items);

        ReusedPasswords = [.. report.ReusedPasswords.Select(
            f => $"Reused by {f.Items.Count} items: {string.Join(", ", f.Items.Select(i => i.Name))}")];

        WeakPasswords = [.. report.WeakPasswords.Select(f => $"{f.Item.Name} — {StrengthDisplay.Phrase(f.Strength)}")];

        DuplicateEntries = [.. report.DuplicateEntries.Select(
            f => $"Duplicate: {string.Join(", ", f.Items.Select(i => i.Name))}")];

        InsecureUrls = [.. report.InsecureUrls.Select(f => $"{f.Item.Name}: {f.Url}")];

        TotalIssues = report.TotalIssues;
        SummaryText = TotalIssues == 0
            ? "No issues found in the local checks."
            : $"{TotalIssues} issue(s) found across local checks.";
    }
}
