using System.Text;
using BitosX.Core.Authenticator;
using BitosX.Core.Domain;

namespace BitosX.Core.Tests.Authenticator;

public sealed class TotpGeneratorTests
{
    // RFC 6238 Appendix B. The text names one seed, "12345678901234567890"; its reference code
    // (Appendix A, main) and the table use a 20-byte seed for SHA1, a 32-byte one for SHA256 and a
    // 64-byte one for SHA512, each the digits 1234567890 repeated. Values copied from Table 1.
    private const string Seed20 = "12345678901234567890";
    private const string Seed32 = "12345678901234567890123456789012";
    private const string Seed64 = "1234567890123456789012345678901234567890123456789012345678901234";

    public static TheoryData<long, TotpAlgorithm, string> Rfc6238AppendixB => new()
    {
        { 59L, TotpAlgorithm.Sha1, "94287082" },
        { 59L, TotpAlgorithm.Sha256, "46119246" },
        { 59L, TotpAlgorithm.Sha512, "90693936" },
        { 1111111109L, TotpAlgorithm.Sha1, "07081804" },
        { 1111111109L, TotpAlgorithm.Sha256, "68084774" },
        { 1111111109L, TotpAlgorithm.Sha512, "25091201" },
        { 1111111111L, TotpAlgorithm.Sha1, "14050471" },
        { 1111111111L, TotpAlgorithm.Sha256, "67062674" },
        { 1111111111L, TotpAlgorithm.Sha512, "99943326" },
        { 1234567890L, TotpAlgorithm.Sha1, "89005924" },
        { 1234567890L, TotpAlgorithm.Sha256, "91819424" },
        { 1234567890L, TotpAlgorithm.Sha512, "93441116" },
        { 2000000000L, TotpAlgorithm.Sha1, "69279037" },
        { 2000000000L, TotpAlgorithm.Sha256, "90698825" },
        { 2000000000L, TotpAlgorithm.Sha512, "38618901" },
        { 20000000000L, TotpAlgorithm.Sha1, "65353130" },
        { 20000000000L, TotpAlgorithm.Sha256, "77737706" },
        { 20000000000L, TotpAlgorithm.Sha512, "47863826" },
    };

    internal static TotpKey RfcKey(TotpAlgorithm algorithm)
    {
        string seed = algorithm switch
        {
            TotpAlgorithm.Sha1 => Seed20,
            TotpAlgorithm.Sha256 => Seed32,
            _ => Seed64,
        };
        return new TotpKey
        {
            Secret = Base32.Encode(Encoding.ASCII.GetBytes(seed)),
            Algorithm = algorithm,
            Digits = 8,
            Period = 30,
        };
    }

    [Theory]
    [MemberData(nameof(Rfc6238AppendixB))]
    public void Generate_Rfc6238AppendixB(long unixSeconds, TotpAlgorithm algorithm, string expected)
    {
        TotpCode code = TotpGenerator.Generate(RfcKey(algorithm), unixSeconds);
        Assert.Equal(expected, code.Code);
        Assert.Equal(30 - (int)(unixSeconds % 30), code.SecondsLeft);
        Assert.Equal(30, code.Period);
    }

    [Fact]
    public void RfcSeeds_HaveTheBase32ThatTheFixturesUse()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", RfcKey(TotpAlgorithm.Sha1).Secret);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA", RfcKey(TotpAlgorithm.Sha256).Secret);
        Assert.Equal(
            "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA",
            RfcKey(TotpAlgorithm.Sha512).Secret);
    }

    // Cases of our own; the codes were computed independently with Python's hmac module. With the
    // RFC seed and 6 digits, steps 0, 1 and 2 are also the HOTP values 755224, 287082 and 359152 of
    // RFC 4226 Appendix D.
    [Theory]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 6, 30, 59L, "287082", 1)]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 6, 30, 60L, "359152", 30)]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 7, 30, 59L, "4287082", 1)]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 6, 60, 59L, "755224", 1)]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 6, 60, 60L, "287082", 60)]
    [InlineData("GEZDGNBVGY3TQOJQ", TotpAlgorithm.Sha1, 6, 30, 0L, "891490", 30)]
    public void Generate_OwnCases(string secret, TotpAlgorithm algorithm, int digits, int period, long unixSeconds, string expected, int secondsLeft)
    {
        TotpCode code = TotpGenerator.Generate(
            new TotpKey { Secret = secret, Algorithm = algorithm, Digits = digits, Period = period }, unixSeconds);
        Assert.Equal(expected, code.Code);
        Assert.Equal(secondsLeft, code.SecondsLeft);
    }

    [Fact]
    public void Generate_SecondsLeft_CountsDownAndStartsAgainAtTheStep()
    {
        TotpKey key = RfcKey(TotpAlgorithm.Sha1);
        Assert.Equal(30, TotpGenerator.Generate(key, 30).SecondsLeft);
        Assert.Equal(1, TotpGenerator.Generate(key, 59).SecondsLeft);
        Assert.Equal(TotpGenerator.Generate(key, 30).Code, TotpGenerator.Generate(key, 59).Code);
        Assert.NotEqual(TotpGenerator.Generate(key, 59).Code, TotpGenerator.Generate(key, 60).Code);
    }

    [Fact]
    public void Generate_DateTimeOffset_IsTheUnixSeconds()
    {
        TotpKey key = RfcKey(TotpAlgorithm.Sha1);
        TotpCode code = TotpGenerator.Generate(key, DateTimeOffset.FromUnixTimeSeconds(1111111109).AddMilliseconds(999));
        Assert.Equal("07081804", code.Code);
    }

    [Fact]
    public void Generate_BeforeTheEpoch_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TotpGenerator.Generate(RfcKey(TotpAlgorithm.Sha1), -1));

    [Fact]
    public void Generate_InvalidKey_ThrowsWithoutQuotingIt()
    {
        var key = new TotpKey { Secret = "gezdgnbvgy3tqojqgezdgnbvgy3tqojq", Digits = 6, Period = 30 };
        ArgumentException ex = Assert.Throws<ArgumentException>(() => TotpGenerator.Generate(key, 0));
        Assert.DoesNotContain("gezdgnbv", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("123456", "123 456")]
    [InlineData("1234567", "123 4567")]
    [InlineData("12345678", "1234 5678")]
    public void Group_SplitsInTwo(string code, string expected) => Assert.Equal(expected, TotpGenerator.Group(code));

    [Fact]
    public void Display_IsTheGroupedCode()
        => Assert.Equal("9428 7082", TotpGenerator.Generate(RfcKey(TotpAlgorithm.Sha1), 59).Display);

    [Fact]
    public void ToString_OfAKeyAndAnItem_LeavesTheSecretOut()
    {
        TotpKey key = RfcKey(TotpAlgorithm.Sha1);
        var item = new LoginItem { Name = "x", Totp = key };
        Assert.DoesNotContain(key.Secret, key.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key.Secret, item.ToString(), StringComparison.Ordinal);
        Assert.Contains("Sha1", key.ToString(), StringComparison.Ordinal);
    }
}
