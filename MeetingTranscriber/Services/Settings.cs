using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingTranscriber.Services;

/// <summary>
/// Small user-scoped settings file. Keeping the key here (rather than in the
/// install directory) means it is per-user and survives upgrades.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("recordingsRoot")] public string RecordingsRoot { get; set; } = "";
    [JsonPropertyName("micDeviceId")] public string MicDeviceId { get; set; } = "";
    [JsonPropertyName("speakerDeviceId")] public string SpeakerDeviceId { get; set; } = "";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetingTranscriber");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        Settings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch (Exception)
        {
            settings = new Settings();
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            settings.ApiKey = DiscoverApiKey();

        if (!string.IsNullOrWhiteSpace(settings.RecordingsRoot))
            MeetingStore.Root = settings.RecordingsRoot;

        return settings;
    }

    /// <summary>
    /// Fall back to the environment, then to a .env beside the executable or
    /// in the working directory - which is how the original tool stored it,
    /// so an existing setup keeps working without re-entering the key.
    /// </summary>
    private static string DiscoverApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ASSEMBLY_AI_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            try
            {
                var envFile = Path.Combine(dir, ".env");
                if (!File.Exists(envFile)) continue;
                foreach (var raw in File.ReadAllLines(envFile))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("ASSEMBLY_AI_TOKEN=", StringComparison.OrdinalIgnoreCase)) continue;
                    return line["ASSEMBLY_AI_TOKEN=".Length..].Trim().Trim('"');
                }
            }
            catch (IOException) { }
        }
        return "";
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* settings are a convenience, not critical */ }
    }
}
