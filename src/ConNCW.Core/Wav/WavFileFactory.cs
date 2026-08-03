using ConNCW.Core.Models;

namespace ConNCW.Core.Wav;

/// <summary>Petite fabrique utilitaire pour construire un WavFile à partir de PCM brut déjà en octets.</summary>
public static class WavFileFactory
{
    public static WavFile FromPcm(AudioFormat format, byte[] pcmBytes)
    {
        return WavFile.FromRaw(format, pcmBytes);
    }
}
