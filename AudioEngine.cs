using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PanelSonidos;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reproduce cada sonido en dos salidas a la vez: los audifonos del usuario y el cable virtual.
/// El microfono real tambien entra al cable virtual para que voz y sonidos lleguen juntos a la llamada.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    const int Rate = 48000;
    const int DeviceInUse = unchecked((int)0x8889000A);
    static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);
    static readonly WaveFormat EngineFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

    readonly object voicesLock = new();
    readonly Dictionary<string, List<Voice>> voices = new();
    Bus? monitor, cable;
    WasapiCapture? mic;

    public event Action<string>? DeviceFailed;

    public bool HasCable => cable != null;
    public bool MicActive => mic != null;

    public static List<AudioDevice> ListDevices(DataFlow flow) => Task.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToList();
    }).GetAwaiter().GetResult();

    /// <summary>Windows no decodifica .ogg por si solo, asi que ese formato usa su propio lector.</summary>
    static WaveStream OpenReader(string path) =>
        path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new NAudio.Vorbis.VorbisWaveReader(path)
            : new AudioFileReader(path);

    /// <summary>Un .ogg puede llevar Vorbis u Opus; Discord y las notas de voz usan Opus.</summary>
    static bool IsOpus(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".ogg" && extension != ".opus") return false;

        using var file = File.OpenRead(path);
        var header = new byte[256];
        int read = file.Read(header, 0, header.Length);
        return System.Text.Encoding.ASCII.GetString(header, 0, read).Contains("OpusHead");
    }

    static float[] DecodeOpus(string path)
    {
        const int MaxFrame = 5760; // 120 ms a 48 kHz, el paquete Opus mas largo posible
        var packets = OggPackets(File.ReadAllBytes(path)).ToList();
        if (packets.Count < 3 || packets[0].Length < 12) throw new InvalidDataException("no contiene audio Opus valido");

        // Paquete 0: cabecera. Paquete 1: etiquetas. Lo demas es audio.
        int channels = packets[0][9] == 1 ? 1 : 2;
        int preSkip = BitConverter.ToUInt16(packets[0], 10);
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(Rate, channels);
        long limit = (long)(MaxLength.TotalSeconds * Rate * 2);
        var samples = new List<float>();
        var pcm = new short[MaxFrame * channels];

        foreach (var packet in packets.Skip(2))
        {
            if (packet.Length == 0) continue;

            int frames;
            try
            {
                frames = decoder.Decode(packet, pcm, MaxFrame, false);
            }
            catch (Concentus.OpusException)
            {
                continue; // un paquete danado no debe invalidar todo el sonido
            }

            for (int f = 0; f < frames; f++)
            {
                if (preSkip > 0) { preSkip--; continue; }
                samples.Add(pcm[f * channels] / 32768f);
                samples.Add(pcm[f * channels + channels - 1] / 32768f);
            }
            if (samples.Count > limit) throw new InvalidDataException("dura mas de 10 minutos");
        }

        if (samples.Count == 0) throw new InvalidDataException("no contiene audio Opus valido");
        return samples.ToArray();
    }

    /// <summary>Separa los paquetes de un archivo Ogg. Siempre avanza, asi que termina aunque el archivo este raro.</summary>
    static IEnumerable<byte[]> OggPackets(byte[] data)
    {
        var packet = new List<byte>();
        int page = 0;
        while (page + 27 <= data.Length
            && data[page] == 'O' && data[page + 1] == 'g' && data[page + 2] == 'g' && data[page + 3] == 'S')
        {
            int segments = data[page + 26];
            int offset = page + 27 + segments;
            if (offset > data.Length) yield break;

            for (int i = 0; i < segments; i++)
            {
                int size = data[page + 27 + i];
                if (offset + size > data.Length) yield break;
                packet.AddRange(new ArraySegment<byte>(data, offset, size));
                offset += size;
                if (size < 255)
                {
                    yield return packet.ToArray();
                    packet.Clear();
                }
            }
            page = offset;
        }
    }

    public static float[] Decode(string path)
    {
        if (IsOpus(path)) return DecodeOpus(path);

        using WaveStream reader = OpenReader(path);
        if (reader.TotalTime > MaxLength)
            throw new InvalidDataException("dura mas de 10 minutos");

        ISampleProvider source = (ISampleProvider)reader;
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2) source = new MultiplexingSampleProvider(new[] { source }, 2);
        if (source.WaveFormat.SampleRate != Rate) source = new WdlResamplingSampleProvider(source, Rate);

        var samples = new List<float>(Math.Max(1024, (int)(reader.TotalTime.TotalSeconds * Rate * 2)));
        var block = new float[Rate];
        int read;
        while ((read = source.Read(block, 0, block.Length)) > 0)
            samples.AddRange(new ArraySegment<float>(block, 0, read));
        return samples.ToArray();
    }

    /// <summary>Abre los dispositivos. Devuelve los errores encontrados, o una cadena vacia si todo fue bien.</summary>
    public string Configure(string? monitorId, string? cableId, string? micId, bool useMic, float monitorVolume, float callVolume)
    {
        Close();

        // Los objetos de audio de Windows se crean fuera del hilo de la interfaz (MTA).
        return Task.Run(() =>
        {
            var errors = new List<string>();
            using var enumerator = new MMDeviceEnumerator();

            MMDevice? cableDevice = null;
            if (!string.IsNullOrEmpty(cableId))
            {
                string? cableError = null;
                foreach (var candidate in CableCandidates(enumerator, cableId))
                {
                    try
                    {
                        cable = new Bus(candidate, 30, callVolume, Report);
                        cableDevice = candidate;
                        cableError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        cableError ??= ex.HResult == DeviceInUse
                            ? "El cable está ocupado. Revisa que Discord no lo tenga como salida de audio."
                            : ex.Message;
                    }
                }
                if (cableError != null) errors.Add("Salida a la llamada: " + cableError);
            }

            try
            {
                var device = monitorId == null
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(monitorId);
                // Si Windows usa el cable como salida, abrirlo otra vez duplicaria el sonido en la llamada.
                if (cableDevice == null || device.ID != cableDevice.ID)
                    monitor = new Bus(device, 40, monitorVolume, Report);
            }
            catch (Exception ex)
            {
                errors.Add("Salida para escuchar: " + ex.Message);
            }

            if (cable != null && useMic)
            {
                try
                {
                    var device = micId == null
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                        : enumerator.GetDevice(micId);
                    if (IsCableOutput(device.FriendlyName))
                        errors.Add("Tu microfono esta configurado como CABLE Output. Elige tu microfono real.");
                    else
                        StartMic(device);
                }
                catch (Exception ex)
                {
                    errors.Add("Microfono: " + ex.Message);
                }
            }

            return string.Join("  ", errors);
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// "CABLE Input" y "CABLE In 16ch" comparten un solo cable: si otro programa usa una, la otra
    /// da error de dispositivo ocupado. Por eso, tras la elegida, se intentan sus variantes.
    /// </summary>
    static IEnumerable<MMDevice> CableCandidates(MMDeviceEnumerator enumerator, string cableId)
    {
        var chosen = enumerator.GetDevice(cableId);
        yield return chosen;
        if (!IsCableInput(chosen.FriendlyName)) yield break;
        foreach (var other in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            if (other.ID != chosen.ID && IsCableInput(other.FriendlyName))
                yield return other;
    }

    public static bool IsCableInput(string name) => name.Contains("CABLE In", StringComparison.OrdinalIgnoreCase);
    public static bool IsCableOutput(string name) => name.Contains("CABLE Out", StringComparison.OrdinalIgnoreCase);

    void StartMic(MMDevice device)
    {
        var capture = new WasapiCapture(device, true, 20);
        var format = capture.WaveFormat;
        if (format.BitsPerSample != 32)
        {
            capture.Dispose();
            throw new NotSupportedException("formato de microfono no compatible");
        }

        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        capture.DataAvailable += (_, e) =>
        {
            // El reloj del microfono y el del cable difieren; esto evita que el retraso vaya creciendo.
            if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(150)) buffer.ClearBuffer();
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Report("Se perdio el microfono: " + e.Exception.Message);
        };

        cable!.AddMic(new MicToStereo(buffer));
        capture.StartRecording();
        mic = capture;
    }

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
        mic?.StopRecording();
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
        readonly WasapiOut output;
        readonly MixingSampleProvider sounds, main;
        readonly VolumeSampleProvider volume;
        readonly int rate;

        public float Volume { set => volume.Volume = value; }

        public Bus(MMDevice device, int latencyMs, float initialVolume, Action<string> onError)
        {
            var mix = device.AudioClient.MixFormat;
            rate = mix.SampleRate;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            sounds = new MixingSampleProvider(format) { ReadFully = true };
            volume = new VolumeSampleProvider(sounds) { Volume = initialVolume };
            main = new MixingSampleProvider(format) { ReadFully = true };
            main.AddMixerInput(volume);

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) onError("Se perdio " + device.FriendlyName + ": " + e.Exception.Message);
            };
            output.Init(new FitChannels(main, mix.Channels));
            output.Play();
        }

        ISampleProvider ToBusRate(ISampleProvider source) =>
            source.WaveFormat.SampleRate == rate ? source : new WdlResamplingSampleProvider(source, rate);

        public void Add(Voice voice) => sounds.AddMixerInput(ToBusRate(voice));

        public void AddMic(MicToStereo source) => main.AddMixerInput(ToBusRate(source));

        public void Dispose()
        {
            output.Stop();
            output.Dispose();
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

        public WaveFormat WaveFormat => EngineFormat;
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

    sealed class MicToStereo : ISampleProvider
    {
        readonly BufferedWaveProvider source;
        readonly int channels;
        byte[] bytes = new byte[8192];

        public MicToStereo(BufferedWaveProvider source)
        {
            this.source = source;
            channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int needed = frames * channels * 4;
            if (bytes.Length < needed) bytes = new byte[needed];
            source.Read(bytes, 0, needed);

            for (int f = 0; f < frames; f++)
            {
                float left = BitConverter.ToSingle(bytes, f * channels * 4);
                float right = channels > 1 ? BitConverter.ToSingle(bytes, (f * channels + 1) * 4) : left;
                buffer[offset + f * 2] = left;
                buffer[offset + f * 2 + 1] = right;
            }
            return frames * 2;
        }
    }

    /// <summary>Adapta la mezcla estereo a la salida real y recorta para que no sature.</summary>
    sealed class FitChannels : ISampleProvider
    {
        readonly ISampleProvider source;
        readonly int channels;
        float[] stereo = new float[8192];

        public FitChannels(ISampleProvider source, int channels)
        {
            this.source = source;
            this.channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (stereo.Length < frames * 2) stereo = new float[frames * 2];
            int got = source.Read(stereo, 0, frames * 2) / 2;

            for (int f = 0; f < got; f++)
            {
                float left = Math.Clamp(stereo[f * 2], -1f, 1f);
                float right = Math.Clamp(stereo[f * 2 + 1], -1f, 1f);
                int at = offset + f * channels;
                if (channels == 1)
                {
                    buffer[at] = (left + right) * 0.5f;
                    continue;
                }
                buffer[at] = left;
                buffer[at + 1] = right;
                for (int c = 2; c < channels; c++) buffer[at + c] = 0f;
            }
            return got * channels;
        }
    }
}
