using ConNCW.Core.IO;
using ConNCW.Core.Logging;
using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Resampler;
using ConNCW.Core.Signatures;
using ConNCW.Core.Wav;

namespace ConNCW.Core.Engine;

public enum ConversionDirection
{
    NcwToWav,
    WavToNcw,
    NcwToNcw,
    WavToWav
}

public sealed class ConversionOptions
{
    public required ConversionDirection Direction { get; init; }
    public bool Recursive { get; init; }
    public bool Rewrite { get; init; }
    public bool BypassSignature { get; init; }
    public bool BypassLowDepth { get; init; }
    public string OutputSignatureHex { get; init; } = "01A89ED631010000";
    public ResampleOptions? Resample { get; init; }
}

/// <summary>
/// Cœur d'orchestration : convertit un fichier unique ou un dossier et coordonne
/// SignatureStore, NcwFile, WavFile, NativeReSampler et ConversionLogger.
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
        bool inPlace = string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!options.Rewrite && File.Exists(destPath) && !inPlace)
            {
                destPath = MakeNonConflictingPath(destPath);
            }

            string actualSavePath = inPlace ? destPath + ".tmp" : destPath;

            switch (options.Direction)
            {
                case ConversionDirection.NcwToWav:
                    ConvertNcwToWav(sourcePath, actualSavePath, options);
                    break;
                case ConversionDirection.WavToNcw:
                    ConvertWavToNcw(sourcePath, actualSavePath, options);
                    break;
                case ConversionDirection.NcwToNcw:
                    ConvertNcwToNcw(sourcePath, actualSavePath, options);
                    break;
                case ConversionDirection.WavToWav:
                    ConvertWavToWav(sourcePath, actualSavePath, options);
                    break;
            }

            if (inPlace && File.Exists(actualSavePath))
            {
                File.Move(actualSavePath, destPath, overwrite: true);
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
        catch (Exception ex)
        {
            if (!inPlace && File.Exists(destPath))
            {
                try { File.Delete(destPath); } catch { /* ignorer */ }
            }
            if (inPlace && File.Exists(destPath + ".tmp"))
            {
                try { File.Delete(destPath + ".tmp"); } catch { /* ignorer */ }
            }

            _logger.Report(new ConversionResult
            {
                SourcePath = sourcePath,
                Status = ConversionStatus.FailCorruptData,
                Message = $"Erreur inattendue : {ex.Message}"
            });
        }
    }

    private void ConvertNcwToWav(string sourcePath, string destPath, ConversionOptions options)
    {
        var ncw = NcwFile.Open(sourcePath, _signatures, options.BypassSignature);
        var wav = WavFile.FromNcw(ncw);

        bool bypassed = false;
        if (options.Resample != null)
        {
            int effectiveRate = options.Resample.TargetSampleRate > 0 ? options.Resample.TargetSampleRate : (int)wav.Format.SampleRate;
            int targetBits = options.Resample.TargetBits > 0 ? options.Resample.TargetBits : 16;

            if (options.BypassLowDepth && !wav.Format.IsFloat && wav.Format.BitsPerSample <= targetBits && wav.Format.SampleRate == effectiveRate)
            {
                bypassed = true;
            }
            else
            {
                byte[] inBytes = wav.ToBytes();
                byte[] outBytes = NativeReSampler.Resample(inBytes, options.Resample, effectiveRate);
                wav = WavFile.FromBytes(outBytes);
            }
        }

        string destDir = Path.GetDirectoryName(destPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        wav.Save(destPath);

        var status = ncw.Header.SignatureRecognized
            ? ConversionStatus.Ok
            : ConversionStatus.OkToleratedSignature;

        string resampleInfo = options.Resample != null
            ? (bypassed
                ? $" (bypass-lowdepth: déjà en {wav.Format.BitsPerSample}b/{wav.Format.SampleRate}Hz)"
                : $" -> rééchantillonné en RAM ({wav.Format.SampleRate}Hz/{wav.Format.BitsPerSample}b)")
            : string.Empty;

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = status,
            SignatureHex = Convert.ToHexString(ncw.Header.RawSignature),
            Message = (ncw.Header.SignatureRecognized
                ? $"Signature: {ncw.Header.MatchedSignatureLabel}"
                : "Signature non reconnue, traité en mode tolérant (--bypass-signature).") + resampleInfo
        });
    }

    private void ConvertWavToNcw(string sourcePath, string destPath, ConversionOptions options)
    {
        var wav = WavFile.Open(sourcePath);
        bool bypassed = false;

        if (options.Resample != null)
        {
            int effectiveRate = options.Resample.TargetSampleRate > 0 ? options.Resample.TargetSampleRate : (int)wav.Format.SampleRate;
            int targetBits = options.Resample.TargetBits > 0 ? options.Resample.TargetBits : 16;

            if (options.BypassLowDepth && !wav.Format.IsFloat && wav.Format.BitsPerSample <= targetBits && wav.Format.SampleRate == effectiveRate)
            {
                bypassed = true;
            }
            else
            {
                byte[] inBytes = wav.ToBytes();
                byte[] outBytes = NativeReSampler.Resample(inBytes, options.Resample, effectiveRate);
                wav = WavFile.FromBytes(outBytes);
            }
        }

        var interleaved = wav.GetInterleavedSamples();
        var ncw = NcwFile.FromPcm(wav.Format, interleaved, options.OutputSignatureHex);

        string destDir = Path.GetDirectoryName(destPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        ncw.Save(destPath);

        string resampleInfo = options.Resample != null
            ? (bypassed
                ? $" (bypass-lowdepth: WAV déjà en {wav.Format.BitsPerSample}b/{wav.Format.SampleRate}Hz)"
                : $" (rééchantillonné en RAM {wav.Format.SampleRate}Hz/{wav.Format.BitsPerSample}b)")
            : string.Empty;

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = ConversionStatus.Ok,
            Message = $"Encodé avec signature {options.OutputSignatureHex}{resampleInfo}"
        });
    }

    private void ConvertNcwToNcw(string sourcePath, string destPath, ConversionOptions options)
    {
        // Low bit-depth bypass fast path: inspect NCW header without decoding audio payload (when --bypass-lowdepth is set)
        if (options.BypassLowDepth && options.Resample != null)
        {
            var header = NcwFile.ReadHeaderOnly(sourcePath, _signatures, options.BypassSignature);
            int targetRate = options.Resample.TargetSampleRate > 0
                ? options.Resample.TargetSampleRate
                : (int)header.SampleRate;
            int targetBits = options.Resample.TargetBits > 0
                ? options.Resample.TargetBits
                : 16;

            if (header.Bits <= targetBits && header.SampleRate == (uint)targetRate)
            {
                string destDir = Path.GetDirectoryName(destPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                }

                var bypassStatus = header.SignatureRecognized
                    ? ConversionStatus.Ok
                    : ConversionStatus.OkToleratedSignature;

                _logger.Report(new ConversionResult
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    Status = bypassStatus,
                    SignatureHex = Convert.ToHexString(header.RawSignature),
                    Message = (header.SignatureRecognized
                        ? $"Signature: {header.MatchedSignatureLabel}"
                        : "Signature non reconnue, mode tolérant") + $" -> NCW déjà en {header.Bits}b/{header.SampleRate}Hz (copie directe sans réencodage --bypass-lowdepth)"
                });
                return;
            }
        }

        var ncw = NcwFile.Open(sourcePath, _signatures, options.BypassSignature);
        var wav = WavFile.FromNcw(ncw);

        if (options.Resample != null)
        {
            int effectiveRate = options.Resample.TargetSampleRate > 0 ? options.Resample.TargetSampleRate : (int)wav.Format.SampleRate;
            if (wav.Format.BitsPerSample > options.Resample.TargetBits || wav.Format.SampleRate != effectiveRate || !options.BypassLowDepth)
            {
                byte[] inBytes = wav.ToBytes();
                byte[] outBytes = NativeReSampler.Resample(inBytes, options.Resample, effectiveRate);
                wav = WavFile.FromBytes(outBytes);
            }
        }

        var interleaved = wav.GetInterleavedSamples();
        var outNcw = NcwFile.FromPcm(wav.Format, interleaved, options.OutputSignatureHex);

        string outDir = Path.GetDirectoryName(destPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        outNcw.Save(destPath);

        var status = ncw.Header.SignatureRecognized
            ? ConversionStatus.Ok
            : ConversionStatus.OkToleratedSignature;

        string resampleInfo = options.Resample != null
            ? $" -> rééchantillonné en RAM ({wav.Format.SampleRate}Hz/{wav.Format.BitsPerSample}b)"
            : string.Empty;

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = status,
            SignatureHex = Convert.ToHexString(ncw.Header.RawSignature),
            Message = (ncw.Header.SignatureRecognized
                ? $"Signature: {ncw.Header.MatchedSignatureLabel}"
                : "Signature non reconnue, traité en mode tolérant (--bypass-signature).") + resampleInfo
        });
    }

    private void ConvertWavToWav(string sourcePath, string destPath, ConversionOptions options)
    {
        var wav = WavFile.Open(sourcePath);

        if (options.Resample != null)
        {
            int effectiveRate = options.Resample.TargetSampleRate > 0 ? options.Resample.TargetSampleRate : (int)wav.Format.SampleRate;
            int targetBits = options.Resample.TargetBits > 0 ? options.Resample.TargetBits : 16;

            if (options.BypassLowDepth && !wav.Format.IsFloat && wav.Format.BitsPerSample <= targetBits && wav.Format.SampleRate == effectiveRate)
            {
                string destDir = Path.GetDirectoryName(destPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                }

                _logger.Report(new ConversionResult
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    Status = ConversionStatus.Ok,
                    Message = $"WAV déjà en {wav.Format.BitsPerSample}b/{wav.Format.SampleRate}Hz : copié directement sans rééchantillonnage (bypass-lowdepth)"
                });
                return;
            }

            byte[] inBytes = wav.ToBytes();
            byte[] outBytes = NativeReSampler.Resample(inBytes, options.Resample, effectiveRate);
            wav = WavFile.FromBytes(outBytes);
        }

        string outDir = Path.GetDirectoryName(destPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        wav.Save(destPath);

        _logger.Report(new ConversionResult
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Status = ConversionStatus.Ok,
            Message = $"WAV rééchantillonné en RAM ({wav.Format.SampleRate}Hz/{wav.Format.BitsPerSample}b)"
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
        string pattern = options.Direction switch
        {
            ConversionDirection.NcwToWav => "*.ncw",
            ConversionDirection.NcwToNcw => "*.ncw",
            ConversionDirection.WavToNcw => "*.wav",
            ConversionDirection.WavToWav => "*.wav",
            _ => "*.ncw"
        };

        string newExt = options.Direction switch
        {
            ConversionDirection.NcwToWav => ".wav",
            ConversionDirection.NcwToNcw => ".ncw",
            ConversionDirection.WavToNcw => ".ncw",
            ConversionDirection.WavToWav => ".wav",
            _ => ".wav"
        };

        var files = FileScanner.FindFiles(sourceDir, pattern, options.Recursive).ToList();
        int threadCount = options.Resample?.WorkerThreads ?? 0;
        if (threadCount <= 0)
        {
            threadCount = Environment.ProcessorCount;
        }

        if (threadCount > 1 && files.Count > 1)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = threadCount
            };

            Parallel.ForEach(files, parallelOptions, file =>
            {
                string dest = FileScanner.MapToDestination(sourceDir, destDir, file, newExt);
                ConvertFile(file, dest, options);
            });
        }
        else
        {
            foreach (var file in files)
            {
                string dest = FileScanner.MapToDestination(sourceDir, destDir, file, newExt);
                ConvertFile(file, dest, options);
            }
        }
    }
}
