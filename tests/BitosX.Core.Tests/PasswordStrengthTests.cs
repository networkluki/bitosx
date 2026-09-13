using BitosX.Core.Security;

namespace BitosX.Core.Tests;

public sealed class PasswordStrengthTests
{
    [Fact]
    public void Estimate_EmptyPassword_IsVeryWeak()
        => Assert.Equal(PasswordStrengthLevel.VeryWeak, PasswordStrength.Estimate(""));

    [Fact]
    public void Estimate_ShortSimple_IsWeak()
        => Assert.True(PasswordStrength.Estimate("abc") <= PasswordStrengthLevel.Weak);

    [Fact]
    public void Estimate_LongComplex_IsStrong()
        => Assert.True(PasswordStrength.Estimate("9x!Kd82@Lm#qZ4wR7vB1") >= PasswordStrengthLevel.Strong);

    [Fact]
    public void EstimateEntropyBits_IncreasesWithLength()
    {
        double shortBits = PasswordStrength.EstimateEntropyBits("abcd");
        double longBits = PasswordStrength.EstimateEntropyBits("abcdefghijklmnop");
        Assert.True(longBits > shortBits);
    }
}
