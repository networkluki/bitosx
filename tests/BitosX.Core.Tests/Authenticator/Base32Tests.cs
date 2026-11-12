using System.Text;
using BitosX.Core.Authenticator;

namespace BitosX.Core.Tests.Authenticator;

public sealed class Base32Tests
{
    // RFC 4648 §10, without the padding.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void EncodeAndDecode_Rfc4648Vectors(string text, string base32)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        Assert.Equal(base32, Base32.Encode(bytes));
        Assert.Equal(bytes, Base32.Decode(base32));
        Assert.Equal(bytes, Base32.Decode(base32.ToLowerInvariant()));
    }

    [Fact]
    public void Decode_DropsLeftoverBitsWithoutACheck()
    {
        // "MZ" carries 10 bits: 8 make "f" and the 2 left over are dropped, whatever they are.
        Assert.Equal("f"u8.ToArray(), Base32.Decode("MZ"));
        Assert.Equal("f"u8.ToArray(), Base32.Decode("MY"));
        Assert.Empty(Base32.Decode("M"));
    }

    [Fact]
    public void Decode_NotBase32_Throws() => Assert.Throws<ArgumentException>(() => Base32.Decode("MZ1"));

    [Theory]
    [InlineData("GEZDGNBVGY3TQOJQ", true)]           // 10 bytes
    [InlineData("GEZDGNBVGY3TQOJ", false)]           // 9 bytes
    [InlineData("gezdgnbvgy3tqojq", false)]          // lower case
    [InlineData("GEZDGNBVGY3TQOJQ====", false)]      // padding
    [InlineData("GEZDGNBV GY3TQOJQ", false)]         // a space
    [InlineData("GEZDGNBVGY3TQOJQG", false)]         // a length no encoding has
    [InlineData("GEZDGNBVGY3TQOJQGE", true)]         // 11 bytes, canonical
    [InlineData("GEZDGNBVGY3TQOJQGF", false)]        // 11 bytes, but leftover bits that are not zero
    public void IsCanonicalSecret(string text, bool expected)
        => Assert.Equal(expected, Base32.IsCanonicalSecret(text, 10, 128));

    [Fact]
    public void IsCanonicalSecret_LengthLimits()
    {
        Assert.True(Base32.IsCanonicalSecret(Base32.Encode(new byte[128]), 10, 128));
        Assert.False(Base32.IsCanonicalSecret(Base32.Encode(new byte[129]), 10, 128));
    }
}
