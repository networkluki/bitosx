using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BitosX.App.Views;

public partial class NoteEditorView : UserControl
{
    public NoteEditorView()
    {
        InitializeComponent();
    }

    // The editor opens with the keyboard in its first field, not on the page behind it.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
    }
}
