using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BitosX.App.Tests;

/// <summary>
/// Runs the application's real views without a display. The views are loaded from the same XAML,
/// with the same styles, resources and view locator as in the running application; what is missing
/// is a window on a screen. This tests what a view shows and does, not how it looks.
/// </summary>
internal static class HeadlessUi
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntryPoint)));

    /// <summary>Runs <paramref name="test"/> on the UI thread of a headless application.</summary>
    public static Task RunAsync(Func<Task> test)
        => Session.Value.Dispatch(
            async () =>
            {
                await test().ConfigureAwait(true);
                return true;
            },
            CancellationToken.None);

    /// <summary>Shows <paramref name="content"/> in a window and lets layout and bindings settle.</summary>
    public static Window Show(Control content)
    {
        var window = new Window { Width = 1000, Height = 700, Content = content };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Runs everything that is queued on the UI thread.</summary>
    public static void Settle() => Dispatcher.UIThread.RunJobs();

    /// <summary>The named control, which must exist.</summary>
    public static T Find<T>(Control root, string name)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().SingleOrDefault(c => c.Name == name)
           ?? throw new InvalidOperationException($"The view has no {typeof(T).Name} named '{name}'.");

    /// <summary>Whether the control is shown: it and everything above it is visible.</summary>
    public static bool IsShown(Control control) => control.IsEffectivelyVisible;

    /// <summary>Types into a text box the way a user does: focus, then text input.</summary>
    public static void Type(Window window, TextBox box, string text)
    {
        box.Focus();
        Settle();
        window.KeyTextInput(text);
        Settle();
    }

    /// <summary>Activates a button from the keyboard: focus, then Enter.</summary>
    public static void Press(Window window, Button button)
    {
        if (!button.IsEffectivelyEnabled)
        {
            throw new InvalidOperationException($"The button '{button.Name}' is disabled.");
        }

        button.Focus();
        Settle();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle();
    }

    /// <summary>Toggles a check box from the keyboard: focus, then Space.</summary>
    public static void Toggle(Window window, CheckBox checkBox)
    {
        checkBox.Focus();
        Settle();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Settle();
    }

    private static class HeadlessEntryPoint
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
