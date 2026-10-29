using ConNCW.Core.Engine;
using ConNCW.Core.Logging;
using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Resampler;
using ConNCW.Core.Signatures;
using ConNCW.Core.Wav;
using Xunit;

namespace ConNCW.Core.Tests;

public class ResamplerPipelineTests
{
    private static readonly SignatureStore TestSignatures = new(new[]
    {
        new SignatureEntry { HexBytes = "01A89ED631010000", Label = "TestSignature" }
    });

    [Fact]
    public void NativeReSampler_IsAvailable_AndReturnsVersion()
    {
        Assert.True(NativeReSampler.IsAvailable, "resampler.dll should be available");
        string ver = NativeReSampler.GetVersion();
        Assert.StartsWith("2.1", ver);
    }

    [Fact]
    public void NativeReSampler_ResampleWavInMemory_ConvertsWavFormatCorrectly()
    {
        // 1. Generate a small synthetic 24-bit 96kHz sine WAV in memory
        int sampleRate = 96000;
        int channels = 2;
        int bits = 24;
        int numFrames = 9600; // 0.1s
        int totalSamples = numFrames * channels;
        var pcm = new byte[totalSamples * 3];

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)(i / channels) / sampleRate;
            double val = Math.Sin(2 * Math.PI * 440.0 * t) * 0.8;
            int sample24 = (int)(val * 8388607.0);

