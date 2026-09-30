using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace BitosX.App.Tests;

/// <summary>
/// Finds what a person would see as a layout fault in a window that has been laid out: text that
/// is cut off, and controls that are partly or wholly outside what is shown and cannot be
/// scrolled to.
/// </summary>
internal static class ViewLayout
{
    private const double Tolerance = 0.75;

    /// <summary>The faults in <paramref name="window"/>, each described in a sentence. Empty when there are none.</summary>
    public static IReadOnlyList<string> Problems(Window window)
    {
        HeadlessUi.Settle();
        var problems = new List<string>();
        var windowArea = new Rect(0, 0, window.ClientSize.Width, window.ClientSize.Height);

        foreach (Control control in window.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsEffectivelyVisible || !IsChecked(control) || IsPartOfAnotherControl(control))
            {
                continue;
            }

            if (control is TextBlock text)
            {
                CheckText(text, problems);
            }

            CheckPlacement(control, window, windowArea, problems);
        }

        return problems;
    }

    /// <summary>What a person reads or operates; containers are judged by what is in them.</summary>
    private static bool IsChecked(Control control)
        => control is TextBlock or Button or TextBox or CheckBox or ComboBox or NumericUpDown or Slider;

    /// <summary>
    /// Parts of a text box, a number box, a slider, a combo box or a scroll bar lay themselves out
    /// by rules of their own (a text box scrolls what does not fit); the control as a whole is checked.
    /// </summary>
    private static bool IsPartOfAnotherControl(Control control)
        => control.GetVisualAncestors().Any(a => a is TextBox or NumericUpDown or Slider or ComboBox or ScrollBar);

    private static void CheckText(TextBlock text, List<string> problems)
    {
        if (string.IsNullOrEmpty(text.Text) && text.Inlines is not { Count: > 0 })
        {
            return;
        }

        TextLayout layout = text.TextLayout;
        double width = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
        double height = text.Bounds.Height - text.Padding.Top - text.Padding.Bottom;

        if (layout.TextLines.Any(line => line.HasCollapsed))
        {
            // Shortened with an ellipsis, which is a way of showing a text that is too long —
            // provided the whole of it can be read somewhere.
            if (ToolTip.GetTip(text) is not string tip || tip != text.Text)
            {
                problems.Add($"The text {Describe(text)} is shortened and cannot be read in full: it has no tooltip with the whole text.");
            }
        }
        else if (layout.TextLines.Any(line => line.HasOverflowed) || layout.Width > width + Tolerance)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The text {Describe(text)} is cut off at the side: it needs {layout.Width:0.#} and has {width:0.#}."));
        }

        if (layout.Height > height + Tolerance)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The text {Describe(text)} is cut off at the bottom: it needs {layout.Height:0.#} and has {height:0.#}."));
        }
    }

    private static void CheckPlacement(Control control, Window window, Rect windowArea, List<string> problems)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            problems.Add($"The {control.GetType().Name} {Describe(control)} has no size.");
            return;
        }

        if (AreaIn(control, window) is not { } area)
        {
            return;
        }

        // What may be scrolled to need not be in view; the rest must be.
        ScrollViewer? scroller = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Rect shown = scroller is null ? windowArea : AreaIn(scroller, window)?.Intersect(windowArea) ?? windowArea;
        bool scrollsDown = scroller is not null && scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
        bool scrollsSideways = scroller is not null && scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;

        if (!scrollsSideways && (area.Left < shown.Left - Tolerance || area.Right > shown.Right + Tolerance))
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The {control.GetType().Name} {Describe(control)} reaches from {area.Left:0.#} to {area.Right:0.#}, outside what is shown ({shown.Left:0.#} to {shown.Right:0.#})."));
        }

        if (!scrollsDown && (area.Top < shown.Top - Tolerance || area.Bottom > shown.Bottom + Tolerance))
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The {control.GetType().Name} {Describe(control)} reaches from {area.Top:0.#} down to {area.Bottom:0.#}, outside what is shown ({shown.Top:0.#} to {shown.Bottom:0.#})."));
        }
    }

    private static Rect? AreaIn(Control control, Window window)
    {
        Point? topLeft = control.TranslatePoint(default, window);
        return topLeft is { } point ? new Rect(point, control.Bounds.Size) : null;
    }

    private static string Describe(Control control)
    {
        string? text = control switch
        {
            TextBlock block => block.Text,
            Button { Content: string content } => content, // a check box is a button too
            TextBox box => box.PlaceholderText,
            _ => null,
        };

        if (text is { Length: > 60 })
        {
            text = text[..60] + "…";
        }

        return (control.Name, text) switch
        {
            (not null, not null) => $"'{control.Name}' (\"{text}\")",
            (not null, null) => $"'{control.Name}'",
            (null, not null) => $"\"{text}\"",
            _ => "(unnamed)",
        };
    }
}
