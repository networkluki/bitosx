using BitosX.ApiClient;

namespace BitosX.App;

/// <summary>
/// Which BitosX server the application talks to, from the command line: the production server
/// by default, another origin with <c>--api-server https://host[:port]</c> (for a server of one's
/// own; plain http only to this machine), or none at all with <c>--api-server none</c>, which
/// makes the run local-only.
/// </summary>
/// <remarks>
/// As with <see cref="AppLocation"/>, an option that is given but cannot be used ends the
/// application rather than quietly talking to the production server.
/// </remarks>
public static class ApiServerSelection
{
    /// <summary>The command-line option that names the server.</summary>
    public const string Option = "--api-server";

    /// <summary>The value that disables server integration for the run.</summary>
    public const string None = "none";

    // Whatever starts like this was meant to be the option, however it is spelled.
    private const string OptionFamily = "--api";

    /// <summary>
    /// The server the command line asks for, <see cref="ApiServer.Production"/> when it does not,
    /// or <see langword="null"/> for <c>--api-server none</c>. Other arguments are left alone.
    /// </summary>
    /// <exception cref="ApiServerSelectionException">The option is given but cannot be used.</exception>
    public static ApiServer? FromArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? value = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            string? given;
            if (string.Equals(argument, Option, StringComparison.Ordinal))
            {
                if (i + 1 >= arguments.Count)
                {
                    throw new ApiServerSelectionException($"{Option} needs a server address or '{None}'.");
                }

                given = arguments[++i];
            }
            else if (argument.StartsWith(Option + "=", StringComparison.Ordinal))
            {
                given = argument[(Option.Length + 1)..];
            }
            else if (argument.StartsWith(OptionFamily, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiServerSelectionException($"'{argument}' is not understood. Use {Option} <https://host> or {Option} {None}.");
            }
            else
            {
                continue;
            }

            if (value is not null)
            {
                throw new ApiServerSelectionException($"{Option} is given more than once.");
            }

            value = given;
        }

        if (value is null)
        {
            return ApiServer.Production;
        }

        if (string.Equals(value, None, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return ApiServer.Parse(value);
        }
        catch (ArgumentException ex)
        {
            throw new ApiServerSelectionException($"{Option}: {ex.Message}", ex);
        }
    }
}

/// <summary>The server asked for on the command line cannot be used.</summary>
public sealed class ApiServerSelectionException : Exception
{
    public ApiServerSelectionException()
    {
    }

    public ApiServerSelectionException(string message)
        : base(message)
    {
    }

    public ApiServerSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
