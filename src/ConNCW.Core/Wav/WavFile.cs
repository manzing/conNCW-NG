using ConNCW.Core.Models;
using ConNCW.Core.Ncw;

namespace ConNCW.Core.Wav;

/// <summary>
/// Portage de WAVParser3.pas, réécrit en parseur générique de chunks RIFF
/// pour tolérer les chunks optionnels (LIST, JUNK, fact, PEAK, bext, ...)
/// que produisent de nombreux DAW modernes et qui faisaient échouer l'original.
/// </summary>
public sealed class WavFile
{
    public AudioFormat Format { get; }
    public byte[] AudioData { get; }

    private WavFile(AudioFormat format, byte[] audioData)
    {
        Format = format;
        AudioData = audioData;
    }

    /// <summary>Construit un WavFile en mémoire à partir de PCM brut déjà encodé en octets (utilisé par le bypass runner).</summary>
    public static WavFile FromRaw(AudioFormat format, byte[] pcmBytes)
    {
        return new WavFile(format, pcmBytes);
    }

    /// <summary>
    /// Construit un WavFile à partir d'un fichier NcwFile décodé en mémoire.
    /// </summary>
    public static WavFile FromNcw(NcwFile ncw)
    {
        var format = ncw.Header.ToAudioFormat();
        int bytesPerSample = format.BitsPerSample / 8;
        var pcmBytes = new byte[ncw.Samples.Length * bytesPerSample];
        for (int i = 0; i < ncw.Samples.Length; i++)
        {
            int v = ncw.Samples[i];
            if (format.BitsPerSample == 8)
            {
                pcmBytes[i] = unchecked((byte)(v + 128));
            }
            else
            {
                for (int b = 0; b < bytesPerSample; b++)
                {
                    pcmBytes[i * bytesPerSample + b] = (byte)((v >> (8 * b)) & 0xFF);
                }
            }
        }
        return new WavFile(format, pcmBytes);
    }

    /// <summary>
    /// Convertit les octets audio PCM en échantillons entiers entrelacés signés (int[]).
    /// </summary>
    public int[] GetInterleavedSamples()
    {
        int bytesPerSample = Format.BitsPerSample / 8;
        int totalSamples = AudioData.Length / bytesPerSample;
        var interleaved = new int[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int v = 0;
            for (int b = 0; b < bytesPerSample; b++)
            {
                v |= AudioData[i * bytesPerSample + b] << (8 * b);
            }

            if (Format.BitsPerSample == 8)
            {
                v -= 128;
            }
            else if (Format.BitsPerSample < 32)
            {
                int signBit = 1 << (Format.BitsPerSample - 1);
                if ((v & signBit) != 0)
                {
                    v -= 1 << Format.BitsPerSample;
                }
            }
            interleaved[i] = v;
        }
        return interleaved;
    }

    public static WavFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        return Open(stream);
    }

    public static WavFile FromBytes(byte[] wavBytes)
    {
        using var stream = new MemoryStream(wavBytes);
        return Open(stream);
    }

    public static WavFile Open(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var riffId = new string(reader.ReadChars(4));
        if (riffId != "RIFF")
        {
            throw new InvalidDataException("En-tête RIFF manquant ou invalide.");
        }

        reader.ReadUInt32(); // taille totale du fichier, non utilisée directement
        var waveId = new string(reader.ReadChars(4));
        if (waveId != "WAVE")
        {
            throw new InvalidDataException("Identifiant WAVE manquant.");
        }

        AudioFormat? format = null;
        byte[]? audioData = null;

        while (stream.Position < stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();

            if (chunkId == "fmt ")
            {
                ushort audioFormatTag = reader.ReadUInt16();
                ushort channels = reader.ReadUInt16();
                uint sampleRate = reader.ReadUInt32();
                reader.ReadUInt32(); // byte rate
                reader.ReadUInt16(); // block align
                ushort bitsPerSample = reader.ReadUInt16();

                long remaining = chunkSize - 16;
                if (remaining > 0)
                {
                    reader.ReadBytes((int)remaining); // extension WAVE_FORMAT_EXTENSIBLE ignorée pour l'instant
                }

                format = new AudioFormat
                {
                    Channels = channels,
                    BitsPerSample = bitsPerSample,
                    SampleRate = sampleRate,
                    SampleCount = 0
                };
            }
            else if (chunkId == "data")
            {
                audioData = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                // Chunk inconnu (LIST, JUNK, fact, PEAK, bext, ...) : on l'ignore proprement
                long skip = chunkSize + (chunkSize % 2); // padding pair RIFF
                reader.ReadBytes((int)Math.Min(skip, stream.Length - stream.Position));
            }
        }

        if (format is null)
        {
            throw new InvalidDataException("Chunk 'fmt ' manquant : fichier WAV invalide.");
        }

        if (audioData is null)
        {
            throw new InvalidDataException("Chunk 'data' manquant : fichier WAV invalide.");
        }

        int bytesPerSample = format.BitsPerSample / 8;
        uint sampleCount = bytesPerSample > 0
            ? (uint)(audioData.Length / (bytesPerSample * Math.Max((int)format.Channels, 1)))
            : 0;

        format = format with { SampleCount = sampleCount };

        return new WavFile(format, audioData);
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        Save(stream);
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream(AudioData.Length + 44);
        Save(stream);
        return stream.ToArray();
    }

    public void Save(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        int byteRate = (int)(Format.SampleRate * Format.Channels * (Format.BitsPerSample / 8));
        short blockAlign = (short)(Format.Channels * (Format.BitsPerSample / 8));
        uint dataSize = (uint)AudioData.Length;
        uint riffSize = 36 + dataSize;

        writer.Write("RIFF".ToCharArray());
        writer.Write(riffSize);
        writer.Write("WAVE".ToCharArray());

        writer.Write("fmt ".ToCharArray());
        writer.Write(16u);
        writer.Write((ushort)1); // PCM
        writer.Write(Format.Channels);
        writer.Write(Format.SampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(Format.BitsPerSample);

        writer.Write("data".ToCharArray());
        writer.Write(dataSize);
        writer.Write(AudioData);
    }
}
