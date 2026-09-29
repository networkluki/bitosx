using System.Text.Json;
using System.Text.Json.Serialization;
using BitosX.Core.Settings;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// Stores non-secret <see cref="AppSettings"/> as a JSON file next to the vault (but separate from
/// it). Contains only preferences — never keys, passwords, or vault contents. Loading fails safe:
/// a missing, unreadable, or oversized file returns <see cref="AppSettings.Default"/>. Saving is
/// atomic (temp file + replace) so a crash cannot leave a half-written settings file.
/// </summary>
/// <remarks>
/// The settings file is not authenticated. Software that can write to the user's profile can change
/// these preferences (for example turn automatic locking off); that is the same trust boundary as the
/// rest of the application (see docs/THREAT_MODEL.md). Values are always clamped to their accepted
/// ranges, and a file that cannot be parsed falls back to the secure defaults.
/// </remarks>
public sealed class FileAppSettingsStore : IAppSettingsStore
{
    /// <summary>Maximum accepted settings file size; the real file is a few hundred bytes.</summary>
    public const long MaxFileSizeBytes = 64 * 1024;

    private readonly string _path;
    private readonly string _directory;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public FileAppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(path));
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

            byte[] bytes = StorageFiles.ReadBounded(_path, MaxFileSizeBytes, "settings file");
            SettingsDocument? doc = JsonSerializer.Deserialize<SettingsDocument>(
                StorageFiles.WithoutByteOrderMark(bytes));
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
        catch (Exception ex) when (ex is IOException
                                       or JsonException
                                       or UnauthorizedAccessException
                                       or BitosXCryptographicException)
        {
            // Preferences are non-critical: never let a corrupt or oversized file block startup.
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

        StorageFiles.CreatePrivateDirectory(_directory);
        StorageFiles.WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(doc, Options));
    }

    private sealed class SettingsDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = AppSettings.SchemaVersion;
        [JsonPropertyName("autoLockOnInactivity")] public bool AutoLockOnInactivity { get; set; } = true;
        [JsonPropertyName("inactivityTimeoutSeconds")] public int InactivityTimeoutSeconds { get; set; } = 900;
        [JsonPropertyName("lockOnMinimize")] public bool LockOnMinimize { get; set; }
    }
}
