using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BitosX.App.Controls;
using BitosX.App.Views;

namespace BitosX.App.Tests;

/// <summary>
/// What a password field tells the accessibility interface of the operating system. Before 0.7.0
/// the fields were ordinary text boxes that showed dots, and the framework hands the text of a
/// text box to that interface whatever it shows: the master password could be read, in the clear,
/// by any program of the same user that asked.
/// </summary>
public sealed class SecretBoxTests
{
    private const string Secret = "correct horse battery staple";

    private static IValueProvider Interface(Control control)
        => Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(control));

    [Fact]
    public Task AnOrdinaryTextBox_HandsOutItsText_WhateverItShows() => HeadlessUi.RunAsync(() =>
    {
        // The behaviour of the framework that made the secret box necessary. If this ever fails,
        // the framework has changed and the secret box can be looked at again.
        var box = new TextBox { PasswordChar = '●', Text = Secret };
        HeadlessUi.Show(box);

        Assert.Equal(Secret, Interface(box).Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ASecretBox_HandsOutWhatItShows_TheDots() => HeadlessUi.RunAsync(() =>
    {
        var box = new SecretBox { Text = Secret };
        HeadlessUi.Show(box);

        string? told = Interface(box).Value;

        Assert.Equal(new string('●', Secret.Length), told);
        Assert.DoesNotContain("correct", told!, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ASecretBox_ThatIsEmpty_HandsOutNothing() => HeadlessUi.RunAsync(() =>
    {
        var box = new SecretBox();
        HeadlessUi.Show(box);

        Assert.Equal(string.Empty, Interface(box).Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ASecretBox_ThatTheUserRevealed_HandsOutTheText_AndHidesItAgain() => HeadlessUi.RunAsync(() =>
    {
        var box = new SecretBox { Text = Secret };
        HeadlessUi.Show(box);

        box.RevealPassword = true;
        Assert.Equal(Secret, Interface(box).Value);

        box.RevealPassword = false;
        Assert.Equal(new string('●', Secret.Length), Interface(box).Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TextCanBeEntered_ThroughTheInterface() => HeadlessUi.RunAsync(() =>
    {
        var box = new SecretBox();
        HeadlessUi.Show(box);

        Interface(box).SetValue(Secret);

        Assert.Equal(Secret, box.Text);
        Assert.Equal(new string('●', Secret.Length), Interface(box).Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TypingIntoASecretBox_WorksAsInATextBox() => HeadlessUi.RunAsync(() =>
    {
        var box = new SecretBox();
        Window window = HeadlessUi.Show(box);

        HeadlessUi.Type(window, box, Secret);

        Assert.Equal(Secret, box.Text);
        Assert.Equal('●', box.PasswordChar);
        Assert.IsType<SecretBoxAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(box));
        return Task.CompletedTask;
    });

    // ---- Every field that hides its text is a secret box ----

    public static TheoryData<Type> ViewsWithPasswords => new()
    {
        typeof(CreateVaultView),
        typeof(UnlockView),
        typeof(LoginEditorView),
        typeof(SettingsPageView),
    };

    [Theory]
    [MemberData(nameof(ViewsWithPasswords))]
    public Task EveryField_ThatShowsDots_IsASecretBox(Type viewType) => HeadlessUi.RunAsync(() =>
    {
        var view = (Control)Activator.CreateInstance(viewType)!;
        HeadlessUi.Show(view, 1000, 3200);

        TextBox[] hidden = [.. view.GetVisualDescendants().OfType<TextBox>().Where(box => box.PasswordChar != default)];

        Assert.NotEmpty(hidden);
        Assert.All(hidden, box => Assert.IsType<SecretBox>(box));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMasterPassword_IsNotHandedOut_WhileItIsTyped() => HeadlessUi.RunAsync(async () =>
    {
        await using var app = new WholeApplication();
        await app.StartAsync();

        app.Type("MasterPasswordBox", Secret);
        app.Type("ConfirmPasswordBox", Secret);

        foreach (Control control in app.Window.GetVisualDescendants().OfType<Control>())
        {
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
            Assert.DoesNotContain(Secret, peer.GetName() ?? string.Empty, StringComparison.Ordinal);
            if (peer.GetProvider<IValueProvider>() is { } value)
            {
                Assert.DoesNotContain(Secret, value.Value ?? string.Empty, StringComparison.Ordinal);
            }
        }
    });
}
