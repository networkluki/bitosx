using BitosX.Core.Settings;

namespace BitosX.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void ProductionDefaults_AreExpected()
    {
        AppSettings d = AppSettings.Default;

        Assert.True(d.AutoLockOnInactivity);
        Assert.Equal(TimeSpan.FromMinutes(15), d.InactivityTimeout); // intended production default
        Assert.False(d.LockOnMinimize);
    }

    [Fact]
    public void Normalized_ClampsBelowFloor()
    {
        AppSettings s = new AppSettings { InactivityTimeout = TimeSpan.FromSeconds(1) }.Normalized();
        Assert.Equal(AppSettings.MinInactivityTimeout, s.InactivityTimeout);
    }

    [Fact]
    public void Normalized_ClampsAboveCeiling()
    {
        AppSettings s = new AppSettings { InactivityTimeout = TimeSpan.FromDays(30) }.Normalized();
        Assert.Equal(AppSettings.MaxInactivityTimeout, s.InactivityTimeout);
    }

    [Fact]
    public void Normalized_KeepsOneMinute()
    {
        // 1 minute must remain a valid choice (used for manual testing).
        AppSettings s = new AppSettings { InactivityTimeout = TimeSpan.FromMinutes(1) }.Normalized();
        Assert.Equal(TimeSpan.FromMinutes(1), s.InactivityTimeout);
    }
}
