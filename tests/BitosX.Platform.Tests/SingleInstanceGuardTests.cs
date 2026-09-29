using BitosX.Platform.Instance;

namespace BitosX.Platform.Tests;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueVaultPath() =>
        Path.Combine(Path.GetTempPath(), "BitosX.Instance", Guid.NewGuid().ToString("N"), "vault.bitosx");

    [Fact]
    public void TryAcquire_FirstInstance_Succeeds()
    {
        using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(UniqueVaultPath());

        Assert.NotNull(guard);
    }

    [Fact]
    public void TryAcquire_WhileAnotherInstanceHoldsTheVault_IsRefused()
    {
        string vault = UniqueVaultPath();
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(vault);

        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(vault);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterTheFirstInstanceExited_Succeeds()
    {
        string vault = UniqueVaultPath();
        SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(vault);
        Assert.NotNull(first);

        first.Dispose();
        first.Dispose(); // idempotent
        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(vault);

        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_ARefusedAttempt_DoesNotKeepTheVaultClaimed()
    {
        string vault = UniqueVaultPath();
        SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(vault);
        Assert.Null(SingleInstanceGuard.TryAcquire(vault));

        first!.Dispose();
        using SingleInstanceGuard? third = SingleInstanceGuard.TryAcquire(vault);

        Assert.NotNull(third);
    }

    [Fact]
    public void TryAcquire_DifferentVaults_AreIndependent()
    {
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(UniqueVaultPath());
        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(UniqueVaultPath());

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_TheSameVaultSpelledDifferently_IsTheSameVault()
    {
        string vault = UniqueVaultPath();
        string directory = Path.GetDirectoryName(vault)!;
        string sameVault = Path.Combine(directory, ".", "..", Path.GetFileName(directory), "vault.bitosx");
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(vault);

        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(sameVault);

        Assert.Null(second);
    }

    [Fact]
    public void NameFor_IsStable_AndDoesNotRevealThePath()
    {
        string vault = Path.Combine(Path.GetTempPath(), "some-user-name", "vault.bitosx");

        string name = SingleInstanceGuard.NameFor(vault);

        Assert.Equal(name, SingleInstanceGuard.NameFor(vault));
        Assert.DoesNotContain("some-user-name", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.Matches("^BitosX\\.Vault\\.[0-9A-F]{32}$", name);
    }

    [Fact]
    public void NameFor_FollowsTheFileSystemsCaseRules()
    {
        string lower = Path.Combine(Path.GetTempPath(), "bitosx", "vault.bitosx");
        string upper = Path.Combine(Path.GetTempPath(), "BITOSX", "VAULT.BITOSX");

        bool same = SingleInstanceGuard.NameFor(lower) == SingleInstanceGuard.NameFor(upper);

        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), same);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAcquire_BlankPath_Throws(string path)
        => Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(path));
}
