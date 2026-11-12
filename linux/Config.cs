using System.Text.Json;

namespace PanelSonidos;

public sealed class SoundEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public uint Mods { get; set; }
    /// <summary>Tecla como "keysym" de X11; 0 = sin tecla.</summary>
    public uint Key { get; set; }
    public string KeyLabel { get; set; } = "";
    public float Volume { get; set; } = 1f;
}

public sealed class AppConfig
{
    public List<SoundEntry> Sounds { get; set; } = new();

    /// <summary>null = salida predeterminada del sistema.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>null = usar el microfono virtual del panel; "" = no enviar a la llamada.</summary>
    public string? CableDevice { get; set; }

    /// <summary>null = microfono predeterminado del sistema.</summary>
    public string? MicDevice { get; set; }

    public bool MicEnabled { get; set; } = true;
    public int MonitorVolume { get; set; } = 70;
    public int CallVolume { get; set; } = 80;
    public uint StopMods { get; set; }
    public uint StopKey { get; set; }
    public string StopKeyLabel { get; set; } = "";

    // En Linux esto es ~/.config/panel-sonidos
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "panel-sonidos");
    static string ConfigPath => Path.Combine(Folder, "config.json");
    public static string SoundsFolder => Path.Combine(Folder, "sonidos");

    public static AppConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            try { System.IO.File.Copy(ConfigPath, ConfigPath + ".danado", true); } catch { }
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temp = ConfigPath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(temp, ConfigPath, true);
    }
}
