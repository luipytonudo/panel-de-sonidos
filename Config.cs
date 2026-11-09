using System.Text.Json;

namespace PanelSonidos;

public sealed class SoundEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public uint Mods { get; set; }
    public uint Key { get; set; }
    public float Volume { get; set; } = 1f;
}

public sealed class AppConfig
{
    public List<SoundEntry> Sounds { get; set; } = new();

    /// <summary>null = salida predeterminada de Windows.</summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>null = buscar CABLE Input automaticamente; "" = no enviar a la llamada.</summary>
    public string? CableDeviceId { get; set; }

    /// <summary>null = microfono predeterminado de Windows.</summary>
    public string? MicDeviceId { get; set; }

    public bool MicEnabled { get; set; } = true;
    public int MonitorVolume { get; set; } = 70;
    public int CallVolume { get; set; } = 80;
    public uint StopMods { get; set; }
    public uint StopKey { get; set; }

    static string Folder => AppContext.BaseDirectory;
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
            // Si el archivo esta danado se conserva como copia y se empieza de cero.
            try { System.IO.File.Copy(ConfigPath, ConfigPath + ".danado", true); } catch { }
        }
        return new AppConfig();
    }

    public void Save()
    {
        var temp = ConfigPath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(temp, ConfigPath, true);
    }
}
