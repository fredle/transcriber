using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingTranscriber.Services;

/// <summary>
/// Small user-scoped settings file. Keeping the key here (rather than in the
/// install directory) means it is per-user and survives upgrades.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("recordingsRoot")] public string RecordingsRoot { get; set; } = "";
    /// <summary>Signed-in account's email, cached for display only - never used for authorization.</summary>
    [JsonPropertyName("accountEmail")] public string AccountEmail { get; set; } = "";
    /// <summary>
    /// On-disk form of the refresh token: DPAPI-encrypted (current-user
    /// scope) then base64'd, so settings.json never holds it in the clear.
    /// Use <see cref="RefreshToken"/> to read/write the plaintext value.
    /// </summary>
    [JsonPropertyName("refreshTokenProtected")] public string RefreshTokenProtected { get; set; } = "";

    /// <summary>Plaintext Google/Firebase refresh token. Not the ID token itself - that's minted from this on demand.</summary>
    [JsonIgnore]
    public string RefreshToken
    {
        get
        {
            if (RefreshTokenProtected.Length == 0) return "";
            try
            {
                var cipher = Convert.FromBase64String(RefreshTokenProtected);
                var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return "";   // stored under a different user/machine - treat as signed out
            }
        }
        set
        {
            if (string.IsNullOrEmpty(value)) { RefreshTokenProtected = ""; return; }
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
            RefreshTokenProtected = Convert.ToBase64String(cipher);
        }
    }
    [JsonPropertyName("micDeviceId")] public string MicDeviceId { get; set; } = "";
    [JsonPropertyName("speakerDeviceId")] public string SpeakerDeviceId { get; set; } = "";
    /// <summary>Keep running in the notification area when the window is closed.</summary>
    [JsonPropertyName("minimiseToTray")] public bool MinimiseToTray { get; set; } = true;
    /// <summary>Begin transcribing by itself when a Teams call starts. Off by
    /// default: recording a meeting should be a deliberate act.</summary>
    [JsonPropertyName("autoStartOnCall")] public bool AutoStartOnCall { get; set; }
    /// <summary>Stop transcribing by itself when the Teams call ends. Off by
    /// default, matching AutoStartOnCall's deliberate-act stance.</summary>
    [JsonPropertyName("autoStopOnCallEnd")] public bool AutoStopOnCallEnd { get; set; }
    /// <summary>Version last seen at startup, so a version bump (e.g. an applied auto-update) can be told apart from every other launch. Empty on a first-ever run, which is deliberately not treated as an update.</summary>
    [JsonPropertyName("lastSeenVersion")] public string LastSeenVersion { get; set; } = "";

    /// <summary>
    /// %AppData%\Teeline, migrating an older %AppData%\Kettle or, before
    /// that, %AppData%\MeetingTranscriber folder so an existing API
    /// key/settings survive prior renames.
    /// </summary>
    private static string Dir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var current = Path.Combine(appData, "Teeline");
            LegacyMigration.MigrateFolder(Path.Combine(appData, "Kettle"), current);
            LegacyMigration.MigrateFolder(Path.Combine(appData, "MeetingTranscriber"), current);
            return current;
        }
    }

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

        if (!string.IsNullOrWhiteSpace(settings.RecordingsRoot))
            MeetingStore.Root = settings.RecordingsRoot;

        return settings;
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
        catch (UnauthorizedAccessException) { }
    }
}
