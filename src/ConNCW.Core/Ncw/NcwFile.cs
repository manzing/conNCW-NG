using ConNCW.Core.Bit;
using ConNCW.Core.Models;
using ConNCW.Core.Signatures;

namespace ConNCW.Core.Ncw;

/// <summary>
/// Portage complet de NCWParser3.pas + BitProcess3.pas.
///
/// Corrections apportées par rapport à l'original :
/// - Signature comparée en bloc entier via SignatureStore (fix du bug octet-par-octet).
/// - Mode bypassSignature pour forcer le décodage et tester la plausibilité du résultat.
/// - BitReader/BitWriter remplacent les manipulations de pointeurs bruts.
/// </summary>
public sealed class NcwFile
{
    public NcwHeader Header { get; }

    /// <summary>Échantillons entrelacés par frame (frame = 1 échantillon x tous les canaux), signés 32 bits en interne.</summary>
    public int[] Samples { get; }

    private NcwFile(NcwHeader header, int[] samples)
    {
        Header = header;
        Samples = samples;
    }

    public static NcwFile Open(string path, SignatureStore signatures, bool bypassSignature = false)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var signature = reader.ReadBytes(8);
        ushort channels = reader.ReadUInt16();
        ushort bits = reader.ReadUInt16();
        uint sampleRate = reader.ReadUInt32();
        uint numSamples = reader.ReadUInt32();
        uint tableOffset = reader.ReadUInt32();
        uint dataOffset = reader.ReadUInt32();
        uint dataSize = reader.ReadUInt32();
        byte[] someData = reader.ReadBytes(88);

        var match = signatures.Match(signature);
        if (match is null && !bypassSignature)
        {
            throw new UnknownNcwSignatureException(signature);
        }

        var header = new NcwHeader
        {
            RawSignature = signature,
            Channels = channels,
            Bits = bits,
            SampleRate = sampleRate,
            NumSamples = numSamples,
            TableOffset = tableOffset,
            DataOffset = dataOffset,
            DataSize = dataSize,
            SomeData = someData,
            MatchedSignatureLabel = match?.Label
        };

        if (bypassSignature)
        {
            // Validation structurelle minimale en mode bypass : header cohérent avant de tenter le décodage.
            if (!header.ToAudioFormat().IsPlausible())
            {
                throw new InvalidDataException("Header NCW incohérent (mode bypass) : channels/bits/samplerate implausibles.");
            }
        }

