using BitosX.Storage;

namespace BitosX.App;

/// <summary>
/// Where the application keeps its files: the vault and the settings. By default that is the
/// per-user location; <c>--data-directory</c> names another one, so the application can be run on
/// a vault of its own — for a second vault, or for trying something out without touching the
/// real one.
/// </summary>
/// <remarks>
/// An option that is given but cannot be used is an error: the application never falls back to
/// the default location, where it would open a vault the caller did not ask for.
/// </remarks>
public sealed class AppLocation
{
    /// <summary>The command-line option that names the data directory.</summary>
    public const string DataDirectoryOption = "--data-directory";

    // Whatever starts like this was meant to be the option, however it is spelled.
    private const string OptionFamily = "--data";

    private const string VaultFileName = "vault.bitosx";
    private const string SettingsFileName = "settings.json";

    private AppLocation(string? dataDirectory, string vaultFilePath, string settingsFilePath)
    {
        DataDirectory = dataDirectory;
        VaultFilePath = vaultFilePath;
        SettingsFilePath = settingsFilePath;
    }

    /// <summary>The per-user location (for example <c>%APPDATA%\BitosX</c> on Windows).</summary>
    public static AppLocation Default { get; } = new(
        dataDirectory: null,
        FileLocalVaultStorage.DefaultVaultFilePath,
        FileAppSettingsStore.DefaultPath);

    /// <summary>The directory named on the command line, or <see langword="null"/> for the default.</summary>
    public string? DataDirectory { get; }

    /// <summary>Whether this is the per-user location.</summary>
    public bool IsDefault => DataDirectory is null;

    /// <summary>The vault file.</summary>
    public string VaultFilePath { get; }

    /// <summary>The settings file.</summary>
    public string SettingsFilePath { get; }

    /// <summary>
    /// The title of the main window. Anywhere but the default location it names the directory, so
    /// that it is always visible which vault a window belongs to.
    /// </summary>
    public string WindowTitle => DataDirectory is null ? "BitosX" : $"BitosX — {DataDirectory}";

    /// <summary>The location in <paramref name="directory"/>, which must be a full path.</summary>
    /// <exception cref="AppLocationException">The directory cannot be used.</exception>
    public static AppLocation In(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new AppLocationException($"{DataDirectoryOption} needs a directory.");
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            // A relative path would depend on where the application happened to be started from.
            throw new AppLocationException(
                $"{DataDirectoryOption} needs a full path; '{directory}' is relative.");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppLocationException($"{DataDirectoryOption}: '{directory}' is not a usable path.", ex);
        }

        if (File.Exists(full))
        {
            throw new AppLocationException($"{DataDirectoryOption}: '{full}' is a file, not a directory.");
        }

        return new AppLocation(full, Path.Combine(full, VaultFileName), Path.Combine(full, SettingsFileName));
    }

    /// <summary>
    /// The location the command line asks for: <c>--data-directory &lt;path&gt;</c> or
    /// <c>--data-directory=&lt;path&gt;</c>, otherwise the default. Other arguments are left alone.
    /// </summary>
    /// <exception cref="AppLocationException">The option is given but cannot be used.</exception>
    public static AppLocation FromArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? directory = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            string? value;
            if (string.Equals(argument, DataDirectoryOption, StringComparison.Ordinal))
            {
                if (i + 1 >= arguments.Count)
                {
                    throw new AppLocationException($"{DataDirectoryOption} needs a directory.");
                }

                value = arguments[++i];
            }
            else if (argument.StartsWith(DataDirectoryOption + "=", StringComparison.Ordinal))
            {
                value = argument[(DataDirectoryOption.Length + 1)..];
            }
            else if (argument.StartsWith(OptionFamily, StringComparison.OrdinalIgnoreCase))
            {
                // A misspelling of the option must not quietly open the default vault instead.
                throw new AppLocationException(
                    $"'{argument}' is not understood. Use {DataDirectoryOption} <directory>.");
            }
            else
            {
                continue;
            }

            if (directory is not null)
            {
                throw new AppLocationException($"{DataDirectoryOption} is given more than once.");
            }

            directory = value;
        }

        return directory is null ? Default : In(directory);
    }
}

/// <summary>The data directory asked for on the command line cannot be used.</summary>
public sealed class AppLocationException : Exception
{
    public AppLocationException()
    {
    }

    public AppLocationException(string message)
        : base(message)
    {
    }

    public AppLocationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
