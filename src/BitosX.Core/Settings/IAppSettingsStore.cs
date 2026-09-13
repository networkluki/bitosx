namespace BitosX.Core.Settings;

/// <summary>
/// Loads and saves non-secret <see cref="AppSettings"/>. Loading always succeeds: a missing or
/// unreadable settings file falls back to <see cref="AppSettings.Default"/> rather than throwing,
/// so a corrupt preferences file never blocks startup.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>Loads settings, returning defaults if none are stored or the file is unreadable.</summary>
    AppSettings Load();

    /// <summary>Persists settings.</summary>
    void Save(AppSettings settings);
}
