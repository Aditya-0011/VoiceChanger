using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceChanger.App.Services;

/// <summary>
/// Persisted user application settings.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Custom models directory override. If null or empty, defaults to %APPDATA%\VoiceChanger\models\.
    /// </summary>
    public string? CustomModelsDirectory { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for <see cref="AppSettings"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Manages loading and saving persistent user settings in %APPDATA%\VoiceChanger\settings.json.
/// </summary>
public static class AppSettingsService
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChanger",
        "settings.json");

    /// <summary>
    /// Loads settings from disk, returning defaults if not found or corrupted.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to default settings
        }

        return new AppSettings();
    }

    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Suppress file write failure
        }
    }
}
