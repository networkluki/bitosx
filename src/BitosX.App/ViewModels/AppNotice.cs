using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitosX.App.ViewModels;

/// <summary>
/// Something the application has to tell the user that does not belong to the screen at hand,
/// shown at the top of the window until it is dismissed. There is at most one; a newer one
/// replaces it.
/// </summary>
public sealed partial class AppNotice : ObservableObject
{
    /// <summary>What to tell the user, or <see langword="null"/> when there is nothing to tell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial string? Message { get; private set; }

    /// <summary>Whether there is something to show.</summary>
    public bool IsShown => !string.IsNullOrEmpty(Message);

    /// <summary>Shows <paramref name="message"/>, replacing what was shown before.</summary>
    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
    }

    /// <summary>Removes the notice.</summary>
    [RelayCommand]
    public void Dismiss() => Message = null;
}
