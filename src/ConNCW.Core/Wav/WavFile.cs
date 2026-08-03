using ConNCW.Core.Models;

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

    public static WavFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

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
                // au lieu de faire échouer le parsing, contrairement au comportement original.
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
        using var writer = new BinaryWriter(stream);

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