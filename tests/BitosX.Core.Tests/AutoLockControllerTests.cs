using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.Core.Tests;

public sealed class AutoLockControllerTests
{
    private const string Password = "correct horse battery";

    private static AppSettings Settings(
        bool inactivity = true,
        int minutes = 5,
        bool lockOnMinimize = false) => new()
        {
            AutoLockOnInactivity = inactivity,
            InactivityTimeout = TimeSpan.FromMinutes(minutes),
            LockOnMinimize = lockOnMinimize,
        };

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static VaultService NewVault() => new(
        VaultCryptor.CreateDefault(),
        new InMemoryVaultStorage(),
        Argon2Parameters.ForTesting,
        NullLogger<VaultService>.Instance);

    private static AutoLockController NewController(VaultService vault, TestTimeProvider time, AppSettings settings)
        => new(vault, settings, time, NullLogger<AutoLockController>.Instance);

    /// <summary>A wall clock and a monotonic clock that move separately, as they do when someone sets the time.</summary>
    private sealed class TwoClocks : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch.AddYears(56);
        public TimeSpan Elapsed { get; set; }

        public override DateTimeOffset GetUtcNow() => Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;
    }

    [Fact]
    public async Task Inactivity_StillLocksOnTime_WhenTheWallClockIsSetBack()
    {
        var time = new TwoClocks();
        VaultService vault = NewVault();
        using AutoLockController controller = new(vault, Settings(minutes: 15), time, NullLogger<AutoLockController>.Instance);
        await vault.CreateVaultAsync(Password);

        time.Elapsed += TimeSpan.FromMinutes(5);
        time.Utc += TimeSpan.FromMinutes(5) - TimeSpan.FromHours(1); // an NTP correction, or a hand, moves it back an hour
        controller.CheckInactivity();
        Assert.Equal(VaultState.Unlocked, vault.State);

        time.Elapsed += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        time.Utc += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        controller.CheckInactivity();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Inactivity_LocksAfterASleepTheMonotonicClockDidNotSee()
    {
        var time = new TwoClocks();
        VaultService vault = NewVault();
        using AutoLockController controller = new(vault, Settings(minutes: 15), time, NullLogger<AutoLockController>.Instance);
        await vault.CreateVaultAsync(Password);

        time.Utc += TimeSpan.FromHours(8); // the lid was closed overnight; the monotonic clock stood still
        controller.CheckInactivity();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Inactivity_LocksAfterTimeout()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 5));

        await vault.CreateVaultAsync(Password);
        Assert.True(controller.IsArmed);

        time.Utc += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        controller.CheckInactivity();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Inactivity_DoesNotLockBeforeTimeout()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 5));

        await vault.CreateVaultAsync(Password);
        time.Utc += TimeSpan.FromMinutes(4);
        controller.CheckInactivity();

        Assert.Equal(VaultState.Unlocked, vault.State);
    }

    [Fact]
    public async Task NotifyActivity_ResetsTheInactivityClock()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 5));

        await vault.CreateVaultAsync(Password);

        time.Utc += TimeSpan.FromMinutes(4);
        controller.NotifyActivity();               // reset
        time.Utc += TimeSpan.FromMinutes(4);        // 4 min since last activity
        controller.CheckInactivity();
        Assert.Equal(VaultState.Unlocked, vault.State);

        time.Utc += TimeSpan.FromMinutes(2);        // now 6 min since last activity
        controller.CheckInactivity();
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Inactivity_Disabled_NeverLocks()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(inactivity: false));

        await vault.CreateVaultAsync(Password);
        time.Utc += TimeSpan.FromHours(10);
        controller.CheckInactivity();

        Assert.Equal(VaultState.Unlocked, vault.State);
    }

    [Fact]
    public async Task SessionLocked_LocksImmediately()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings());

        await vault.CreateVaultAsync(Password);
        controller.OnSessionLocked();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Suspending_LocksImmediately()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings());

        await vault.CreateVaultAsync(Password);
        controller.OnSystemSuspending();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Minimize_LocksOnlyWhenEnabled()
    {
        var time = new TestTimeProvider();

        VaultService offVault = NewVault();
        using (AutoLockController off = NewController(offVault, time, Settings(lockOnMinimize: false)))
        {
            await offVault.CreateVaultAsync(Password);
            off.OnWindowMinimized();
            Assert.Equal(VaultState.Unlocked, offVault.State);
        }

        VaultService onVault = NewVault();
        using (AutoLockController on = NewController(onVault, time, Settings(lockOnMinimize: true)))
        {
            await onVault.CreateVaultAsync(Password);
            on.OnWindowMinimized();
            Assert.Equal(VaultState.Locked, onVault.State);
        }
    }

    [Fact]
    public async Task WhenAlreadyLocked_TriggersAreNoOps()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 5));

        await vault.CreateVaultAsync(Password);
        vault.Lock(); // manual lock
        Assert.False(controller.IsArmed);

        // None of these should throw or change state.
        time.Utc += TimeSpan.FromHours(1);
        controller.CheckInactivity();
        controller.OnSessionLocked();
        controller.OnSystemSuspending();
        controller.OnWindowMinimized();

        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Reunlock_ReArmsAndResetsClock()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 5));

        await vault.CreateVaultAsync(Password);
        controller.OnSessionLocked();
        Assert.Equal(VaultState.Locked, vault.State);

        await vault.UnlockAsync(Password);
        Assert.True(controller.IsArmed);

        time.Utc += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        controller.CheckInactivity();
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public void BeforeUnlock_TriggersDoNothing()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault(); // NoVault state
        using AutoLockController controller = NewController(vault, time, Settings());

        Assert.False(controller.IsArmed);
        controller.OnSessionLocked();
        controller.OnSystemSuspending();
        controller.CheckInactivity();

        Assert.Equal(VaultState.NoVault, vault.State);
    }

    [Fact]
    public async Task UpdateSettings_ShorterTimeout_TakesEffect()
    {
        var time = new TestTimeProvider();
        VaultService vault = NewVault();
        using AutoLockController controller = NewController(vault, time, Settings(minutes: 30));

        await vault.CreateVaultAsync(Password);
        controller.UpdateSettings(Settings(minutes: 1));

        time.Utc += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        controller.CheckInactivity();

        Assert.Equal(VaultState.Locked, vault.State);
    }
}
