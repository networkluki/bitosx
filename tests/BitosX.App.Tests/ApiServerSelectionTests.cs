using BitosX.ApiClient;

namespace BitosX.App.Tests;

/// <summary>
/// Which server the application talks to, and what the <c>--api-server</c> option does. The rule
/// that matters: an option that is given but cannot be used never leads to the production server.
/// </summary>
public sealed class ApiServerSelectionTests
{
    [Fact]
    public void WithoutTheOption_ItIsTheProductionServer()
    {
        Assert.Same(ApiServer.Production, ApiServerSelection.FromArguments([]));
        Assert.Same(ApiServer.Production, ApiServerSelection.FromArguments(["--data-directory", "/tmp/x", "-psn_0_1"]));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    public void None_DisablesTheServer(string value)
    {
        Assert.Null(ApiServerSelection.FromArguments(["--api-server", value]));
        Assert.Null(ApiServerSelection.FromArguments(["--api-server=" + value]));
    }

    [Fact]
    public void AnOrigin_IsTheServer()
    {
        ApiServer? server = ApiServerSelection.FromArguments(["--api-server", "https://api.example.test:8443"]);

        Assert.NotNull(server);
        Assert.Equal(new Uri("https://api.example.test:8443/"), server!.BaseAddress);
        Assert.False(server.IsProduction);
    }

    [Fact]
    public void PlainHttp_ToThisMachine_IsAllowedForDevelopment()
    {
        ApiServer? server = ApiServerSelection.FromArguments(["--api-server=http://127.0.0.1:5080"]);

        Assert.Equal(new Uri("http://127.0.0.1:5080/"), server!.BaseAddress);
    }

    [Theory]
    [InlineData("--api-server")]                                  // no value
    [InlineData("--api-server", "http://api.example.test")]        // plain http elsewhere
    [InlineData("--api-server", "https://api.example.test/v1")]    // a path
    [InlineData("--api-server", "api.example.test")]
    [InlineData("--api-server", "")]
    [InlineData("--api-server", "https://a.test", "--api-server", "https://b.test")]
    [InlineData("--api-sever", "https://api.example.test")]        // a misspelling must not quietly use production
    [InlineData("--API-SERVER=https://api.example.test")]
    public void AnOptionThatCannotBeUsed_IsRefused(params string[] arguments)
    {
        Assert.Throws<ApiServerSelectionException>(() => ApiServerSelection.FromArguments(arguments));
    }
}
