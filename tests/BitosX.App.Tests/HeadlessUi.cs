using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BitosX.App.Tests;

/// <summary>
/// Runs the application's real views without a display. The views are loaded from the same XAML,
/// with the same styles, resources, fonts and view locator as in the running application, and are
/// measured and drawn by the same renderer; what is missing is a window on a screen.
/// </summary>
/// <remarks>
/// Set the environment variable <c>BITOSX_VIEW_SNAPSHOTS</c> to a directory to have
/// <see cref="Snapshot"/> write what was drawn there as PNG files.
/// </remarks>
internal static class HeadlessUi
{
    /// <summary>The environment variable that names the directory for <see cref="Snapshot"/>.</summary>
    public const string SnapshotDirectoryVariable = "BITOSX_VIEW_SNAPSHOTS";

    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntryPoint)));

    // The tests share one application: one UI thread, one keyboard focus, one clipboard. They run
    // one at a time, so that none of them sees what another one copied or focused.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// Runs <paramref name="test"/> on the UI thread of a headless application, after every other
    /// test that runs there has finished.
    /// </summary>
    public static async Task RunAsync(Func<Task> test)
    {
        await OneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            await Session.Value.Dispatch(
                async () =>
                {
                    await test().ConfigureAwait(true);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Shows <paramref name="content"/> in a window and lets layout and bindings settle.</summary>
    public static Window Show(Control content, double width = 1000, double height = 700)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
            Background = Application.Current!.FindResource("BitosXBackgroundBrush") as IBrush,
        };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Runs everything that is queued on the UI thread.</summary>
    public static void Settle() => Dispatcher.UIThread.RunJobs();

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, the way a user waits for the screen to
    /// change, while the UI thread keeps running.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutSeconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            Settle();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {timeoutSeconds} s, but this did not happen: {what}.");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>As <see cref="WaitUntilAsync(Func{bool}, string, int)"/>, for a condition that has to be awaited.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            Settle();
            if (await condition().ConfigureAwait(true))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {timeoutSeconds} s, but this did not happen: {what}.");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>The named control, which must exist.</summary>
    public static T Find<T>(Control root, string name)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().SingleOrDefault(c => c.Name == name)
           ?? throw new InvalidOperationException($"The view has no {typeof(T).Name} named '{name}'.");

    /// <summary>The one control of type <typeparamref name="T"/> that is shown, which must exist.</summary>
    public static T FindShown<T>(Control root)
        where T : Control
    {
        T[] found = [.. root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible)];
        return found.Length == 1
            ? found[0]
            : throw new InvalidOperationException($"Expected one {typeof(T).Name} on screen, found {found.Length}.");
    }

    /// <summary>The button that is shown with <paramref name="text"/> on it, which must exist.</summary>
    public static Button FindButton(Control root, string text)
    {
        Button[] found =
        [
            .. root.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible && b.Content is string s && s.Contains(text, StringComparison.Ordinal)),
        ];
        return found.Length == 1
            ? found[0]
            : throw new InvalidOperationException($"Expected one button '{text}' on screen, found {found.Length}.");
    }

    /// <summary>Every text that is shown, in the order of the visual tree.</summary>
    public static IReadOnlyList<string> ShownTexts(Control root)
        =>
        [
            .. root.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text!),
            .. root.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text!),
        ];

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

    /// <summary>Replaces what is in a text box: focus, select all, then text input.</summary>
    public static void Retype(Window window, TextBox box, string text)
    {
        box.Focus();
        box.SelectAll();
        Settle();
        window.KeyTextInput(text);
        Settle();
    }

    /// <summary>Activates a button from the keyboard: focus, then Enter.</summary>
    public static void Press(Window window, Button button)
    {
        if (!button.IsEffectivelyEnabled)
        {
            throw new InvalidOperationException($"The button '{button.Name ?? button.Content}' is disabled.");
        }

        button.Focus();
        Settle();
        Key(window, PhysicalKey.Enter);
    }

    /// <summary>Clicks in the middle of a control with the mouse.</summary>
    public static void Click(Window window, Control control)
    {
        if (!control.IsEffectivelyEnabled)
        {
            throw new InvalidOperationException($"The control '{control.Name}' is disabled.");
        }

        Point? middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (middle is not { } point)
        {
            throw new InvalidOperationException($"The control '{control.Name}' is not in the window.");
        }

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    /// <summary>Presses and releases a key in whatever has the focus.</summary>
    public static void Key(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Settle();
    }

    /// <summary>Toggles a check box from the keyboard: focus, then Space.</summary>
    public static void Toggle(Window window, CheckBox checkBox)
    {
        checkBox.Focus();
        Settle();
        Key(window, PhysicalKey.Space);
    }

    /// <summary>
    /// Writes what the window draws to <c>&lt;name&gt;.png</c> in the directory named by
    /// <see cref="SnapshotDirectoryVariable"/>. Does nothing when the variable is not set.
    /// </summary>
    public static void Snapshot(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable(SnapshotDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        Settle();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static class HeadlessEntryPoint
    {
        // The renderer and the font of the running application, so that text is measured and
        // drawn as it is there.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
    }
}
