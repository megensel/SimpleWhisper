using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Manages application settings persistence. Loads settings from a JSON file on construction
/// and provides thread-safe save functionality. Settings are stored under the user's
/// <c>%AppData%/SimpleWhisper/</c> directory.
/// </summary>
public sealed class SettingsService
{
    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimpleWhisper");

    private static readonly string SettingsFilePath = Path.Combine(AppDataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _saveLock = new();

    /// <summary>
    /// Raised after settings have been successfully saved to disk.
    /// </summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// The current application settings. Modify properties on this instance
    /// and call <see cref="Save"/> to persist changes.
    /// </summary>
    public AppSettings Settings { get; private set; }

    /// <summary>
    /// Initializes the settings service by loading settings from the JSON file.
    /// If the file does not exist or is unreadable, default settings are created and persisted.
    /// </summary>
    public SettingsService()
    {
        Settings = Load();
    }

    /// <summary>
    /// Persists the current <see cref="Settings"/> to disk as indented JSON.
    /// This operation is thread-safe; concurrent calls are serialized.
    /// Raises <see cref="SettingsChanged"/> on successful save.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            EnsureDirectoryExists();

            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reloads settings from disk, replacing the current <see cref="Settings"/> instance.
    /// If the file is missing or corrupted, defaults are restored and saved.
    /// </summary>
    public void Reload()
    {
        Settings = Load();
    }

    /// <summary>
    /// Gets the full path to the settings JSON file.
    /// </summary>
    public static string FilePath => SettingsFilePath;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

                if (settings is not null)
                {
                    // Migration: if the old plaintext "openAIApiKey" field exists in the JSON
                    // and the new encrypted field is empty, migrate the key to DPAPI encryption.
                    if (string.IsNullOrEmpty(settings.EncryptedOpenAIApiKey))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("openAIApiKey", out var oldKey))
                        {
                            string plainKey = oldKey.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(plainKey))
                            {
                                settings.OpenAIApiKey = plainKey; // Setter encrypts via DPAPI
                            }
                        }
                    }

                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Settings file is corrupted or inaccessible; fall through to create defaults.
        }

        // Create default settings and persist them so the file always exists.
        var defaults = new AppSettings();
        SaveDefaults(defaults);
        return defaults;
    }

    private static void SaveDefaults(AppSettings settings)
    {
        try
        {
            EnsureDirectoryExists();
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the app can still run with in-memory defaults.
        }
    }

    private static void EnsureDirectoryExists()
    {
        if (!Directory.Exists(AppDataDirectory))
        {
            Directory.CreateDirectory(AppDataDirectory);
        }
    }
}
