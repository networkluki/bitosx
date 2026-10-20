using BitosX.ApiClient;

namespace BitosX.ApiClient.Tests;

public sealed class ApiServerTests
{
    [Fact]
    public void Production_IsTheBitosXApi()
    {
        Assert.Equal(new Uri("https://api.bitosx.se/"), ApiServer.Production.BaseAddress);
        Assert.Equal("api.bitosx.se", ApiServer.Production.DisplayName);
        Assert.True(ApiServer.Production.IsProduction);
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/", "api.example.com")]
    [InlineData("https://API.Example.com/", "https://api.example.com/", "api.example.com")]
    [InlineData("https://api.example.com:8443", "https://api.example.com:8443/", "api.example.com:8443")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/", "127.0.0.1:5080")]
    [InlineData("http://localhost:5080", "http://localhost:5080/", "localhost:5080")]
    [InlineData("http://[::1]:5080", "http://[::1]:5080/", "[::1]:5080")]
    public void Parse_AcceptsAnOrigin(string given, string canonical, string display)
    {
        ApiServer server = ApiServer.Parse(given);

        Assert.Equal(new Uri(canonical), server.BaseAddress);
        Assert.Equal(display, server.DisplayName);
        Assert.False(server.IsProduction);
    }

    [Theory]
    [InlineData("http://api.example.com")]          // plain http to a host that is not this machine
    [InlineData("http://10.0.0.5:5080")]
    [InlineData("ftp://api.example.com")]
    [InlineData("api.example.com")]
    [InlineData("https://api.example.com/v1")]
    [InlineData("https://api.example.com/?x=1")]
    [InlineData("https://api.example.com/#frag")]
    [InlineData("https://user:pw@api.example.com")]
    [InlineData("https://api.exa mple.com")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RefusesAnythingButAnOrigin(string given)
    {
        Assert.ThrowsAny<ArgumentException>(() => ApiServer.Parse(given));
    }

    [Fact]
    public void Parse_RefusesAnOverlongValue()
    {
        Assert.ThrowsAny<ArgumentException>(() => ApiServer.Parse("https://" + new string('a', 300) + ".example"));
    }
}
