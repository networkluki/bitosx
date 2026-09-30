using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// What happens when a fault nobody foresaw reaches the user interface: the application fails
/// closed — the vault is locked and a copied secret is cleared — and tells the user, instead of
/// ending on the spot with the secret still on the clipboard.
/// </summary>
public sealed class UnexpectedErrorGuardTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<UnexpectedErrorGuard>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }

    private static (UnexpectedErrorGuard Guard, FakeVaultService Vault, FakeClipboardService Clipboard, AppNotice Notice, Clock Clock)
        NewGuard(ILogger<UnexpectedErrorGuard>? logger = null)
    {
        var vault = new FakeVaultService { State = VaultState.Unlocked };
        var clipboard = new FakeClipboardService();
        var notice = new AppNotice();
        var clock = new Clock();
        var guard = new UnexpectedErrorGuard(
            vault, clipboard, notice, clock, logger ?? NullLogger<UnexpectedErrorGuard>.Instance);
        return (guard, vault, clipboard, notice, clock);
    }

    [Fact]
    public void AFault_LocksTheVault_AndTellsTheUser()
    {
        (UnexpectedErrorGuard guard, FakeVaultService vault, _, AppNotice notice, _) = NewGuard();

        bool goOn = guard.Recover(new InvalidOperationException("boom"));

        Assert.True(goOn);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(notice.IsShown);
        Assert.Equal(UnexpectedErrorGuard.Notice, notice.Message);
    }

    [Fact]
    public async Task AFault_ClearsACopiedSecret()
    {
        (UnexpectedErrorGuard guard, _, FakeClipboardService clipboard, _, _) = NewGuard();
        await clipboard.CopySecretAsync("a copied password");
        Assert.True(clipboard.HasPendingSecret);

        guard.Recover(new InvalidOperationException("boom"));

        Assert.False(clipboard.HasPendingSecret);
    }

    [Fact]
    public void WhatTheFaultSays_IsNeitherShownNorLogged()
    {
        var logger = new RecordingLogger();
        (UnexpectedErrorGuard guard, _, _, AppNotice notice, _) = NewGuard(logger);

        guard.Recover(new InvalidOperationException("the password was hunter2"));

        Assert.DoesNotContain("hunter2", notice.Message!, StringComparison.Ordinal);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public void AVaultThatCannotBeLocked_DoesNotStopTheRest()
    {
        (UnexpectedErrorGuard guard, FakeVaultService vault, _, AppNotice notice, _) = NewGuard();
        vault.LockThrows = new InvalidOperationException("locking failed");

        bool goOn = guard.Recover(new InvalidOperationException("boom"));

        Assert.True(goOn);
        Assert.True(notice.IsShown);
    }

    [Fact]
    public void FaultsThatKeepComing_AreLetThrough_WithTheVaultLocked()
    {
        (UnexpectedErrorGuard guard, FakeVaultService vault, _, _, Clock clock) = NewGuard();

        for (int i = 0; i < UnexpectedErrorGuard.MaxFaults; i++)
        {
            Assert.True(guard.Recover(new InvalidOperationException("boom")));
            clock.Now += TimeSpan.FromSeconds(1);
        }

        vault.State = VaultState.Unlocked;
        bool goOn = guard.Recover(new InvalidOperationException("boom"));

        Assert.False(goOn);
        Assert.Equal(VaultState.Locked, vault.State); // failing closed comes first, whatever follows
    }

    [Fact]
    public void FaultsThatAreFarApart_AreEachRecoveredFrom()
    {
        (UnexpectedErrorGuard guard, _, _, _, Clock clock) = NewGuard();

        for (int i = 0; i < UnexpectedErrorGuard.MaxFaults * 3; i++)
        {
            Assert.True(guard.Recover(new InvalidOperationException("boom")));
            clock.Now += UnexpectedErrorGuard.FaultWindow + TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public void TheNotice_CanBeDismissed_AndReplaced()
    {
        var notice = new AppNotice();
        Assert.False(notice.IsShown);

        notice.Show("first");
        notice.Show("second");
        Assert.Equal("second", notice.Message);

        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsShown);
        Assert.Null(notice.Message);
    }
}
