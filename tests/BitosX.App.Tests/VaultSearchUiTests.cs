using System.Diagnostics;
using System.Globalization;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using BitosX.App.ViewModels;
using BitosX.App.Views;
using BitosX.Core.Domain;
using BitosX.Core.Generation;
using Xunit.Abstractions;

namespace BitosX.App.Tests;

/// <summary>
/// Searching a large vault, as rendered: each keystroke shows and hides the cards that are already
/// there instead of building the list again, the focus stays in the search box, and what is hidden
/// is hidden from a screen reader too. The time each keystroke takes is written to the test output
/// (run with <c>--logger "console;verbosity=detailed"</c> to see it); it is reported, not asserted,
/// because it depends on the machine.
/// </summary>
public sealed class VaultSearchUiTests(ITestOutputHelper output)
{
    private const int ManyItems = 5_000;

    private static VaultPageViewModel PageWith(int count)
    {
        var vault = new FakeVaultService();
        for (int i = 0; i < count; i++)
        {
            vault.Stored.Add(new LoginItem
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Site {i:D4}"),
                Username = string.Create(CultureInfo.InvariantCulture, $"user{i:D4}@example.test"),
                Password = "correct horse battery staple",
                Urls = [string.Create(CultureInfo.InvariantCulture, $"https://site{i:D4}.example.test")],
            });
        }

        var page = new VaultPageViewModel(vault, new FakeClipboardService(), new PasswordGenerator());
        page.RefreshAsync();
        return page;
    }

    /// <summary>Every card of the list that exists, shown or not.</summary>
    private static List<Border> Cards(Control view)
        => [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card") && b.DataContext is LoginItemViewModel)];

    private static List<string> ShownNames(Control view)
        =>
        [
            .. Cards(view).Where(c => c.IsEffectivelyVisible)
                .Select(c => ((LoginItemViewModel)c.DataContext!).Name),
        ];

    [Fact]
    public Task TypingInTheSearch_ShowsAndHidesTheCards_WithoutBuildingThemAgain() => HeadlessUi.RunAsync(() =>
    {
        VaultPageViewModel page = PageWith(ManyItems);
        var view = new VaultPageView { DataContext = page };

        var watch = Stopwatch.StartNew();
        Window window = HeadlessUi.Show(view);
        window.UpdateLayout();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ManyItems} items: the page is built and laid out in {watch.ElapsedMilliseconds} ms"));

        TextBox search = HeadlessUi.Find<TextBox>(view, "SearchBox");
        search.Focus();
        HeadlessUi.Settle();
        var known = new HashSet<Border>(Cards(view));

        // Narrowing down to one item, then back to all of them.
        (string Key, int Matches)[] steps =
        [
            ("1", 2_084), ("2", 199), ("3", 15), ("4", 1),
            ("\b", 15), ("\b", 199), ("\b", 2_084), ("\b", ManyItems),
        ];

        long total = 0;
        foreach ((string key, int matches) in steps)
        {
            watch.Restart();
            if (key == "\b")
            {
                HeadlessUi.Key(window, PhysicalKey.Backspace);
            }
            else
            {
                window.KeyTextInput(key);
                HeadlessUi.Settle();
            }

            window.UpdateLayout();
            watch.Stop();
            total += watch.ElapsedMilliseconds;

            List<Border> cards = Cards(view);
            int built = cards.Count(c => !known.Contains(c));
            known.UnionWith(cards);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"search “{search.Text}”: {page.Items.Count} matches, {watch.ElapsedMilliseconds} ms, {built} cards built"));

            Assert.Equal(matches, page.Items.Count);
            Assert.Equal(page.Items.Select(i => i.Name), ShownNames(view));
            Assert.Equal(0, built);
            Assert.Same(search, window.FocusManager?.GetFocusedElement());
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{steps.Length} keystrokes: {total} ms in all"));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task AHiddenCard_IsHiddenFromAScreenReaderToo() => HeadlessUi.RunAsync(() =>
    {
        VaultPageViewModel page = PageWith(3);
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);

        page.SearchText = "Site 0001";
        HeadlessUi.Settle();
        window.UpdateLayout();

        var names = new List<string>();
        Collect(ControlAutomationPeer.CreatePeerForElement(view), names);
        Assert.Contains("Edit Site 0001", names);
        Assert.DoesNotContain("Edit Site 0000", names);
        Assert.DoesNotContain("Edit Site 0002", names);
        Assert.Equal(["Site 0001"], ShownNames(view));
        window.Close();
        return Task.CompletedTask;

        static void Collect(AutomationPeer peer, List<string> into)
        {
            into.Add(peer.GetName());
            foreach (AutomationPeer child in peer.GetChildren())
            {
                Collect(child, into);
            }
        }
    });

    [Fact]
    public Task AnItemSavedWhileASearchIsTyped_IsFilteredLikeTheRest() => HeadlessUi.RunAsync(async () =>
    {
        VaultPageViewModel page = PageWith(3);
        var view = new VaultPageView { DataContext = page };
        Window window = HeadlessUi.Show(view);
        page.SearchText = "0002";
        HeadlessUi.Settle();

        await page.ToggleFavoriteCommand.ExecuteAsync(page.Items[0]);
        HeadlessUi.Settle();
        window.UpdateLayout();

        Assert.Equal(["Site 0002"], page.Items.Select(i => i.Name));
        Assert.Equal(["Site 0002"], ShownNames(view));
        Assert.True(page.Items[0].Favorite);

        page.SearchText = string.Empty;
        HeadlessUi.Settle();
        window.UpdateLayout();
        Assert.Equal(["Site 0002", "Site 0000", "Site 0001"], ShownNames(view));
        window.Close();
    });
}
