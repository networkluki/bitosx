using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BitosX.App.Views;

public partial class CreateVaultView : UserControl
{
    public CreateVaultView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        MasterPasswordBox.Focus();
    }
}
