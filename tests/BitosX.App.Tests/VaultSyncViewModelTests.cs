using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.App.ViewModels;
using BitosX.Crypto;

namespace BitosX.App.Tests;

/// <summary>
/// The Sync card over a scripted sync service: what each outcome opens, that no replacement happens
/// without the acknowledgement, that the master password never stays on the page, and what each
/// failure says.
/// </summary>
public sealed class VaultSyncViewModelTests
{
    private static readonly SyncStatus Server = new(3, new string('a', 64), 2048, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    private static (VaultSyncViewModel Card, FakeVaultSyncService Sync, FakeAccountSession Session) New(bool signedIn = true)
    {
        var sync = new FakeVaultSyncService();
        var session = new FakeAccountSession();
        session.Set(signedIn);
        return (new VaultSyncViewModel(sync, session), sync, session);
    }

    [Fact]
    public void SignedOut_NothingCanBePressed()
    {
        var (card, _, _) = New(signedIn: false);
        using (card)
        {
            Assert.False(card.IsSignedIn);
            Assert.False(card.SyncNowCommand.CanExecute(null));
            Assert.False(card.RemoveFromServerCommand.CanExecute(null));
            Assert.False(card.OverwriteServerCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task UpToDate_SaysSo_AndOpensNothing()
    {
        var (card, sync, _) = New();
        using (card)
        {
            await card.SyncNowCommand.ExecuteAsync(null);

            Assert.Equal(1, sync.SyncCount);
            Assert.StartsWith("Up to date", card.Status, StringComparison.Ordinal);
            Assert.Contains("revision 3", card.Summary, StringComparison.Ordinal);
            Assert.False(card.CanTakeServerVault);
            Assert.False(card.IsConflict);
        }
    }

    [Fact]
    public async Task ServerAhead_OffersTheDownload_ButNotTheOverwrite()
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.ServerAhead, Server, DateTimeOffset.UtcNow);
            await card.SyncNowCommand.ExecuteAsync(null);

            Assert.True(card.IsServerAhead);
            Assert.True(card.CanTakeServerVault);
            Assert.False(card.IsConflict);
            Assert.False(card.PrepareDownloadCommand.CanExecute(null)); // no password yet
            card.DownloadMasterPassword = "pw";
            Assert.True(card.PrepareDownloadCommand.CanExecute(null));
            card.ServerChangeAcknowledged = true;
            Assert.False(card.OverwriteServerCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Conflict_OffersBothWaysOut_AndTakesNeither()
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.Conflict, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);

            Assert.True(card.IsConflict);
            Assert.True(card.CanTakeServerVault);
            Assert.Contains("nothing was changed", card.Status, StringComparison.Ordinal);
            Assert.False(card.OverwriteServerCommand.CanExecute(null)); // not acknowledged
            Assert.Equal(0, sync.OverwriteCount);
            Assert.Equal(0, sync.CommitCount);
        }
    }

    [Fact]
    public async Task TheDownload_IsCheckedWithThePassword_WhichIsThenForgotten_AndCommittedOnlyWhenAcknowledged()
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.ServerAhead, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);
            card.DownloadMasterPassword = "the server vault's password";

            await card.PrepareDownloadCommand.ExecuteAsync(null);

            Assert.Equal(["the server vault's password"], sync.PasswordsTried);
            Assert.Equal(string.Empty, card.DownloadMasterPassword);
            Assert.True(card.IsDownloadPending);
            Assert.Contains("7 item(s)", card.DownloadSummary, StringComparison.Ordinal);
            Assert.False(card.DownloadAcknowledged);
            Assert.False(card.ConfirmDownloadCommand.CanExecute(null));

            await card.ConfirmDownloadCommand.ExecuteAsync(null); // the guard inside, not only CanExecute
            Assert.Equal(0, sync.CommitCount);

            card.DownloadAcknowledged = true;
            Assert.True(card.ConfirmDownloadCommand.CanExecute(null));
            await card.ConfirmDownloadCommand.ExecuteAsync(null);

