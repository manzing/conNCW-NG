using ConNCW.Core.Bit;
using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;
using Xunit;

namespace ConNCW.Core.Tests;

public class NcwComplianceTests
{
    private static readonly SignatureStore EmptySignatures = new(new[]
    {
        new SignatureEntry { HexBytes = "01A89ED631010000", Label = "TestSignature" }
    });

    [Fact]
    public void BlockHeader_Flags_CorrectlyDetectsMidSideAndFloat()
    {
        var hDirect = new NcwBlockHeader { Flags = 0 };
        Assert.False(hDirect.IsMidSide);
        Assert.False(hDirect.IsFloat);

        var hMidSide = new NcwBlockHeader { Flags = 1 };
        Assert.True(hMidSide.IsMidSide);
        Assert.False(hMidSide.IsFloat);

        var hFloat = new NcwBlockHeader { Flags = 2 };
        Assert.False(hFloat.IsMidSide);
        Assert.True(hFloat.IsFloat);

        var hCombined = new NcwBlockHeader { Flags = 3 };
        Assert.True(hCombined.IsMidSide);
        Assert.True(hCombined.IsFloat);
    }

    [Fact]
    public void BlockHeader_EffectiveBits_SafelyClamped()
    {
        var hNormal = new NcwBlockHeader { Bits = 16 };
        Assert.Equal(16, hNormal.EffectiveBits(24));

        var hZero = new NcwBlockHeader { Bits = 0 };
        Assert.Equal(24, hZero.EffectiveBits(24));

        var hNegative = new NcwBlockHeader { Bits = -24 };
        Assert.Equal(24, hNegative.EffectiveBits(24));

        var hMinOverflow = new NcwBlockHeader { Bits = short.MinValue };
        Assert.Equal(32, hMinOverflow.EffectiveBits(16));
    }

    [Fact]
    public void NcwFile_RoundtripPcm16_Lossless()
    {
        RunRoundtripTest(channels: 2, bitsPerSample: 16, sampleRate: 44100, frameCount: 1200);
    }

    [Fact]
    public void NcwFile_RoundtripPcm24_Lossless()
    {
        RunRoundtripTest(channels: 2, bitsPerSample: 24, sampleRate: 48000, frameCount: 750);
    }

    [Fact]
    public void NcwFile_RoundtripPcm32_Lossless_WithNegativeValues()
    {
        RunRoundtripTest(channels: 1, bitsPerSample: 32, sampleRate: 96000, frameCount: 600);
    }

    [Fact]
    public void NcwFile_Roundtrip_BoundaryFrameCounts()
    {
        int[] frameCounts = { 1, 511, 512, 513, 1024, 1025 };
        foreach (int count in frameCounts)
        {
            RunRoundtripTest(channels: 1, bitsPerSample: 16, sampleRate: 44100, frameCount: count);
        }
    }

    private static void RunRoundtripTest(ushort channels, ushort bitsPerSample, uint sampleRate, int frameCount)
    {
        var rng = new Random(42);
        int totalSamples = frameCount * channels;
        var originalSamples = new int[totalSamples];

        int maxVal = bitsPerSample == 32 ? int.MaxValue / 2 : (1 << (bitsPerSample - 1)) - 1;
        int minVal = bitsPerSample == 32 ? int.MinValue / 2 : -(1 << (bitsPerSample - 1));

        for (int i = 0; i < totalSamples; i++)
        {
            originalSamples[i] = rng.Next(minVal, maxVal);
        }

        var format = new AudioFormat
        {
            Channels = channels,
            BitsPerSample = bitsPerSample,
            SampleRate = sampleRate,
            SampleCount = (uint)frameCount
        };

        var ncw = NcwFile.FromPcm(format, originalSamples, "01A89ED631010000");

        string tempPath = Path.Combine(Path.GetTempPath(), $"ncw_test_{Guid.NewGuid():N}.ncw");
        try
        {
            ncw.Save(tempPath);
            var loaded = NcwFile.Open(tempPath, EmptySignatures);

            Assert.Equal(format.Channels, loaded.Header.Channels);
            Assert.Equal(format.BitsPerSample, loaded.Header.Bits);
            Assert.Equal(format.SampleRate, loaded.Header.SampleRate);
            Assert.Equal(format.SampleCount, loaded.Header.NumSamples);
            Assert.Equal(originalSamples.Length, loaded.Samples.Length);

            for (int i = 0; i < originalSamples.Length; i++)
            {
                Assert.Equal(originalSamples[i], loaded.Samples[i]);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void NcwFile_Open_ValidatesSentinelAndTable()
    {
        var format = new AudioFormat
        {
            Channels = 1,
            BitsPerSample = 16,
            SampleRate = 44100,
            SampleCount = 512
        };
        var samples = new int[512];
        var ncw = NcwFile.FromPcm(format, samples, "01A89ED631010000");

        string validPath = Path.Combine(Path.GetTempPath(), $"ncw_val_{Guid.NewGuid():N}.ncw");
        ncw.Save(validPath);

        try
        {
            var rawBytes = File.ReadAllBytes(validPath);

            // 1. Corrupt sentinel at offset 124 (table entry 1 for 1 block is sentinel)
            var badSentinelBytes = (byte[])rawBytes.Clone();
            badSentinelBytes[124] ^= 0xFF;
            string badSentinelPath = validPath + "_bad_sentinel";
            File.WriteAllBytes(badSentinelPath, badSentinelBytes);
            try
            {
                Assert.Throws<InvalidDataException>(() => NcwFile.Open(badSentinelPath, EmptySignatures));
            }
            finally
            {
                File.Delete(badSentinelPath);
            }

            // 2. Corrupt TableOffset at offset 20
            var badTableOffsetBytes = (byte[])rawBytes.Clone();
            badTableOffsetBytes[20] = 0x80; // != 120
            string badTablePath = validPath + "_bad_table";
            File.WriteAllBytes(badTablePath, badTableOffsetBytes);
            try
            {
                Assert.Throws<InvalidDataException>(() => NcwFile.Open(badTablePath, EmptySignatures));
            }
            finally
            {
                File.Delete(badTablePath);
            }
        }
        finally
        {
            if (File.Exists(validPath)) File.Delete(validPath);
        }
    }
}
