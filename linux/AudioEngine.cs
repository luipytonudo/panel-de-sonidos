using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PanelSonidos;

/// <summary>
/// Reproduce cada sonido en dos salidas a la vez: los audifonos del usuario y la salida virtual
/// que alimenta el microfono del panel. El microfono real tambien entra ahi para que voz y sonidos lleguen juntos.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SoundDecoder.Rate, 2);

    readonly object voicesLock = new();
    readonly Dictionary<string, List<Voice>> voices = new();
    Bus? monitor, cable;
    MicPump? mic;

    public event Action<string>? DeviceFailed;

    public bool HasCable => cable != null;
    public bool MicActive => mic != null;

    /// <summary>Abre las salidas. Devuelve los errores encontrados, o una cadena vacia si todo fue bien.</summary>
    public string Configure(string? monitorDevice, string? cableDevice, string? micDevice, bool useMic, float monitorVolume, float callVolume)
    {
        Close();
        var errors = new List<string>();

        if (cableDevice != null)
        {
            try { cable = new Bus(cableDevice, "Llamada", callVolume, Report); }
            catch (Exception ex) { errors.Add("Salida a la llamada: " + Describe(ex)); }
        }

        if (monitorDevice == null || monitorDevice != cableDevice)
        {
            try { monitor = new Bus(monitorDevice, "Escucha", monitorVolume, Report); }
            catch (Exception ex) { errors.Add("Salida para escuchar: " + Describe(ex)); }
        }

        if (cable != null && useMic)
        {
            try { mic = new MicPump(micDevice, cable, Report); }
            catch (Exception ex) { errors.Add("Micrófono: " + Describe(ex)); }
        }

        return string.Join("  ", errors);
    }

    static string Describe(Exception ex) => ex is DllNotFoundException
        ? "falta libpulse (instala PulseAudio o pipewire-pulse)"
        : ex.Message;

    void Report(string message) => DeviceFailed?.Invoke(message);

    public void SetVolumes(float monitorVolume, float callVolume)
    {
        if (monitor != null) monitor.Volume = monitorVolume;
        if (cable != null) cable.Volume = callVolume;
    }

    /// <summary>Si el mismo sonido ya estaba sonando, vuelve a empezar en lugar de encimarse.</summary>
    public void Play(string id, float[] samples, float volume)
    {
        var created = new List<Voice>();
        foreach (var bus in new[] { monitor, cable })
        {
            if (bus == null) continue;
            var voice = new Voice(samples, volume);
            bus.Add(voice);
            created.Add(voice);
        }

        lock (voicesLock)
        {
            if (voices.TryGetValue(id, out var old))
                foreach (var voice in old) voice.Stop();
            voices[id] = created;
        }
    }

    public bool IsPlaying(string id)
    {
        lock (voicesLock)
            return voices.TryGetValue(id, out var list) && list.Any(v => !v.Finished);
    }

    public void StopAll()
    {
        lock (voicesLock)
        {
            foreach (var voice in voices.Values.SelectMany(v => v)) voice.Stop();
            voices.Clear();
        }
    }

    void Close()
    {
        StopAll();
        mic?.Dispose();
        mic = null;
        monitor?.Dispose();
        monitor = null;
        cable?.Dispose();
        cable = null;
    }

    public void Dispose() => Close();

    sealed class Bus : IDisposable
    {
        const int ChunkFrames = 480; // 10 ms

        readonly PulseStream stream;
        readonly MixingSampleProvider sounds, main;
        readonly VolumeSampleProvider volume;
        readonly Thread thread;
        volatile bool running = true;

        public float Volume { set => volume.Volume = value; }

        public Bus(string? device, string name, float initialVolume, Action<string> onError)
        {
            stream = PulseStream.Open(true, device, name, 40);
            sounds = new MixingSampleProvider(Format) { ReadFully = true };
            volume = new VolumeSampleProvider(sounds) { Volume = initialVolume };
            main = new MixingSampleProvider(Format) { ReadFully = true };
            main.AddMixerInput(volume);

            thread = new Thread(() => Pump(onError)) { IsBackground = true, Name = "audio " + name, Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        void Pump(Action<string> onError)
        {
            var buffer = new float[ChunkFrames * 2];
            try
            {
                while (running)
                {
                    int count = main.Read(buffer, 0, buffer.Length);
                    for (int i = 0; i < count; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
                    stream.Write(buffer, count); // bloquea hasta que el servidor de audio pide mas: marca el ritmo
                }
            }
            catch (Exception ex)
            {
                if (running) onError("Se perdió la salida de audio: " + ex.Message);
            }
        }

        public void Add(ISampleProvider voice) => sounds.AddMixerInput(voice);
        public void AddMic(ISampleProvider source) => main.AddMixerInput(source);

        public void Dispose()
        {
            running = false;
            // Liberar el flujo mientras el hilo lo usa cerraria el programa; si no termina a tiempo se deja abierto.
            if (thread.Join(1000)) stream.Dispose();
        }
    }

    sealed class MicPump : IDisposable
    {
        readonly PulseStream stream;
        readonly Thread thread;
        volatile bool running = true;

        public MicPump(string? device, Bus target, Action<string> onError)
        {
            stream = PulseStream.Open(false, device, "Micrófono", 20);
            var buffer = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            target.AddMic(buffer.ToSampleProvider());

            thread = new Thread(() =>
            {
                var chunk = new byte[480 * 2 * sizeof(float)];
                try
                {
                    while (running)
                    {
                        stream.Read(chunk);
                        // El microfono y la salida van a relojes distintos; esto evita que el retraso crezca.
                        if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(150)) buffer.ClearBuffer();
                        buffer.AddSamples(chunk, 0, chunk.Length);
                    }
                }
                catch (Exception ex)
                {
                    if (running) onError("Se perdió el micrófono: " + ex.Message);
                }
            }) { IsBackground = true, Name = "microfono" };
            thread.Start();
        }

        public void Dispose()
        {
            running = false;
            if (thread.Join(1000)) stream.Dispose();
        }
    }

    sealed class Voice : ISampleProvider
    {
        readonly float[] data;
        readonly float gain;
        int position;
        volatile bool stopped;

        public Voice(float[] data, float gain)
        {
            this.data = data;
            this.gain = gain;
        }

        public WaveFormat WaveFormat => Format;
        public bool Finished => stopped || Volatile.Read(ref position) >= data.Length;
        public void Stop() => stopped = true;

        public int Read(float[] buffer, int offset, int count)
        {
            if (stopped) return 0;
            int n = Math.Min(count, data.Length - position);
            for (int i = 0; i < n; i++) buffer[offset + i] = data[position + i] * gain;
            Volatile.Write(ref position, position + n);
            return n;
        }
    }
}
