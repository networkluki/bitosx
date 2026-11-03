using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BitosX.App.Services;

/// <summary>
/// A small window that says why BitosX did not start — a second instance on the same vault, or a
/// command line it cannot use — where there is no console to read it from (a Windows shortcut, a
/// desktop launcher). The message is BitosX's own text; nothing secret is ever in it.
/// </summary>
public static class StartupNotice
{
    /// <summary>The window, for showing it — and for the tests, which look at it without a display.</summary>
    public static Window CreateWindow(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var ok = new Button
        {
            Name = "StartupNoticeOkButton",
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80,
        };
        var text = new TextBlock { Name = "StartupNoticeText", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(text, Avalonia.Automation.AutomationLiveSetting.Assertive);

        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { text, ok } },
        };
        ok.Click += (_, _) => window.Close();
        window.Opened += (_, _) => ok.Focus();
        return window;
    }

    /// <summary>Shows the notice and returns when it is closed. Does nothing where no window can be shown (no display).</summary>
    public static void Show(string title, string message, string[] args)
    {
        try
        {
            AppBuilder.Configure(() => new NoticeApplication(title, message))
                .UsePlatformDetect()
                .WithInterFont()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or TypeInitializationException)
        {
            // No display to show it on: the message went to the standard error stream as well.
        }
    }

    private sealed class NoticeApplication(string title, string message) : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = CreateWindow(title, message);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
