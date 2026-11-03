namespace BitosX.App.Services;

/// <summary>Opens a web page in the system browser.</summary>
public interface ILinkOpener
{
    /// <summary>Opens <paramref name="address"/>; <see langword="false"/> when the system could not open it.</summary>
    Task<bool> OpenAsync(Uri address);
}

/// <summary>The system browser, through the main window's launcher. Only https addresses are opened.</summary>
public sealed class AvaloniaLinkOpener(Func<Avalonia.Controls.TopLevel?> topLevel) : ILinkOpener
{
    public async Task<bool> OpenAsync(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || !string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        if (topLevel() is not { } top)
        {
            return false;
        }

        return await top.Launcher.LaunchUriAsync(address).ConfigureAwait(true);
    }
}
