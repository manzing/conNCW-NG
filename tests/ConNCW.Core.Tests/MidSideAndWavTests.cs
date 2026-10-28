using ConNCW.Core.Engine;
using ConNCW.Core.Logging;
using ConNCW.Core.Models;
using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;
using ConNCW.Core.Wav;
using Xunit;

namespace ConNCW.Core.Tests;

public class MidSideAndWavTests
{
    private static readonly SignatureStore Signatures = new(new[]
    {
        new SignatureEntry { HexBytes = "01A89ED631010000", Label = "TestSignature" }
    });

    [Fact]
    public void MidSide_Channel0ControlsTransform_Channel1Ignored()
    {
        // Test que le canal 0 sélectionne Mid/Side et que le flag du canal 1 n'écrase rien
        // Créons un fichier NCW binaire manuellement pour contrôler exactement les flags des blocs.
        string path = Path.Combine(Path.GetTempPath(), $"midside_probe_{Guid.NewGuid():N}.ncw");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                // Header (120 octets)
                writer.Write(Convert.FromHexString("01A89ED631010000")); // sig (8)
                writer.Write((ushort)2); // channels (2)
                writer.Write((ushort)16); // bits (16)
                writer.Write((uint)44100); // sampleRate (4)
                writer.Write((uint)512); // numSamples (4)
                writer.Write((uint)120); // tableOffset (4)
                writer.Write((uint)(120 + 2 * 4)); // dataOffset (4) (table has 2 entries = 8 bytes)
                
                // Calcul de la taille des données : 2 canaux * (16 bytes header + 16 * 64 bytes payload)
                int channelBlockSize = 16 + (16 * 64); // 16 + 1024 = 1040 bytes
                uint totalDataSize = (uint)(channelBlockSize * 2); // 2080 bytes
                writer.Write(totalDataSize); // dataSize (4)
                writer.Write(0u); // sampleFormatWord (4)
                writer.Write(new byte[84]); // someData (84)

                // Table d'offsets (2 entrées : 0 et sentinelle = totalDataSize)
                writer.Write(0u);
                writer.Write(totalDataSize);

                // Sous-bloc Canal 0 (Mid) : Flag = 1 (MidSide), unencoded raw (-16)
                writer.Write(NcwHeader.BlockSignature);
                writer.Write(100); // baseValue
                writer.Write((short)-16); // raw 16-bit
                writer.Write((ushort)1); // Flags = 1 (MidSide)
                writer.Write(0u);
                for (int i = 0; i < 512; i++)
                {
                    writer.Write((short)100); // mid = 100
                }

                // Sous-bloc Canal 1 (Side) : Flag = 0, unencoded raw (-16)
                writer.Write(NcwHeader.BlockSignature);
                writer.Write(20); // baseValue
                writer.Write((short)-16); // raw 16-bit
                writer.Write((ushort)0); // Flags = 0 (canal 1 ne doit PAS annuler le Mid/Side du canal 0)
                writer.Write(0u);
                for (int i = 0; i < 512; i++)
                {
                    writer.Write((short)20); // side = 20
                }
            }

            var loaded = NcwFile.Open(path, Signatures);
            Assert.Equal(512 * 2, loaded.Samples.Length);

            // Reconstitution attendue : Left = mid + side = 120, Right = mid - side = 80
            for (int k = 0; k < 512; k++)
            {
                Assert.Equal(120, loaded.Samples[k * 2]);
                Assert.Equal(80, loaded.Samples[k * 2 + 1]);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MidSide_Channel0Direct_Channel1Flag1_RemainsDirectStereo()
    {
        // Quand canal 0 a Flag = 0 et canal 1 a Flag = 1, Kontakt et conNCW-NG doivent rester en Stéréo Directe
        string path = Path.Combine(Path.GetTempPath(), $"direct_probe_{Guid.NewGuid():N}.ncw");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Convert.FromHexString("01A89ED631010000"));
                writer.Write((ushort)2);
                writer.Write((ushort)16);
                writer.Write((uint)44100);
                writer.Write((uint)512);
                writer.Write((uint)120);
                writer.Write((uint)(120 + 2 * 4));
                
                int channelBlockSize = 16 + (16 * 64);
                uint totalDataSize = (uint)(channelBlockSize * 2);
                writer.Write(totalDataSize);
                writer.Write(0u);
                writer.Write(new byte[84]);

                writer.Write(0u);
                writer.Write(totalDataSize);

                // Sous-bloc Canal 0 : Flag = 0
                writer.Write(NcwHeader.BlockSignature);
                writer.Write(100);
                writer.Write((short)-16);
                writer.Write((ushort)0); // Flag = 0 -> direct
                writer.Write(0u);
                for (int i = 0; i < 512; i++) writer.Write((short)100);

                // Sous-bloc Canal 1 : Flag = 1
                writer.Write(NcwHeader.BlockSignature);
                writer.Write(20);
                writer.Write((short)-16);
                writer.Write((ushort)1); // Flag = 1
                writer.Write(0u);
                for (int i = 0; i < 512; i++) writer.Write((short)20);
            }

            var loaded = NcwFile.Open(path, Signatures);
            // Stéréo directe : Left = 100, Right = 20
            for (int k = 0; k < 512; k++)
            {
                Assert.Equal(100, loaded.Samples[k * 2]);
                Assert.Equal(20, loaded.Samples[k * 2 + 1]);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WavToNcwAndBack_32Bit_ExtremeNegativeValues_NoOverflowCorruption()
    {
        // Régression critique pour le bug "1 << 32"
        string dir = Path.Combine(Path.GetTempPath(), $"wav_32bit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        string wavPath = Path.Combine(dir, "input.wav");
        string ncwPath = Path.Combine(dir, "output.ncw");
        string backWavPath = Path.Combine(dir, "roundtrip.wav");

        try
        {
            var format = new AudioFormat
            {
                Channels = 1,
                BitsPerSample = 32,
                SampleRate = 48000,
                SampleCount = 4
            };

            int[] testSamples = { int.MinValue, -1, 0, int.MaxValue };
            var pcmBytes = new byte[testSamples.Length * 4];
            for (int i = 0; i < testSamples.Length; i++)
            {
                var b = BitConverter.GetBytes(testSamples[i]);
                Buffer.BlockCopy(b, 0, pcmBytes, i * 4, 4);
            }

            var wav = WavFile.FromRaw(format, pcmBytes);
            wav.Save(wavPath);

            using var logger = new ConversionLogger(Path.Combine(dir, "conversion.log"));
            var engine = new ConversionEngine(Signatures, logger);
            engine.ConvertFile(wavPath, ncwPath, new ConversionOptions
            {
                Direction = ConversionDirection.WavToNcw,
                OutputSignatureHex = "01A89ED631010000"
            });

            Assert.True(File.Exists(ncwPath));

            engine.ConvertFile(ncwPath, backWavPath, new ConversionOptions
            {
                Direction = ConversionDirection.NcwToWav
            });

            Assert.True(File.Exists(backWavPath));
            var backWav = WavFile.Open(backWavPath);

            for (int i = 0; i < testSamples.Length; i++)
            {
                int actual = BitConverter.ToInt32(backWav.AudioData, i * 4);
                Assert.Equal(testSamples[i], actual);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
