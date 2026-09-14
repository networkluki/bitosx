using BitosX.Core.Services;

namespace BitosX.Core.Tests;

public sealed class RestoreConfirmationStateTests
{
    [Fact]
    public void Initial_CannotCommit()
    {
        var s = new RestoreConfirmationState();
        Assert.False(s.HasPreview);
        Assert.False(s.Acknowledged);
        Assert.False(s.CanCommit);
    }

    [Fact]
    public void Prepared_ShowsPreview_ButCannotCommitUntilAcknowledged()
    {
        var s = new RestoreConfirmationState();
        s.SetPrepared();
        Assert.True(s.HasPreview);
        Assert.False(s.Acknowledged);
        Assert.False(s.CanCommit); // Replace stays disabled
    }

    [Fact]
    public void Acknowledged_AfterPreview_EnablesCommit()
    {
        var s = new RestoreConfirmationState();
        s.SetPrepared();
        s.SetAcknowledged(true);
        Assert.True(s.CanCommit);
    }

    [Fact]
    public void Acknowledge_WithoutPreview_DoesNotEnableCommit()
    {
        var s = new RestoreConfirmationState();
        s.SetAcknowledged(true);
        Assert.False(s.Acknowledged);
        Assert.False(s.CanCommit);
    }

    [Fact]
    public void ReValidate_ResetsAcknowledgement()
    {
        var s = new RestoreConfirmationState();
        s.SetPrepared();
        s.SetAcknowledged(true);
        Assert.True(s.CanCommit);

        s.SetPrepared(); // a new validation
        Assert.True(s.HasPreview);
        Assert.False(s.Acknowledged);
        Assert.False(s.CanCommit);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var s = new RestoreConfirmationState();
        s.SetPrepared();
        s.SetAcknowledged(true);

        s.Reset(); // file change / password change / cancel / validation failure / commit
        Assert.False(s.HasPreview);
        Assert.False(s.Acknowledged);
        Assert.False(s.CanCommit);
    }

    [Fact]
    public void Unacknowledge_DisablesCommit()
    {
        var s = new RestoreConfirmationState();
        s.SetPrepared();
        s.SetAcknowledged(true);
        Assert.True(s.CanCommit);

        s.SetAcknowledged(false);
        Assert.False(s.CanCommit);
    }
}
