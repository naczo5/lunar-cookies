using System.IO;
using System.Text.Json;

namespace LunarCookies.Core;

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;
    public bool ExitWhenMinecraftCloses { get; set; } = true;
    public bool PopulateLunarAccountManager { get; set; }
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public AppSettingsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LunarCookies");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                       ?? new AppSettings();
        }
        catch
        {
            // Fall back to safe defaults if the settings file is unavailable.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
