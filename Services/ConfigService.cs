using System;
using System.IO;
using System.Text.Json;
using MultiPing.Models;

namespace MultiPing.Services;

/// <summary>Loads and saves <see cref="AppConfig"/> as JSON under the per-user AppData folder.</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>%AppData%\MultiPing</summary>
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiPing");

    public string ConfigPath => Path.Combine(AppDataDirectory, "config.json");

    /// <summary>Default folder for log files when the user has not chosen one.</summary>
    public static string DefaultLogDirectory => Path.Combine(AppDataDirectory, "logs");

    /// <summary>
    /// Loads the config file. Only properties known to <see cref="AppConfig"/> are read; anything
    /// else (legacy flat keys, unknown fields) is dropped. When the file on disk does not match the
    /// canonical serialization of what was loaded, it is rewritten so stale content is removed.
    /// </summary>
    public AppConfig Load()
    {
        AppConfig cfg = new();
        string? existingJson = null;

        try
        {
            if (File.Exists(ConfigPath))
            {
                existingJson = File.ReadAllText(ConfigPath);
                cfg = JsonSerializer.Deserialize<AppConfig>(existingJson, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            // Corrupt or unreadable config falls back to defaults rather than crashing startup.
            cfg = new AppConfig();
        }

        cfg.Normalize();

        // Rewrite the file whenever its content differs from the normalized form, which both
        // migrates to the sectioned layout and strips unknown fields.
        if (existingJson is not null)
        {
            string canonical = JsonSerializer.Serialize(cfg, JsonOptions);
            if (!string.Equals(existingJson, canonical, StringComparison.Ordinal))
                Save(cfg);
        }

        return cfg;
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Persisting settings is best-effort; failure should not interrupt the user.
        }
    }
}
