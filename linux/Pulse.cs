using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PanelSonidos;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Un flujo de audio de PulseAudio (o PipeWire, que entiende el mismo protocolo).
/// Usa la API "simple": escribir y leer bloquean, asi que cada flujo tiene su propio hilo.
/// </summary>
public sealed unsafe class PulseStream : IDisposable
{
    const string SimpleLib = "libpulse-simple.so.0";
    const string CoreLib = "libpulse.so.0";
    const int Float32LE = 5;
    const int DirectionPlayback = 1, DirectionRecord = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    [DllImport(SimpleLib)]
    static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName,
        ref SampleSpec spec, IntPtr channelMap, ref BufferAttr attr, out int error);

    [DllImport(SimpleLib)] static extern int pa_simple_write(IntPtr stream, void* data, nuint bytes, out int error);
    [DllImport(SimpleLib)] static extern int pa_simple_read(IntPtr stream, void* data, nuint bytes, out int error);
    [DllImport(SimpleLib)] static extern void pa_simple_free(IntPtr stream);
    [DllImport(CoreLib)] static extern IntPtr pa_strerror(int error);

    IntPtr handle;

    PulseStream(IntPtr handle) => this.handle = handle;

    /// <param name="device">Nombre del dispositivo de PulseAudio, o null para el predeterminado.</param>
    public static PulseStream Open(bool playback, string? device, string streamName, int latencyMs)
    {
        var spec = new SampleSpec { Format = Float32LE, Rate = SoundDecoder.Rate, Channels = 2 };
        uint bytes = (uint)(SoundDecoder.Rate * 2 * 4 * latencyMs / 1000);
        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = playback ? bytes : uint.MaxValue,
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = playback ? uint.MaxValue : bytes,
        };

        var handle = pa_simple_new(null, "Panel de sonidos", playback ? DirectionPlayback : DirectionRecord,
            device, streamName, ref spec, IntPtr.Zero, ref attr, out int error);
        if (handle == IntPtr.Zero) throw new IOException(ErrorText(error));
        return new PulseStream(handle);
    }

    public void Write(float[] samples, int count)
    {
        fixed (float* data = samples)
            if (pa_simple_write(handle, data, (nuint)(count * sizeof(float)), out int error) < 0)
                throw new IOException(ErrorText(error));
    }

    public void Read(byte[] buffer)
    {
        fixed (byte* data = buffer)
            if (pa_simple_read(handle, data, (nuint)buffer.Length, out int error) < 0)
                throw new IOException(ErrorText(error));
    }

    static string ErrorText(int error)
    {
        try { return Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}"; }
        catch { return $"error {error}"; }
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        pa_simple_free(handle);
        handle = IntPtr.Zero;
    }
}

/// <summary>Lista dispositivos y crea el microfono virtual usando la herramienta pactl.</summary>
public static class PulseDevices
{
    public const string VirtualSink = "panel_sonidos";
    public const string VirtualMic = "panel_sonidos_mic";
    public const string VirtualMicLabel = "PanelSonidos-Microfono";

    static string Pactl(params string[] args)
    {
        var info = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["LC_ALL"] = "C"; // la salida de pactl se traduce segun el idioma; asi siempre sale en ingles

        using var process = Process.Start(info) ?? throw new InvalidOperationException("no se pudo abrir pactl");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0) throw new InvalidOperationException(errors.Result.Trim());
        return output;
    }

    /// <param name="kind">"sinks" (salidas) o "sources" (entradas).</param>
    public static List<AudioDevice> List(string kind)
    {
        var devices = new List<AudioDevice>();
        string? name = null;
        foreach (var raw in Pactl("list", kind).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name: ")) name = line[6..];
            else if (line.StartsWith("Description: ") && name != null)
            {
                devices.Add(new AudioDevice(name, line[13..]));
                name = null;
            }
        }
        return devices;
    }

    /// <summary>
    /// Crea una salida virtual y un microfono que escucha esa salida. Discord usa ese microfono.
    /// Duran hasta cerrar sesion; si ya existen se reutilizan.
    /// </summary>
    /// <returns>null si todo fue bien, o el motivo del fallo.</returns>
    public static string? EnsureVirtualMic()
    {
        try
        {
            if (List("sinks").All(d => d.Id != VirtualSink))
                Pactl("load-module", "module-null-sink", $"sink_name={VirtualSink}",
                    "sink_properties=device.description=PanelSonidos-Salida");

            if (List("sources").All(d => d.Id != VirtualMic))
                Pactl("load-module", "module-remap-source", $"master={VirtualSink}.monitor", $"source_name={VirtualMic}",
                    $"source_properties=device.description={VirtualMicLabel}");
            return null;
        }
        catch (Win32Exception)
        {
            return "no se encontró pactl (instala pulseaudio-utils)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Nombre del microfono predeterminado del sistema, o null si no se pudo saber.</summary>
    public static string? DefaultSource()
    {
        try
        {
            foreach (var raw in Pactl("info").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Default Source: ")) return line[16..];
            }
        }
        catch { }
        return null;
    }

    public static List<AudioDevice> SafeList(string kind)
    {
        try { return List(kind); }
        catch { return new List<AudioDevice>(); }
    }
}