            Assert.Equal(1, sync.CommitCount);
            Assert.Equal(3, sync.Committed!.Server.Revision);
            Assert.False(card.IsDownloadPending);
            Assert.False(card.DownloadAcknowledged);
            Assert.Contains("replaced", card.Status, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheAcknowledgement_CannotBeSetBeforeAnythingIsPending_AndCancelResetsIt()
    {
        var (card, sync, _) = New();
        using (card)
        {
            card.DownloadAcknowledged = true;
            Assert.False(card.DownloadAcknowledged);

            sync.NextResult = new SyncResult(SyncOutcome.Conflict, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);
            card.DownloadMasterPassword = "pw";
            await card.PrepareDownloadCommand.ExecuteAsync(null);
            card.DownloadAcknowledged = true;
            Assert.True(card.DownloadAcknowledged);

            card.CancelDownloadCommand.Execute(null);

            Assert.False(card.IsDownloadPending);
            Assert.False(card.DownloadAcknowledged);
            Assert.False(card.ConfirmDownloadCommand.CanExecute(null));
            Assert.Equal(0, sync.CommitCount);
        }
    }

    [Fact]
    public async Task AWrongPassword_SaysSo_AndChangesNothing()
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.ServerAhead, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);
            sync.Throws = new VaultAuthenticationException();
            card.DownloadMasterPassword = "wrong";

            await card.PrepareDownloadCommand.ExecuteAsync(null);

            Assert.Contains("does not open", card.Status, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", card.Status, StringComparison.Ordinal);
            Assert.False(card.IsDownloadPending);
            Assert.Equal(string.Empty, card.DownloadMasterPassword);
        }
    }

    [Fact]
    public async Task Overwrite_NeedsAConflict_AndTheAcknowledgement_ThenClearsIt()
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.Conflict, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);
            card.ServerChangeAcknowledged = true;
            Assert.True(card.OverwriteServerCommand.CanExecute(null));

            await card.OverwriteServerCommand.ExecuteAsync(null);

            Assert.Equal(1, sync.OverwriteCount);
            Assert.False(card.ServerChangeAcknowledged);
            Assert.False(card.IsConflict);
            Assert.Contains("revision 4", card.Summary, StringComparison.Ordinal);
            Assert.Contains("Every other device", card.Status, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Remove_NeedsTheAcknowledgement()
    {
        var (card, sync, _) = New();
        using (card)
        {
            Assert.False(card.RemoveFromServerCommand.CanExecute(null));
            card.ServerChangeAcknowledged = true;

            await card.RemoveFromServerCommand.ExecuteAsync(null);

            Assert.Equal(1, sync.DeleteCount);
            Assert.False(card.ServerChangeAcknowledged);
            Assert.Contains("removed from the server", card.Status, StringComparison.Ordinal);
            Assert.Contains("unchanged", card.Status, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ApiFailure.Network, "could not be reached")]
    [InlineData(ApiFailure.Unauthenticated, "session has ended")]
    [InlineData(ApiFailure.Unavailable, "temporarily unavailable")]
    [InlineData(ApiFailure.Conflict, "Nothing was changed")]
    [InlineData(ApiFailure.Unexpected, "Nothing was changed")]
    public async Task AServerFailure_IsOneFixedSentence(ApiFailure failure, string expected)
    {
        var (card, sync, _) = New();
        using (card)
        {
            sync.Throws = new ApiException(failure, 500);

            await card.SyncNowCommand.ExecuteAsync(null);

            Assert.Contains(expected, card.Status, StringComparison.Ordinal);
            Assert.False(card.IsBusy);
        }
    }

    [Fact]
    public async Task SigningOut_ClearsTheCard()
    {
        var (card, sync, session) = New();
        using (card)
        {
            sync.NextResult = new SyncResult(SyncOutcome.Conflict, Server, null);
            await card.SyncNowCommand.ExecuteAsync(null);
            card.DownloadMasterPassword = "pw";
            card.ServerChangeAcknowledged = true;

            session.Set(false);

            Assert.False(card.IsSignedIn);
            Assert.False(card.IsConflict);
            Assert.False(card.CanTakeServerVault);
            Assert.Equal(string.Empty, card.DownloadMasterPassword);
            Assert.False(card.ServerChangeAcknowledged);
            Assert.Null(card.Summary);
            Assert.False(card.SyncNowCommand.CanExecute(null));
        }
    }
}
