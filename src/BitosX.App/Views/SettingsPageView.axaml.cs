using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace BitosX.App.Views;

public partial class SettingsPageView : UserControl
{
    private readonly IDisposable _confirmPanelVisibility;

    public SettingsPageView()
    {
        InitializeComponent();

        // When the destructive restore-confirmation panel appears, move initial focus to the
        // non-destructive Cancel button (never the "Replace my vault" button). This is a view
        // concern only — no domain or crypto logic. It keeps a stray Enter/Space from landing on
        // the destructive action right after validation.
        _confirmPanelVisibility = RestoreConfirmPanel
            .GetObservable(Visual.IsVisibleProperty)
            .Subscribe(new AnonymousObserver(visible =>
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(() => RestoreCancelButton.Focus());
                }
            }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _confirmPanelVisibility.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class AnonymousObserver(Action<bool> onNext) : IObserver<bool>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(bool value) => onNext(value);
    }
}
