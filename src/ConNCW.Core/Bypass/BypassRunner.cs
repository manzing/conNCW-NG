using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;
using ConNCW.Core.Wav;

namespace ConNCW.Core.Bypass;

/// <summary>
/// Orchestration complète de --bypass-signature : décodage forcé (signature
/// ignorée), analyse heuristique du flux PCM, écriture d'un WAV de test dans
/// un dossier dédié pour écoute manuelle avant confirmation via --learn-signature.
/// </summary>
public static class BypassRunner
{
    public sealed record BypassResult(
        string TestWavPath,
        double ConfidenceScore,
        IReadOnlyList<string> Warnings,
        byte[] SignatureBytes,
        string SignatureHex);

    public static BypassResult Run(string ncwPath, SignatureStore signatures, string testOutputDir)
    {
        var ncw = NcwFile.Open(ncwPath, signatures, bypassSignature: true);
        var format = ncw.Header.ToAudioFormat();

        var report = BypassAnalyzer.Analyze(ncw.Samples, format);

        Directory.CreateDirectory(testOutputDir);
        string testWavPath = Path.Combine(
            testOutputDir,
            Path.GetFileNameWithoutExtension(ncwPath) + "_bypass_test.wav");

        var pcmBytes = InterleavedSamplesToPcmBytes(ncw.Samples, format.BitsPerSample);
        var wav = WavFileFactory.FromPcm(format, pcmBytes);
        wav.Save(testWavPath);

        return new BypassResult(
            testWavPath,
            report.ConfidenceScore,
            report.Warnings,
            ncw.Header.RawSignature,
            Convert.ToHexString(ncw.Header.RawSignature));
    }

    private static byte[] InterleavedSamplesToPcmBytes(int[] samples, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        var result = new byte[samples.Length * bytesPerSample];

        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i];
            for (int b = 0; b < bytesPerSample; b++)
            {
                result[i * bytesPerSample + b] = (byte)((v >> (8 * b)) & 0xFF);
            }
        }

        return result;
    }
}
