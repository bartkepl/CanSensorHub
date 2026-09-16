using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanSensorHub.App.Services;

public sealed class DeviceSettingsEntry
{
    public string ModuleId { get; set; } = "";
    public byte NodeId { get; set; }
    public string InstanceName { get; set; } = "";
}

public sealed class AppSettings
{
    public string TransportKind { get; set; } = "Simulated";
    public string? Port { get; set; }
    public int Bitrate { get; set; } = 500000;
    public List<DeviceSettingsEntry> Devices { get; set; } = [];
}

[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>Persists the last connection choice and device list to %AppData%\CanSensorHub\settings.json.</summary>
public static class AppSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanSensorHub", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best-effort — losing settings between runs is not fatal
        }
    }
}
