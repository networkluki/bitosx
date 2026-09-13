using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BitosX.Core.Services;
using BitosX.Core.Settings;
using BitosX.Platform.Session;

namespace BitosX.App.Services;

/// <summary>
/// Avalonia glue for auto-locking. Feeds the platform-agnostic <see cref="AutoLockController"/>:
/// in-app input resets the inactivity clock, a UI-thread timer periodically checks the timeout,
/// window minimize is observed, and OS session/suspend signals are marshalled onto the UI thread.
/// All decision logic lives in the controller; this type only wires up sources.
/// </summary>
public sealed class AutoLockService : IDisposable
{
    // How often the inactivity timeout is evaluated. Fine-grained enough for minute-scale timeouts
    // without waking frequently.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly AutoLockController _controller;
    private readonly ISystemSessionEvents _sessionEvents;
    private readonly EventHandler<RoutedEventArgs> _activityHandler;

    private DispatcherTimer? _timer;
    private Window? _window;
    private bool _attached;
    private bool _disposed;

    public AutoLockService(AutoLockController controller, ISystemSessionEvents sessionEvents)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _sessionEvents = sessionEvents ?? throw new ArgumentNullException(nameof(sessionEvents));
        _activityHandler = OnActivity;
    }

    /// <summary>Wires activity, minimize, OS signals, and the inactivity timer to the main window.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _window = window;

        // Observe all input to the window (tunnelling + handledEventsToo so nothing is missed).
        window.AddHandler(InputElement.PointerMovedEvent, _activityHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, _activityHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, _activityHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, _activityHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.PropertyChanged += OnWindowPropertyChanged;

        _sessionEvents.SessionLocked += OnSessionLocked;
        _sessionEvents.Suspending += OnSuspending;
        _sessionEvents.Start();

        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>Applies updated settings to the underlying policy.</summary>
    public void UpdateSettings(AppSettings settings) => _controller.UpdateSettings(settings);

    private void OnActivity(object? sender, RoutedEventArgs e) => _controller.NotifyActivity();

    private void OnTimerTick(object? sender, EventArgs e) => _controller.CheckInactivity();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && _window?.WindowState == WindowState.Minimized)
        {
            _controller.OnWindowMinimized();
        }
    }

    // SystemEvents fire on a non-UI thread; marshal before touching the vault service.
    private void OnSessionLocked(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_controller.OnSessionLocked);

    private void OnSuspending(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_controller.OnSystemSuspending);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        if (_window is not null)
        {
            _window.RemoveHandler(InputElement.PointerMovedEvent, _activityHandler);
            _window.RemoveHandler(InputElement.PointerPressedEvent, _activityHandler);
            _window.RemoveHandler(InputElement.PointerWheelChangedEvent, _activityHandler);
            _window.RemoveHandler(InputElement.KeyDownEvent, _activityHandler);
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        _sessionEvents.SessionLocked -= OnSessionLocked;
        _sessionEvents.Suspending -= OnSuspending;
        _sessionEvents.Dispose();
        _controller.Dispose();
    }
}
