using System.Text.Json;
using System.Text.Json.Serialization;
using BitosX.Core.Settings;

namespace BitosX.Storage;

/// <summary>
/// Stores non-secret <see cref="AppSettings"/> as a JSON file next to the vault (but separate from
/// it). Contains only preferences — never keys, passwords, or vault contents. Loading fails safe:
/// a missing or unreadable file returns <see cref="AppSettings.Default"/>. Saving is atomic
/// (temp file + replace) so a crash cannot leave a half-written settings file.
/// </summary>
public sealed class FileAppSettingsStore : IAppSettingsStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _directory;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public FileAppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(path));
        _tempPath = _path + ".tmp";
    }

    /// <summary>The default per-user settings file location (e.g. %APPDATA%\BitosX\settings.json).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "BitosX",
        "settings.json");

    /// <summary>Creates a store at the default per-user location.</summary>
    public static FileAppSettingsStore CreateDefault() => new(DefaultPath);

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AppSettings.Default;
            }

            string json = File.ReadAllText(_path);
            SettingsDocument? doc = JsonSerializer.Deserialize<SettingsDocument>(json);
            if (doc is null)
            {
                return AppSettings.Default;
            }

            return new AppSettings
            {
                AutoLockOnInactivity = doc.AutoLockOnInactivity,
                InactivityTimeout = TimeSpan.FromSeconds(doc.InactivityTimeoutSeconds),
                LockOnMinimize = doc.LockOnMinimize,
            }.Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Preferences are non-critical: never let a corrupt file block startup.
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();

        var doc = new SettingsDocument
        {
            SchemaVersion = AppSettings.SchemaVersion,
            AutoLockOnInactivity = settings.AutoLockOnInactivity,
            InactivityTimeoutSeconds = (int)settings.InactivityTimeout.TotalSeconds,
            LockOnMinimize = settings.LockOnMinimize,
        };

        Directory.CreateDirectory(_directory);
        string json = JsonSerializer.Serialize(doc, Options);

        try
        {
            File.WriteAllText(_tempPath, json);
            if (File.Exists(_path))
            {
                File.Replace(_tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(_tempPath, _path);
            }
        }
        finally
        {
            TryDeleteTempFile();
        }
    }

    private void TryDeleteTempFile()
    {
        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; it is overwritten on the next save.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored for the same reason.
        }
    }

    private sealed class SettingsDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = AppSettings.SchemaVersion;
        [JsonPropertyName("autoLockOnInactivity")] public bool AutoLockOnInactivity { get; set; } = true;
        [JsonPropertyName("inactivityTimeoutSeconds")] public int InactivityTimeoutSeconds { get; set; } = 900;
        [JsonPropertyName("lockOnMinimize")] public bool LockOnMinimize { get; set; }
    }
}