        int blockCount = header.BlockCount;
        stream.Seek(header.TableOffset, SeekOrigin.Begin);
        var offsets = new uint[blockCount + 1];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = reader.ReadUInt32();
        }
        header.BlockOffsets = offsets;

        var samples = DecodeBlocks(stream, reader, header);

        return new NcwFile(header, samples);
    }

    private static int[] DecodeBlocks(FileStream stream, BinaryReader reader, NcwHeader header)
    {
        int channels = Math.Max(header.Channels, 1);
        long totalFrames = header.NumSamples;
        var samples = new int[totalFrames * channels];

        int blockCount = header.BlockCount;
        var channelBuffers = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            channelBuffers[c] = new int[NcwHeader.BlockSampleCount];
        }

        long frameCursor = 0;

        for (int b = 0; b < blockCount; b++)
        {
            stream.Seek(header.DataOffset + header.BlockOffsets[b], SeekOrigin.Begin);

            bool midSide = false;

            for (int c = 0; c < channels; c++)
            {
                var blockHeader = ReadBlockHeader(reader);
                midSide = blockHeader.IsMidSide;
                int effBits = blockHeader.EffectiveBits(header.Bits);
                int byteLen = effBits * 64;
                var blockBytes = reader.ReadBytes(byteLen);

                DecodeBlockSamples(blockBytes, effBits, blockHeader, channelBuffers[c]);
            }

            int samplesInThisBlock = (int)Math.Min(NcwHeader.BlockSampleCount, totalFrames - frameCursor);

            for (int k = 0; k < samplesInThisBlock; k++)
            {
                if (midSide && channels == 2)
                {
                    int mid = channelBuffers[0][k];
                    int side = channelBuffers[1][k];
                    samples[frameCursor * 2] = mid + side;
                    samples[frameCursor * 2 + 1] = mid - side;
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                    {
                        samples[frameCursor * channels + c] = channelBuffers[c][k];
                    }
                }
                frameCursor++;
            }

            if (frameCursor >= totalFrames)
            {
                break;
            }
        }

        return samples;
    }

    private static NcwBlockHeader ReadBlockHeader(BinaryReader reader)
    {
        var sig = reader.ReadBytes(4);
        int baseValue = reader.ReadInt32();
        short bits = reader.ReadInt16();
        ushort flags = reader.ReadUInt16();
        uint zero = reader.ReadUInt32();

        return new NcwBlockHeader
        {
            Signature = sig,
            BaseValue = baseValue,
            Bits = bits,
            Flags = flags,
            Zero = zero
        };
    }

    /// <summary>
    /// Décode un bloc (delta-decoding si bits > 0, valeurs absolues si bits < 0 ou = 0
    /// en tolérant les deux conventions rencontrées dans des NCW réels).
    /// </summary>
    private static void DecodeBlockSamples(byte[] blockBytes, int effBits, NcwBlockHeader blockHeader, int[] output)
    {
        var bitReader = new BitReader(blockBytes);
        bool encoded = blockHeader.Bits > 0;

        if (encoded)
        {
            int current = blockHeader.BaseValue;
            output[0] = current;
            for (int i = 1; i < NcwHeader.BlockSampleCount; i++)
            {
                int diff = bitReader.ReadSignedBits(effBits);
                current += diff;
                output[i] = current;
            }
        }
        else
        {
            for (int i = 0; i < NcwHeader.BlockSampleCount; i++)
            {
                output[i] = bitReader.ReadSignedBits(effBits);
            }
        }
    }

    /// <summary>
    /// Construit un NcwFile en mémoire à partir de données PCM (pour l'encodage WAV -> NCW),
    /// prêt à être sérialisé via Save().
    /// </summary>
    public static NcwFile FromPcm(AudioFormat format, int[] interleavedSamples, string signatureHex)
    {
        var header = new NcwHeader
        {
            RawSignature = Convert.FromHexString(signatureHex),
            Channels = format.Channels,
            Bits = format.BitsPerSample,
            SampleRate = format.SampleRate,
            NumSamples = format.SampleCount,
            TableOffset = NcwHeader.HeaderSize,
            SomeData = new byte[88]
        };
        return new NcwFile(header, interleavedSamples);
    }

    public void Save(string path)
    {
        int channels = Math.Max(Header.Channels, 1);
        int totalFrames = (int)Header.NumSamples;
        int blockCount = Header.BlockCount;

        var blockPayloads = new List<byte[]>(blockCount * channels);
        var blockOffsets = new List<uint>(blockCount + 1);
        uint cursor = 0;

        var channelBuffer = new int[NcwHeader.BlockSampleCount];

        for (int b = 0; b < blockCount; b++)
        {
            blockOffsets.Add(cursor);
            int samplesInBlock = Math.Min(NcwHeader.BlockSampleCount, totalFrames - b * NcwHeader.BlockSampleCount);

            for (int c = 0; c < channels; c++)
            {
                for (int k = 0; k < NcwHeader.BlockSampleCount; k++)
                {
                    int frameIdx = b * NcwHeader.BlockSampleCount + k;
                    channelBuffer[k] = frameIdx < totalFrames ? Samples[frameIdx * channels + c] : 0;
                }

                var (payload, blockHeaderBytes) = EncodeChannelBlock(channelBuffer, samplesInBlock, Header.Bits);
                var full = new byte[blockHeaderBytes.Length + payload.Length];
                Buffer.BlockCopy(blockHeaderBytes, 0, full, 0, blockHeaderBytes.Length);
                Buffer.BlockCopy(payload, 0, full, blockHeaderBytes.Length, payload.Length);

                blockPayloads.Add(full);
                cursor += (uint)full.Length;
            }
        }
        blockOffsets.Add(cursor);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        uint tableOffset = NcwHeader.HeaderSize;
        uint dataOffset = (uint)(tableOffset + blockOffsets.Count * 4);
        uint dataSize = cursor;

        writer.Write(Header.RawSignature.Length == 8 ? Header.RawSignature : Header.RawSignature.Concat(new byte[8]).Take(8).ToArray());
        writer.Write(Header.Channels);
        writer.Write(Header.Bits);
        writer.Write(Header.SampleRate);
        writer.Write(Header.NumSamples);
        writer.Write(tableOffset);
        writer.Write(dataOffset);
        writer.Write(dataSize);
        writer.Write(new byte[88]);

        foreach (var off in blockOffsets)
        {
            writer.Write(off);
        }

        foreach (var block in blockPayloads)
        {
            writer.Write(block);
        }
    }

    private static (byte[] payload, byte[] headerBytes) EncodeChannelBlock(int[] samples, int validCount, int bitsPerSample)
    {
        int min = int.MaxValue, max = int.MinValue;
        var diffs = new int[NcwHeader.BlockSampleCount];
        diffs[0] = 0;
        for (int i = 1; i < NcwHeader.BlockSampleCount; i++)
        {
            int a = i < validCount ? samples[i] : 0;
            int prev = (i - 1) < validCount ? samples[i - 1] : 0;
            int d = a - prev;
            diffs[i] = d;
            if (d < min) min = d;
            if (d > max) max = d;
        }
        if (min == int.MaxValue) { min = 0; max = 0; }

        int neededBits = MinBitsForRange(min, max);
        bool useEncoded = neededBits < bitsPerSample;
        int effBits = useEncoded ? neededBits : bitsPerSample;

        var bitWriter = new BitWriter();
        if (useEncoded)
        {
            for (int i = 1; i < NcwHeader.BlockSampleCount; i++)
            {
                bitWriter.WriteSignedBits(diffs[i], effBits);
            }
        }
        else
        {
            for (int i = 0; i < NcwHeader.BlockSampleCount; i++)
            {
                int v = i < validCount ? samples[i] : 0;
                bitWriter.WriteSignedBits(v, effBits);
            }
        }

        var payload = bitWriter.ToArray();
        int expectedLen = effBits * 64;
        if (payload.Length < expectedLen)
        {
            Array.Resize(ref payload, expectedLen);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(NcwHeader.BlockSignature);
        bw.Write(samples.Length > 0 ? samples[0] : 0);
        bw.Write((short)(useEncoded ? effBits : -effBits));
        bw.Write((ushort)0);
        bw.Write(0u);

        return (payload, ms.ToArray());
    }

    private static int MinBitsForRange(int min, int max)
    {
        int bits = 2;
        while (bits < 32)
        {
            long lo = -(1L << (bits - 1));
            long hi = (1L << (bits - 1)) - 1;
            if (min >= lo && max <= hi)
            {
                return bits;
            }
            bits++;
        }
        return 32;
    }
}

public sealed class UnknownNcwSignatureException : Exception
{
    public byte[] Signature { get; }

    public UnknownNcwSignatureException(byte[] signature)
        : base($"Signature NCW inconnue: {Convert.ToHexString(signature)}")
    {
        Signature = signature;
    }
}
