namespace BitosX.ApiClient;

/// <summary>
/// The BitosX server the application talks to: an origin, nothing more. Only <c>https://</c> is
/// accepted, with one exception for development: plain <c>http://</c> to a loopback address, where
/// nothing leaves the machine.
/// </summary>
public sealed class ApiServer
{
    /// <summary>The production server.</summary>
    public static ApiServer Production { get; } = new(new Uri("https://api.bitosx.se/"));

    private ApiServer(Uri baseAddress)
    {
        BaseAddress = baseAddress;
    }

    /// <summary>The origin, with a trailing slash so relative paths resolve beneath it.</summary>
    public Uri BaseAddress { get; }

    /// <summary>What to call the server in the user interface: its host name.</summary>
    public string DisplayName => BaseAddress.IsDefaultPort ? BaseAddress.Host : $"{BaseAddress.Host}:{BaseAddress.Port}";

    /// <summary>Whether this is the production server.</summary>
    public bool IsProduction => BaseAddress == Production.BaseAddress;

    /// <summary>
    /// Parses an origin such as <c>https://api.example.com</c> or <c>https://api.example.com:8443</c>.
    /// Anything else — another scheme, a path, a query, a fragment, userinfo, or plain http to a host
    /// that is not loopback — is refused.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an acceptable origin.</exception>
    public static ApiServer Parse(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        if (origin.Length > 256 || origin.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("The server address is too long or contains whitespace.", nameof(origin));
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException($"'{origin}' is not an absolute address.", nameof(origin));
        }

        bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        bool http = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        if (!https && !(http && uri.IsLoopback))
        {
            throw new ArgumentException(
                $"'{origin}' must use https:// (plain http:// is allowed only to this machine).", nameof(origin));
        }

        if (uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException($"'{origin}' must not carry a user name, and must name a host.", nameof(origin));
        }

        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException($"'{origin}' must be an origin only: no path, query or fragment.", nameof(origin));
        }

        // Rebuilt from its parts, so the result is canonical (lowercase scheme and host, trailing slash).
        var canonical = new UriBuilder(uri.Scheme, uri.Host, uri.Port) { Path = "/" }.Uri;
        return new ApiServer(canonical);
    }

    public override string ToString() => BaseAddress.ToString();
}
