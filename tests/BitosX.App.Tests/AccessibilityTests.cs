using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using BitosX.App.Views;

namespace BitosX.App.Tests;

/// <summary>
/// What the screens tell a screen reader. Before 0.7.0 every input field was a nameless "edit":
/// whoever cannot see the label next to a field was not told what to type into it.
/// </summary>
public sealed class AccessibilityTests
{
    private const string MasterPassword = "correct horse battery staple";

    /// <summary>What a person types into or chooses from, and has to be told the purpose of.</summary>
    private static bool IsInput(Control control)
        => control is TextBox or ComboBox or Slider or NumericUpDown or CheckBox or Button;

    private static bool IsPartOfAnotherControl(Control control)
        => control.GetVisualAncestors().Any(a => a is TextBox or NumericUpDown or Slider or ComboBox or ScrollBar);

    private static IReadOnlyList<string> Nameless(Window window)
        =>
        [
            .. window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible && IsInput(c) && !IsPartOfAnotherControl(c))
                .Where(c => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(c).GetName()))
                .Select(c => $"{c.GetType().Name} '{c.Name}'"),
        ];

    private static async Task<WholeApplication> UnlockedAsync()
    {
        var app = new WholeApplication();
        await app.StartAsync(1000, 3200); // tall enough for the whole of every page
        await app.WaitForAsync<CreateVaultView>();
        Assert.Empty(Nameless(app.Window));

        app.Type("MasterPasswordBox", MasterPassword);
        app.Type("ConfirmPasswordBox", MasterPassword);
        app.Press("CreateButton");
        await app.WaitForAsync<UnlockedShellView>();
        return app;
    }

    [Fact]
    public Task EveryInput_OnEveryScreen_HasAName() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();

        // The vault page, with an item and with the editor open.
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        Assert.Empty(Nameless(app.Window));
        app.Type("NameBox", "GitHub");
        app.Type("UsernameBox", "octocat@example.com");
        app.Type("PasswordBox", "P@ssw0rd-Strong-123");
        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");
        Assert.Empty(Nameless(app.Window));

        app.Press("GeneratorNavButton");
        await app.WaitForAsync<GeneratorPageView>();
        Assert.Empty(Nameless(app.Window));

        app.Press("SecurityNavButton");
        await app.WaitForAsync<SecurityPageView>();
        Assert.Empty(Nameless(app.Window));

        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();
        Assert.Empty(Nameless(app.Window));

        app.Press("AccountNavButton");
        await app.WaitForAsync<AccountPageView>();
        Assert.Empty(Nameless(app.Window));

        // Signed in, with the sync card in its fullest state: a conflict, the download pending.
        app.Type("AccountEmailBox", ScriptedServer.Email);
        app.Type("AccountPasswordBox", ScriptedServer.Password);
        app.Press("SignInButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("SyncPanel"), "the account is shown");
        app.Server.Store(System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"other\":true}"));
        app.Press("SyncNowButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("TakeServerVaultPanel"), "the conflict is shown");
        Assert.Empty(Nameless(app.Window));

        app.Press("LockButton");
        await app.WaitForAsync<UnlockView>();
        Assert.Empty(Nameless(app.Window));
    });

    [Fact]
    public Task TheStar_SaysWhatItDoes() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        app.Press("AddLoginButton");
        await HeadlessUi.WaitUntilAsync(() => app.Shows("NameBox"), "the editor opens");
        app.Type("NameBox", "GitHub");
        app.Press("SaveButton");
        await HeadlessUi.WaitUntilAsync(() => !app.Shows("NameBox"), "the editor closes");

        Button star = app.FindInItem<Button>("GitHub", "FavoriteButton");
        Assert.Equal("Add GitHub to favorites", ControlAutomationPeer.CreatePeerForElement(star).GetName());

        HeadlessUi.Click(app.Window, star);
        await HeadlessUi.WaitUntilAsync(
            () => (string?)app.FindInItem<Button>("GitHub", "FavoriteButton").Content == "★", "the favourite is marked");
        Assert.Equal(
            "Remove GitHub from favorites",
            ControlAutomationPeer.CreatePeerForElement(app.FindInItem<Button>("GitHub", "FavoriteButton")).GetName());
    });

    [Fact]
    public Task ThePasswordFields_AreToldApart() => HeadlessUi.RunAsync(async () =>
    {
        await using WholeApplication app = await UnlockedAsync();
        app.Press("SettingsNavButton");
        await app.WaitForAsync<SettingsPageView>();

        string?[] names =
        [
            .. app.Window.GetVisualDescendants().OfType<TextBox>()
                .Where(box => box.PasswordChar != default && box.IsEffectivelyVisible)
                .Select(box => ControlAutomationPeer.CreatePeerForElement(box).GetName()),
        ];

        Assert.Equal(5, names.Length); // the sixth, for a backup, is shown once a backup is chosen
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    });
}
