using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BitosX.App.Views;

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
    }

    // After every lock the master password can be typed at once, without a click first.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        MasterPasswordBox.Focus();
    }
}