            pcm[i * 3 + 0] = (byte)(sample24 & 0xFF);
            pcm[i * 3 + 1] = (byte)((sample24 >> 8) & 0xFF);
            pcm[i * 3 + 2] = (byte)((sample24 >> 16) & 0xFF);
        }

        var inFormat = new AudioFormat
        {
            Channels = (ushort)channels,
            BitsPerSample = (ushort)bits,
            SampleRate = (uint)sampleRate,
            SampleCount = (uint)numFrames
        };

        var inWav = WavFile.FromRaw(inFormat, pcm);
        byte[] inWavBytes = inWav.ToBytes();

        // 2. Resample in memory to 44.1kHz 16-bit
        var resampleOptions = new ResampleOptions
        {
            TargetSampleRate = 44100,
            TargetBits = 16,
            DitherProfile = 6,
            DitherAmount = 1.0,
            DoublePrecision = true
        };

        byte[] outWavBytes = NativeReSampler.Resample(inWavBytes, resampleOptions);
        Assert.NotNull(outWavBytes);
        Assert.True(outWavBytes.Length > 44);

        // 3. Verify resulting WAV properties
        var outWav = WavFile.FromBytes(outWavBytes);
        Assert.Equal(44100u, outWav.Format.SampleRate);
        Assert.Equal((ushort)16, outWav.Format.BitsPerSample);
        Assert.Equal((ushort)2, outWav.Format.Channels);
        Assert.True(outWav.AudioData.Length > 0);
    }

    [Fact]
    public void EndToEnd_NcwToNcw_SinglePassRamPipeline_ProducesValid16BitNcw()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ncw_pipeline_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Create a 24-bit 96kHz NCW file
            int sampleRate = 96000;
            int channels = 1;
            int numFrames = 4800; // 0.05s
            var samples24 = new int[numFrames];
            for (int i = 0; i < numFrames; i++)
            {
                double t = (double)i / sampleRate;
                samples24[i] = (int)(Math.Sin(2 * Math.PI * 1000.0 * t) * 8000000.0);
            }

            var format = new AudioFormat
            {
                Channels = (ushort)channels,
                BitsPerSample = 24,
                SampleRate = (uint)sampleRate,
                SampleCount = (uint)numFrames
            };

            var originalNcw = NcwFile.FromPcm(format, samples24, "01A89ED631010000");
            string sourcePath = Path.Combine(tempDir, "sample_96k_24b.ncw");
            string destPath = Path.Combine(tempDir, "sample_44k_16b.ncw");
            originalNcw.Save(sourcePath);

            // 2. Run conversion engine with NcwToNcw + Resample in RAM
            using var logger = new ConversionLogger("NUL");
            var engine = new ConversionEngine(TestSignatures, logger);

            var options = new ConversionOptions
            {
                Direction = ConversionDirection.NcwToNcw,
                Rewrite = true,
                Resample = new ResampleOptions
                {
                    TargetSampleRate = 44100,
                    TargetBits = 16,
                    DitherProfile = 6,
                    DitherAmount = 1.0,
                    DoublePrecision = true
                }
            };

            engine.ConvertFile(sourcePath, destPath, options);

            // 3. Verify destination NCW file exists and has 16-bit 44.1kHz audio
            Assert.True(File.Exists(destPath), "Destination NCW file should exist");
            var convertedNcw = NcwFile.Open(destPath, TestSignatures, false);

            Assert.Equal((ushort)16, convertedNcw.Header.Bits);
            Assert.Equal(44100u, convertedNcw.Header.SampleRate);
            Assert.Equal((ushort)1, convertedNcw.Header.Channels);
            Assert.True(convertedNcw.Samples.Length > 0);
            Assert.True(convertedNcw.Header.SignatureRecognized);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void CliProcess_FolderResample_RunsAndConvertsFiles()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "conNCW-NG.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }

        string[] candidates = [
            Path.Combine(dir, "src", "ConNCW.Cli", "bin", "x64", "Release", "net8.0", "conNCW-NG.exe"),
            Path.Combine(dir, "src", "ConNCW.Cli", "bin", "Release", "net8.0", "conNCW-NG.exe"),
            Path.Combine(dir, "src", "ConNCW.Cli", "bin", "x64", "Debug", "net8.0", "conNCW-NG.exe"),
            Path.Combine(dir, "src", "ConNCW.Cli", "bin", "Debug", "net8.0", "conNCW-NG.exe")
        ];
        string cliExe = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        Assert.True(File.Exists(cliExe), $"CLI executable not found at: {cliExe}");

        string tempDir = Path.Combine(Path.GetTempPath(), "cli_folder_test_" + Guid.NewGuid().ToString("N"));
        string inDir = Path.Combine(tempDir, "in");
        string outDir = Path.Combine(tempDir, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        try
        {
            // Generate 3 sample NCW files (24-bit 96kHz)
            for (int i = 0; i < 3; i++)
            {
                var samples = new int[4800];
                for (int s = 0; s < 4800; s++)
                {
                    samples[s] = (int)(Math.Sin(2 * Math.PI * (440.0 + i * 50) * s / 96000.0) * 8000000.0);
                }
                var fmt = new AudioFormat { Channels = 1, BitsPerSample = 24, SampleRate = 96000, SampleCount = 4800 };
                var ncw = NcwFile.FromPcm(fmt, samples, "01A89ED631010000");
                ncw.Save(Path.Combine(inDir, $"sample_{i}.ncw"));
            }

            // Execute CLI process
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cliExe,
                Arguments = $"\"{inDir}\" \"{outDir}\" -n2n --resampler -sr 44100 -b 16 --ns 6 --log NUL",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);

            // Verify all 3 files exist in outDir and are 16-bit 44.1kHz
            var outFiles = Directory.GetFiles(outDir, "*.ncw");
            Assert.Equal(3, outFiles.Length);

            foreach (var f in outFiles)
            {
                var ncw = NcwFile.Open(f, TestSignatures, false);
                Assert.Equal((ushort)16, ncw.Header.Bits);
                Assert.Equal(44100u, ncw.Header.SampleRate);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void NcwToNcw_Already16Bit_BypassesDirectlyWithoutReEncoding_WhenBypassLowDepthSet()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ncw_bypass_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string srcFile = Path.Combine(tempDir, "input_16b.ncw");
            string dstFile = Path.Combine(tempDir, "output_16b.ncw");

            // Create a genuine 16-bit 44.1kHz NCW
            var format = new AudioFormat
            {
                Channels = 2,
                BitsPerSample = 16,
                SampleRate = 44100,
                SampleCount = 512
            };
            var samples = new int[512 * 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 10);

            var ncw = NcwFile.FromPcm(format, samples, "01A89ED631010000");
            ncw.Save(srcFile);

            using var logger = new ConversionLogger("NUL");
            ConversionResult? lastResult = null;
            logger.ResultReported += res => lastResult = res;

            var engine = new ConversionEngine(TestSignatures, logger);
            var options = new ConversionOptions
            {
                Direction = ConversionDirection.NcwToNcw,
                BypassLowDepth = true,
                Resample = new ResampleOptions
                {
                    TargetBits = 16,
                    TargetSampleRate = 44100,
                    DitherProfile = 6
                }
            };

            engine.ConvertFile(srcFile, dstFile, options);

            Assert.True(File.Exists(dstFile));
            Assert.NotNull(lastResult);
            Assert.Equal(ConversionStatus.Ok, lastResult.Status);
            Assert.Contains("copie directe sans réencodage", lastResult.Message);

            // Output should have exact same byte length as input because it was a direct file copy
            Assert.Equal(new FileInfo(srcFile).Length, new FileInfo(dstFile).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void NcwToNcw_Already16Bit_ReEncodesNormally_WhenBypassLowDepthNotSet()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ncw_nobypass_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string srcFile = Path.Combine(tempDir, "input_16b.ncw");
            string dstFile = Path.Combine(tempDir, "output_16b.ncw");

            var format = new AudioFormat
            {
                Channels = 2,
                BitsPerSample = 16,
                SampleRate = 44100,
                SampleCount = 512
            };
            var samples = new int[512 * 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 10);

            var ncw = NcwFile.FromPcm(format, samples, "01A89ED631010000");
            ncw.Save(srcFile);

            using var logger = new ConversionLogger("NUL");
            ConversionResult? lastResult = null;
            logger.ResultReported += res => lastResult = res;

            var engine = new ConversionEngine(TestSignatures, logger);
            var options = new ConversionOptions
            {
                Direction = ConversionDirection.NcwToNcw,
                BypassLowDepth = false, // Default: no bypass
                Resample = new ResampleOptions
                {
                    TargetBits = 16,
                    TargetSampleRate = 44100,
                    DitherProfile = 6
                }
            };

            engine.ConvertFile(srcFile, dstFile, options);

            Assert.True(File.Exists(dstFile));
            Assert.NotNull(lastResult);
            Assert.Equal(ConversionStatus.Ok, lastResult.Status);
            Assert.Contains("rééchantillonné en RAM", lastResult.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void WavToNcw_Already16Bit_BypassesReSampler_WhenBypassLowDepthSet()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wav_bypass_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string srcFile = Path.Combine(tempDir, "input_16b.wav");
            string dstFile = Path.Combine(tempDir, "output_16b.ncw");

            var format = new AudioFormat
            {
                Channels = 2,
                BitsPerSample = 16,
                SampleRate = 44100,
                SampleCount = 512
            };
            var pcm = new byte[512 * 2 * 2];
            var wav = WavFile.FromRaw(format, pcm);
            wav.Save(srcFile);

            using var logger = new ConversionLogger("NUL");
            ConversionResult? lastResult = null;
            logger.ResultReported += res => lastResult = res;

            var engine = new ConversionEngine(TestSignatures, logger);
            var options = new ConversionOptions
            {
                Direction = ConversionDirection.WavToNcw,
                BypassLowDepth = true,
                Resample = new ResampleOptions
                {
                    TargetBits = 16,
                    TargetSampleRate = 44100,
                    DitherProfile = 6
                }
            };

            engine.ConvertFile(srcFile, dstFile, options);

            Assert.True(File.Exists(dstFile));
            Assert.NotNull(lastResult);
            Assert.Equal(ConversionStatus.Ok, lastResult.Status);
            Assert.Contains("bypass-lowdepth", lastResult.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
