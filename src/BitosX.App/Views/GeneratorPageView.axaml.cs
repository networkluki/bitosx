using Avalonia.Controls;
using BitosX.Core.Generation;

namespace BitosX.App.Views;

public partial class GeneratorPageView : UserControl
{
    public GeneratorPageView()
    {
        InitializeComponent();

        // The slider reaches exactly what the generator accepts, from the one place that says so.
        LengthSlider.Minimum = PasswordGenerationOptions.MinLength;
        LengthSlider.Maximum = PasswordGenerationOptions.MaxLength;
    }
}
