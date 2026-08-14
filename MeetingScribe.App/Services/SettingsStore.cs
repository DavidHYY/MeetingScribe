using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingScribe.App.Models;

namespace MeetingScribe.App.Services;

/// <summary>Loads/saves <see cref="AppSettings"/> as UTF-8 JSON under %LocalAppData%\MeetingScribeCS.</summary>
public static class SettingsStore
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingScribeCS",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The prompt template and meeting titles routinely contain Japanese/Chinese/
        // Polish text; the default encoder escapes all of that to \uXXXX, which is
        // technically valid UTF-8-encoded-as-ASCII but defeats the point of keeping
        // these files human-readable UTF-8. This is our own local settings file, not
        // web-facing output, so relaxed escaping is safe here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads settings from disk, or returns defaults if the file is missing/corrupt.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not prevent the app from
            // starting - fall back to defaults, same policy as the Python prototype's
            // config.py.
            return new AppSettings();
        }
    }

    /// <summary>Writes settings to disk as UTF-8 (no BOM). Throws on failure - callers decide how to surface it.</summary>
    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
