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

    // ---- Patterns an attacker tries first (0.5.1) ----

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]         // one character repeated: was rated Strong
    [InlineData("abcdefghijklmnop")]             // alphabet run
    [InlineData("12345678901234567890")]         // digit run
    [InlineData("zyxwvutsrqponmlk")]             // descending run
    [InlineData("qwertyuiopasdfgh")]             // keyboard rows
    [InlineData("QWERTYUIOPASDFGH")]
    [InlineData("abababababababab")]             // a block repeated
    [InlineData("P@ssw0rd!P@ssw0rd!")]           // was rated VeryStrong
    [InlineData("Passw0rd!Passw0rd!Passw0rd!")]  // was rated VeryStrong
    [InlineData("Password123!")]                 // a common password with decoration
    [InlineData("Qwerty123456!")]                // was rated Strong
    [InlineData("iloveyou1234")]
    [InlineData("letmein2026!")]
    [InlineData("TrustNo1TrustNo1")]
    [InlineData("pa55word-pa55word")]
    [InlineData("bitosx-bitosx-bitosx")]
    public void Estimate_ObviousPattern_IsNeverBetterThanWeak(string password)
        => Assert.True(
            PasswordStrength.Estimate(password) <= PasswordStrengthLevel.Weak,
            $"'{password}' was rated {PasswordStrength.Estimate(password)} ({PasswordStrength.EstimateEntropyBits(password):0.0} bits).");

    [Theory]
    [InlineData("9x!Kd82@Lm#qZ4wR7vB1")]
    [InlineData("hV7#mQ2$xL9@cT4&nB6!")]
    [InlineData("mX4kP9wQ2rT7yL5nB8cV")]
    public void Estimate_LongRandomPassword_IsStrong(string password)
        => Assert.True(PasswordStrength.Estimate(password) >= PasswordStrengthLevel.Strong);

    [Fact]
    public void Estimate_PasswordsFromTheGenerator_AreStrong()
    {
        // A random password contains the odd neighbouring pair by chance; that must not drag it down.
        var generator = new BitosX.Core.Generation.PasswordGenerator();
        var options = new BitosX.Core.Generation.PasswordGenerationOptions { Length = 20 };

        for (int i = 0; i < 500; i++)
        {
            string password = generator.Generate(options);
            Assert.True(
                PasswordStrength.Estimate(password) >= PasswordStrengthLevel.Strong,
                $"A generated password was rated {PasswordStrength.Estimate(password)}.");
        }
    }

    [Fact]
    public void EstimateEntropyBits_NeverExceedsTheEstimateForRandomCharacters()
    {
        // The discounts only ever lower the estimate.
        foreach (string password in new[] { "aaaa", "abcd", "Password1", "x9#Lq2", "correct horse battery staple" })
        {
            double upperBound = password.Length * Math.Log2(94);
            Assert.InRange(PasswordStrength.EstimateEntropyBits(password), 0.1, upperBound);
        }
    }

    [Fact]
    public void EstimateEntropyBits_RepeatingABlock_AddsAlmostNothing()
    {
        double once = PasswordStrength.EstimateEntropyBits("x9#Lq2vT");
        double fourTimes = PasswordStrength.EstimateEntropyBits("x9#Lq2vTx9#Lq2vTx9#Lq2vTx9#Lq2vT");

        Assert.InRange(fourTimes - once, 0, 3);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("åäö")]
    [InlineData("🔐🔐🔐")]
    [InlineData("\0\0\0")]
    [InlineData("     ")]
    public void EstimateEntropyBits_UnusualInput_DoesNotThrow(string password)
        => Assert.True(PasswordStrength.EstimateEntropyBits(password) > 0);

    [Fact]
    public void EstimateEntropyBits_VeryLongInput_IsHandled()
    {
        string password = string.Concat(Enumerable.Range(0, 4096).Select(i => (char)('!' + (i * 37 % 90))));

        Assert.Equal(PasswordStrengthLevel.VeryStrong, PasswordStrength.Estimate(password));
    }
}
