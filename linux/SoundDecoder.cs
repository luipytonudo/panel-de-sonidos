using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PanelSonidos;

/// <summary>Convierte un archivo de audio en muestras 48 kHz estereo. Todo es codigo .NET, sin depender del sistema.</summary>
public static class SoundDecoder
{
    public const int Rate = 48000;
    static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);
    public static readonly string[] Extensions = { ".mp3", ".wav", ".ogg", ".opus" };

    public static float[] Decode(string path)
    {
        if (IsOpus(path)) return DecodeOpus(path);

        IDisposable owner;
        TimeSpan length;
        ISampleProvider source;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".wav":
            {
                var reader = new WaveFileReader(path);
                owner = reader;
                length = reader.TotalTime;
                source = reader.ToSampleProvider();
                break;
            }
            case ".mp3":
            {
                var mpeg = new NLayer.MpegFile(path);
                owner = mpeg;
                length = mpeg.Duration;
                source = new MpegSource(mpeg);
                break;
            }
            case ".ogg":
            {
                var vorbis = new NAudio.Vorbis.VorbisWaveReader(path);
                owner = vorbis;
                length = vorbis.TotalTime;
                source = vorbis;
                break;
            }
            default:
                throw new InvalidDataException("formato no compatible: usa mp3, wav u ogg");
        }

        using (owner)
        {
            if (length > MaxLength) throw new InvalidDataException("dura mas de 10 minutos");

            if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels > 2) source = new MultiplexingSampleProvider(new[] { source }, 2);
            if (source.WaveFormat.SampleRate != Rate) source = new WdlResamplingSampleProvider(source, Rate);

            var samples = new List<float>(Math.Max(1024, (int)(length.TotalSeconds * Rate * 2)));
            var block = new float[Rate];
            int read;
            while ((read = source.Read(block, 0, block.Length)) > 0)
                samples.AddRange(new ArraySegment<float>(block, 0, read));
            return samples.ToArray();
        }
    }

    sealed class MpegSource : ISampleProvider
    {
        readonly NLayer.MpegFile file;

        public MpegSource(NLayer.MpegFile file)
        {
            this.file = file;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(file.SampleRate, file.Channels);
        }

        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count) => file.ReadSamples(buffer, offset, count);
    }

    /// <summary>Un .ogg puede llevar Vorbis u Opus; Discord y las notas de voz usan Opus.</summary>
    static bool IsOpus(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".ogg" && extension != ".opus") return false;

        using var file = System.IO.File.OpenRead(path);
        var header = new byte[256];
        int read = file.Read(header, 0, header.Length);
        return System.Text.Encoding.ASCII.GetString(header, 0, read).Contains("OpusHead");
    }

    static float[] DecodeOpus(string path)
    {
        const int MaxFrame = 5760; // 120 ms a 48 kHz, el paquete Opus mas largo posible
        var packets = OggPackets(System.IO.File.ReadAllBytes(path)).ToList();
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
}
