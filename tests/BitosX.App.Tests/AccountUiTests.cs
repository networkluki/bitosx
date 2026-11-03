using Avalonia.Controls;
using Avalonia.Threading;
using BitosX.ApiClient;
using BitosX.App.Services;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Services;

namespace BitosX.App.Tests;

/// <summary>
/// The Account page as rendered, against a server whose answers complete on other threads the way
/// real HTTP calls do, and the account features added in 0.11.0: changing the account password,
/// deleting the account, the two-factor state, and ending the session when BitosX closes.
/// </summary>
public sealed class AccountUiTests
{
    private const string MasterPassword = "correct horse battery staple";

    private static async Task<WholeApplication> UnlockedAsync(bool answersOnAnotherThread = true)
    {
        var app = new WholeApplication();
        app.Server.AnswersOnAnotherThread = answersOnAnotherThread;
        await app.StartAsync(1000, 3600);
        await app.WaitForAsync<CreateVaultView>();
        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
        return app;
    }

    private static async Task SignInAsync(WholeApplication app)
    {
        app.Press("AccountNavButton");
        await app.WaitForAsync<AccountPageView>();
        app.Type("AccountEmailBox", ScriptedServer.Email);
        app.Type("AccountPasswordBox", ScriptedServer.Password);
        app.Press("SignInButton");
        await app.WaitForTextAsync("AccountStatus", "Signed in.");
    }

    [Fact]
    public Task ASessionChangeRaisedOnAnotherThread_ReachesThePage_OnTheUiThread() => HeadlessUi.RunAsync(async () =>
    {
        var session = new FakeAccountSession();
        using var page = new AccountPageViewModel(session, new FakeVaultSyncService(), new AvaloniaUiDispatcher(), new FakeLinkOpener());
        Window window = HeadlessUi.Show(new AccountPageView { DataContext = page }, 1000, 3000);
        var offThread = new List<string>();
        void Watch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                lock (offThread)
                {
                    offThread.Add(e.PropertyName ?? "?");
                }
            }
        }

        page.PropertyChanged += Watch;
        page.Sync.PropertyChanged += Watch;

        await Task.Run(() => session.Set(true)).ConfigureAwait(true);
        await HeadlessUi.WaitUntilAsync(() => page.IsSignedIn && page.Sync.IsSignedIn, "the page follows the sign-in");
        Assert.True(HeadlessUi.Find<Button>(window, "SyncNowButton").IsEffectivelyEnabled);
        Assert.True(HeadlessUi.Find<Border>(window, "AccountPanel").IsEffectivelyVisible);

        await Task.Run(() => session.Set(false)).ConfigureAwait(true);
        await HeadlessUi.WaitUntilAsync(() => !page.IsSignedIn && !page.Sync.IsSignedIn, "the page follows the sign-out");
        Assert.True(HeadlessUi.Find<Border>(window, "SignInPanel").IsEffectivelyVisible);

        Assert.Empty(offThread);
        window.Close();
    });

    [Fact]
    public Task SigningIn_Syncing_AndSigningOut_WorkWhenTheServerAnswersOnOtherThreads() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await SignInAsync(app);

        Assert.Equal(ScriptedServer.Email, app.Find<SelectableTextBlock>("ProfileEmail").Text);
        await HeadlessUi.WaitUntilAsync(() => app.Find<ItemsControl>("SessionsList").ItemCount == 2, "the sessions are listed");
        Assert.True(app.Shows("SyncPanel"));

        app.Press("SyncNowButton");
        await app.WaitForTextAsync("SyncStatus", "This device's vault was uploaded.");

        app.Press("SignOutButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("SignInPanel"), "the sign-in form is back");
        Assert.Equal(VaultState.Unlocked, app.Vault.State); // nothing went wrong on the way: no fault locked the vault
        Assert.Equal(1, app.Server.LogoutCount);
    });

    [Fact]
    public Task LockingTheVault_EndsTheAccountSessionOnTheServer() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await SignInAsync(app);

        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();

        await HeadlessUi.WaitUntilAsync(() => app.Server.LogoutCount == 1, "the server is told");
    });

    [Fact]
    public Task ClosingBitosX_EndsTheAccountSessionOnTheServer() => HeadlessUi.RunAsync(async () =>
    {
        WholeApplication app = await UnlockedAsync(answersOnAnotherThread: false);
        try
        {
            await SignInAsync(app);
            Assert.Equal(0, app.Server.LogoutCount);

            await app.StopAsync();

            Assert.Equal(1, app.Server.LogoutCount);
        }
        finally
        {
            await app.DisposeAsync();
        }
    });

    [Fact]
    public Task TheTwoFactorState_IsShown_AndTheCodeFieldsFollowIt() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        app.Server.TwoFactorEnabled = true;
        await SignInAsync(app);

        await app.WaitForTextAsync("TwoFactorSummary", "On since");
        Assert.True(app.Shows("ChangePasswordCodeBox"));
        Assert.True(app.Shows("DeleteAccountCodeBox"));
        Assert.False(app.Shows("ForgotPasswordButton")); // not the production server
    });

    [Fact]
    public Task ChangingTheAccountPassword_KeepsThisDeviceSignedIn() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await SignInAsync(app);
        await app.WaitForTextAsync("TwoFactorSummary", "Off.");
        Assert.False(app.Shows("ChangePasswordCodeBox"));

        app.Type("CurrentAccountPasswordBox", ScriptedServer.Password);
        app.Type("NewAccountPasswordBox", "a brand new account password");
        app.Type("RepeatAccountPasswordBox", "a brand new account password");
        app.Press("ChangeAccountPasswordButton");

        await app.WaitForTextAsync("ChangePasswordStatus", "this device stays signed in");
        Assert.Equal(("correct horse battery", "a brand new account password", (string?)null), Assert.Single(app.Server.PasswordChanges));
        Assert.True(app.Shows("AccountPanel"));
        await HeadlessUi.WaitUntilAsync(() => app.Find<ItemsControl>("SessionsList").ItemCount == 1, "the other session is gone from the list");
        Assert.Equal(string.Empty, app.Find<TextBox>("CurrentAccountPasswordBox").Text);
    });

    [Fact]
    public Task DeletingTheAccount_SignsOut_AndLeavesTheVault() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        await SignInAsync(app);
        byte[] vaultBefore = app.VaultFile;

        app.Type("DeleteAccountPasswordBox", ScriptedServer.Password);
        Assert.False(app.Find<Button>("DeleteAccountButton").IsEffectivelyEnabled);
        HeadlessUi.Toggle(app.Window, app.Find<CheckBox>("DeleteAccountAcknowledgeCheckBox"));
        app.Press("DeleteAccountButton");

        await HeadlessUi.WaitUntilAsync(() => app.Shows("SignInPanel"), "signed out");
        await app.WaitForTextAsync("AccountStatus", "vault on this device was not touched");
        Assert.Equal(1, app.Server.AccountDeletions);
        Assert.Equal(vaultBefore, app.VaultFile);
        Assert.Equal(VaultState.Unlocked, app.Vault.State);
    });
}
