using BitosX.Core.Authenticator;
using BitosX.Core.Domain;

namespace BitosX.Core.Tests.Authenticator;

public sealed class TotpKeyParserTests
{
    private const string Rfc = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static TotpKey Key(string text)
    {
        TotpParseResult result = TotpKeyParser.Parse(text);
        Assert.Null(result.Refusal);
        return Assert.IsType<TotpKey>(result.Key);
    }

    private static void Refused(string text, TotpRefusal expected)
    {
        TotpParseResult result = TotpKeyParser.Parse(text);
        Assert.Null(result.Key);
        Assert.False(result.IsEmpty);
        Assert.Equal(expected, result.Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n 　﻿")]
    public void Parse_EmptyAfterTrimming_IsNoKey(string? text)
    {
        TotpParseResult result = TotpKeyParser.Parse(text);
        Assert.True(result.IsEmpty);
        Assert.Null(result.Key);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void Parse_Base32_IsSha1SixDigitsThirtySeconds()
    {
        TotpKey key = Key(Rfc);
        Assert.Equal(Rfc, key.Secret);
        Assert.Equal(TotpAlgorithm.Sha1, key.Algorithm);
        Assert.Equal(6, key.Digits);
        Assert.Equal(30, key.Period);
    }

    [Theory]
    [InlineData("gezd gnbv gy3t qojq gezd gnbv gy3t qojq")]
    [InlineData("GEZD-GNBV-GY3T-QOJQ-GEZD-GNBV-GY3T-QOJQ")]
    [InlineData("  GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ====\n")]
    public void Parse_Base32_SpacesDashesCaseAndPaddingDoNotMatter(string text) => Assert.Equal(Rfc, Key(text).Secret);

    [Fact]
    public void Parse_Base32_IsStoredCanonically()
        => Assert.Equal("GEZDGNBVGY3TQOJQGE", Key("GEZDGNBVGY3TQOJQGF").Secret);

    [Theory]
    [InlineData("GEZDGNBVGY3TQOJQ=GEZ", TotpRefusal.InvalidSecret)]
    [InlineData("GEZDGNBVGY3TQOJQ1", TotpRefusal.InvalidSecret)]
    [InlineData("GEZDGNBV\tGY3TQOJQ", TotpRefusal.InvalidSecret)]
    [InlineData("https://example.com/", TotpRefusal.InvalidSecret)]
    [InlineData("GEZDGNBVGY3TQOJ", TotpRefusal.SecretTooShort)]
    [InlineData("- -", TotpRefusal.SecretTooShort)]
    [InlineData("steam://ABCDEF", TotpRefusal.SteamNotSupported)]
    [InlineData("STEAM://ABCDEF", TotpRefusal.SteamNotSupported)]
    public void Parse_Refusals(string text, TotpRefusal expected) => Refused(text, expected);

    [Fact]
    public void Parse_SecretLengthLimits()
    {
        Assert.Equal(128, Base32.Decode(Key(new string('A', 206)).Secret).Length);
        Refused(new string('A', 207), TotpRefusal.SecretTooLong);
    }

    [Fact]
    public void Parse_TooLong_IsCountedBeforeTrimming()
    {
        Refused(new string(' ', 2049), TotpRefusal.TooLong);
        Assert.True(TotpKeyParser.Parse(new string(' ', 2048)).IsEmpty);
    }

    [Fact]
    public void Parse_Link_WithEverything()
    {
        TotpKey key = Key("otpauth://totp/Example:alice@example.com?secret=" + Rfc + "&issuer=Example&algorithm=SHA256&digits=8&period=60");
        Assert.Equal(Rfc, key.Secret);
        Assert.Equal(TotpAlgorithm.Sha256, key.Algorithm);
        Assert.Equal(8, key.Digits);
        Assert.Equal(60, key.Period);
    }

    [Theory]
    [InlineData("OTPAUTH://TOTP/x?SECRET=" + Rfc + "&Algorithm=sha512")]
    [InlineData("otpauth://totp/?secret=" + Rfc + "&&algorithm=Sha512&")]
    [InlineData("otpauth://totp/a%20b?secret=" + Rfc + "&algorithm=SHA512#issuer=x")]
    public void Parse_Link_NamesAndValuesInAnyCase(string text)
    {
        TotpKey key = Key(text);
        Assert.Equal(Rfc, key.Secret);
        Assert.Equal(TotpAlgorithm.Sha512, key.Algorithm);
    }

    [Fact]
    public void Parse_Link_PercentDecodesTheSecret()
        => Assert.Equal(Rfc, Key("otpauth://totp/x?secret=GEZDGNBV%20GY3TQOJQ%2dGEZDGNBVGY3TQOJQ%3D%3d").Secret);

    [Theory]
    [InlineData("otpauth://totp", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x#?secret=" + Rfc, TotpRefusal.InvalidLink)]
    [InlineData("otpauth://motp/x?secret=" + Rfc, TotpRefusal.InvalidLink)]
    [InlineData("otpauth://hotp/x?secret=" + Rfc + "&counter=1", TotpRefusal.HotpNotSupported)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&issuer", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&issuer=%E", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&issuer=%ZZ", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&issuer=%C3", TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&Secret=" + Rfc, TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?issuer=a&issuer=b&secret=" + Rfc, TotpRefusal.InvalidLink)]
    [InlineData("otpauth://totp/x?issuer=x", TotpRefusal.MissingSecret)]
    [InlineData("otpauth://totp/x?secret=", TotpRefusal.MissingSecret)]
    [InlineData("otpauth://totp/x?secret=GEZD+GNBVGY3TQOJQ", TotpRefusal.InvalidSecret)]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV", TotpRefusal.SecretTooShort)]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&algorithm=MD5", TotpRefusal.SecretTooShort)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&algorithm=MD5&digits=9", TotpRefusal.UnsupportedAlgorithm)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&algorithm=", TotpRefusal.UnsupportedAlgorithm)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&algorithm=SHA-1", TotpRefusal.UnsupportedAlgorithm)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&digits=5", TotpRefusal.UnsupportedDigits)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&digits=06", TotpRefusal.UnsupportedDigits)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&digits=+6", TotpRefusal.UnsupportedDigits)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&digits=", TotpRefusal.UnsupportedDigits)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&digits=6&period=0", TotpRefusal.UnsupportedPeriod)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&period=301", TotpRefusal.UnsupportedPeriod)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&period=030", TotpRefusal.UnsupportedPeriod)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&period=1e2", TotpRefusal.UnsupportedPeriod)]
    [InlineData("otpauth://totp/x?secret=" + Rfc + "&period=", TotpRefusal.UnsupportedPeriod)]
    public void Parse_Link_Refusals(string text, TotpRefusal expected) => Refused(text, expected);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("300", 300)]
    [InlineData("60", 60)]
    public void Parse_Link_Periods(string period, int expected)
        => Assert.Equal(expected, Key("otpauth://totp/x?secret=" + Rfc + "&period=" + period).Period);

    [Fact]
    public void Parse_Link_IgnoresOtherParametersAndTheLabel()
    {
        TotpKey key = Key("otpauth://totp/%%%?secret=" + Rfc + "&image=https%3A%2F%2Fexample.com%2Fa.png&counter=x&issuer=%C3%A9");
        Assert.Equal(Rfc, key.Secret);
        Assert.Equal(30, key.Period);
    }

    [Fact]
    public void Texts_EveryRefusalHasACodeAndAText()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (TotpRefusal refusal in Enum.GetValues<TotpRefusal>())
        {
            Assert.True(codes.Add(TotpTexts.Code(refusal)));
            Assert.False(string.IsNullOrWhiteSpace(TotpTexts.Text(refusal)));
        }
    }
}
