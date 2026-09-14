using System.Text.RegularExpressions;
using BitosX.App;

namespace BitosX.App.Tests;

/// <summary>
/// Regression tests for the user-facing version string. The About section previously showed the CLR
/// 4-part assembly version ("0.3.0.0"); it must show a 3-part SemVer core ("0.3.0") with no trailing
/// revision and no +build metadata.
/// </summary>
public sealed class AppVersionInfoTests
{
    [Fact]
    public void Normalize_StripsBuildMetadata_FromInformationalVersion()
    {
        Assert.Equal("0.3.0", AppVersionInfo.Normalize("0.3.0+80c2711bb47e55f0", new Version(0, 3, 0, 0)));
    }

    [Fact]
    public void Normalize_KeepsCleanInformationalVersion_AsIs()
    {
        Assert.Equal("1.2.3", AppVersionInfo.Normalize("1.2.3", new Version(1, 2, 3, 0)));
    }

    [Fact]
    public void Normalize_PrefersInformationalVersion_OverAssemblyVersion()
    {
        Assert.Equal("2.5.9", AppVersionInfo.Normalize("2.5.9+deadbeef", new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void Normalize_FallsBackToThreePartAssemblyVersion_DroppingRevision()
    {
        Assert.Equal("0.3.0", AppVersionInfo.Normalize(null, new Version(0, 3, 0, 0)));
        Assert.Equal("0.3.0", AppVersionInfo.Normalize("   ", new Version(0, 3, 0, 7)));
    }

    [Fact]
    public void Normalize_HasSaneDefault_WhenNothingIsAvailable()
    {
        Assert.Equal("0.0.0", AppVersionInfo.Normalize(null, null));
    }

    [Fact]
    public void Display_IsThreePartSemVer_WithNoRevisionOrBuildMetadata()
    {
        // The regression: must never be the 4-part CLR version and must never carry +metadata.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AppVersionInfo.Display);
        Assert.DoesNotContain("+", AppVersionInfo.Display);
    }
}
