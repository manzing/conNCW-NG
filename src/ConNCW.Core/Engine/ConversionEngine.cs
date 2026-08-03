using ConNCW.Core.IO;
using ConNCW.Core.Logging;
using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;
using ConNCW.Core.Wav;

namespace ConNCW.Core.Engine;

public enum ConversionDirection { NcwToWav, WavToNcw }

public sealed class ConversionOptions
{
    public required ConversionDirection Direction { get; init; }
    public bool Recursive { get; init; }
    public bool Rewrite { get; init; }
    public bool BypassSignature { get; init; }
    public string OutputSignatureHex { get; init; } = "01A89ED631010000";
}

/// <summary>
/// Cœur d'orchestration : convertit un fichier unique et coordonne
/// SignatureStore, NcwFile, WavFile, ConversionLogger.
/// </summary>
public sealed class ConversionEngine
{
    private readonly SignatureStore _signatures;
    private readonly ConversionLogger _logger;

    public ConversionEngine(SignatureStore signatures, ConversionLogger logger)
    {
        _signatures = signatures;
        _logger = logger;
    }

    public void ConvertFile(string sourcePath, string destPath, ConversionOptions options)
    {
        try
        {
            if (!options.Rewrite && File.Exists(destPath))
            {
                destPath = MakeNonConflictingPath(destPath);
            }

            if (options.Direction == ConversionDirection.NcwToWav)
            {
                ConvertNcwToWav(sourcePath, destPath, options);
            }
            else
            {
                ConvertWavToNcw(sourcePath, destPath, options);
            }
        }
        catch (UnknownNcwSignatureException ex)
        {
            _logger.Report(new ConversionResult
            {
                SourcePath = sourcePath,
                Status = ConversionStatus.FailUnknownSignature,
                Message = "Utilisez --learn-signature ou --bypass-signature pour ce fichier.",
                SignatureHex = Convert.ToHexString(ex.Signature)
            });
        }
        catch (InvalidDataException ex)
        {
            _logger.Report(new ConversionResult
            {
                SourcePath = sourcePath,
                Status = ConversionStatus.FailCorruptData,
                Message = ex.Message
            });
        }
        catch (IOException ex)
        {
            _logger.Report(new ConversionResult
            {
                SourcePath = sourcePath,
                Status = ConversionStatus.FailIoError,
                Message = ex.Message
            });
        }
    }

    private void ConvertNcwToWav(string sourcePath, string destPath, ConversionOptions options)
    {
        var ncw = NcwFile.Open(sourcePath, _signatures, options.BypassSignature);
        var format = ncw.Header.ToAudioFormat();

        int bytesPerSample = format.BitsPerSample / 8;
        var pcmBytes = new byte[ncw.Samples.Length * bytesPerSample];
        for (int i = 0; i < ncw.Samples.Length; i++)
        {
            int v = ncw.Samples[i];
            for (int b = 0; b < bytesPerSample; b++)
            {
                pcmBytes[i * bytesPerSample + b] = (byte)((v >> (8 * b)) & 0xFF);
            }
        }

        var wav = WavFile.FromRaw(format, pcmBytes);
        wav.Save(destPath);

        var status = ncw.Header.SignatureRecognized
            ? ConversionStatus.Ok
            : ConversionStatus.OkToleratedSignature;

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = status,
            SignatureHex = Convert.ToHexString(ncw.Header.RawSignature),
            Message = ncw.Header.SignatureRecognized
                ? $"Signature: {ncw.Header.MatchedSignatureLabel}"
                : "Signature non reconnue, traité en mode tolérant (--bypass-signature)."
        });
    }

    private void ConvertWavToNcw(string sourcePath, string destPath, ConversionOptions options)
    {
        var wav = WavFile.Open(sourcePath);
        int bytesPerSample = wav.Format.BitsPerSample / 8;
        int totalSamples = wav.AudioData.Length / bytesPerSample;
        var interleaved = new int[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int v = 0;
            for (int b = 0; b < bytesPerSample; b++)
            {
                v |= wav.AudioData[i * bytesPerSample + b] << (8 * b);
            }
            int signBit = 1 << (wav.Format.BitsPerSample - 1);
            if ((v & signBit) != 0)
            {
                v -= 1 << wav.Format.BitsPerSample;
            }
            interleaved[i] = v;
        }

        var ncw = NcwFile.FromPcm(wav.Format, interleaved, options.OutputSignatureHex);
        ncw.Save(destPath);

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = ConversionStatus.Ok,
            Message = $"Encodé avec signature {options.OutputSignatureHex}"
        });
    }

    private static string MakeNonConflictingPath(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        int n = 1;
        string candidate = path;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}_{n}{ext}");
            n++;
        }
        return candidate;
    }

    public void ConvertDirectory(string sourceDir, string destDir, ConversionOptions options)
    {
        string pattern = options.Direction == ConversionDirection.NcwToWav ? "*.ncw" : "*.wav";
        string newExt = options.Direction == ConversionDirection.NcwToWav ? ".wav" : ".ncw";

        // Fix bug 0.5 : AllDirectories descend dans tous les sous-dossiers,
        // même si la racine ne contient aucun fichier correspondant.
        foreach (var file in FileScanner.FindFiles(sourceDir, pattern, options.Recursive))
        {
            string dest = FileScanner.MapToDestination(sourceDir, destDir, file, newExt);
            ConvertFile(file, dest, options);
        }
    }
}
