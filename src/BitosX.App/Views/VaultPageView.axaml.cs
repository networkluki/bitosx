using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BitosX.App.Views;

public partial class VaultPageView : UserControl
{
    public VaultPageView()
    {
        InitializeComponent();
    }

    // Asking to delete hides the Delete button that has the focus; the focus moves to the question's
    // safe answer, Keep, on the same card, instead of leaving the card.
    private void OnDeleteRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button)
        {
            return;
        }

        Border? card = button.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
        Dispatcher.UIThread.Post(() =>
            card?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "KeepButton" && b.IsEffectivelyVisible)?.Focus());
    }
}
