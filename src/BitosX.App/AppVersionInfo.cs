using System.Reflection;

namespace BitosX.App;

/// <summary>
/// Resolves the user-facing product version as a 3-part SemVer core (MAJOR.MINOR.PATCH).
/// </summary>
/// <remarks>
/// The value is derived from the assembly's informational (product) version — which is driven by
/// <c>&lt;Version&gt;</c> in the build — with the SemVer <c>+build</c> metadata (the git commit the
/// SDK appends) removed. It deliberately does not expose the CLR/Windows 4-part
/// <see cref="AssemblyName.Version"/> (whose trailing <c>.0</c> revision is a platform artefact, not
/// a real release component). The 3-part assembly version is only a fallback when no informational
/// version is present.
/// </remarks>
public static class AppVersionInfo
{
    /// <summary>The 3-part product version to display, e.g. "0.3.0". Never has a 4th component or +metadata.</summary>
    public static string Display { get; } = Resolve(typeof(AppVersionInfo).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Normalize(informational, assembly.GetName().Version);
    }

    /// <summary>
    /// Prefers the informational (product) version, stripping SemVer <c>+build</c> metadata; falls back
    /// to the numeric assembly version trimmed to its first three parts (dropping the CLR revision).
    /// </summary>
    public static string Normalize(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            string core = informationalVersion;
            int plus = core.IndexOf('+');
            if (plus >= 0)
            {
                core = core[..plus];
            }

            core = core.Trim();
            if (core.Length > 0)
            {
                return core;
            }
        }

        if (assemblyVersion is not null)
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        }

        return "0.0.0";
    }
}
