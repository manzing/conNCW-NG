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
/// - BitReader/BitWriter optimisés remplaçant les manipulations de pointeurs bruts.
/// - Validation stricte de la table d'offsets, de la sentinelle et des bornes de fichiers.
/// - Conformité FORMAT.md : gestion rigoureuse du 512e delta, du padding répété et du Mid/Side (canal 0).
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

    /// <summary>
    /// Lit uniquement le header fixe (120 octets : signature + channels + bits + sampleRate
    /// + numSamples + tableOffset + dataOffset + dataSize + sampleFormatWord + someData). Ne seek jamais vers
    /// la table d'offsets et ne décode aucun bloc audio. Conçu pour être rapide sur de très
    /// grands volumes de fichiers (scan d'inventaire).
    /// </summary>
    public static NcwHeader ReadHeaderOnly(string path, SignatureStore signatures, bool bypassSignature = true)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var header = ReadHeaderCore(reader, signatures, bypassSignature);

        if (bypassSignature && !header.ToAudioFormat().IsPlausible())
        {
            throw new InvalidDataException("Header NCW incohérent (mode bypass) : channels/bits/samplerate implausibles.");
        }

        return header;
    }

    /// <summary>
    /// Lecture bas niveau du header fixe. Partagée entre Open() et ReadHeaderOnly() pour
    /// éviter toute divergence entre le chemin "décodage complet" et le chemin "inventaire".
    /// </summary>
    private static NcwHeader ReadHeaderCore(BinaryReader reader, SignatureStore signatures, bool bypassSignature)
    {
        if (reader.BaseStream.Length < NcwHeader.HeaderSize)
        {
            throw new InvalidDataException("Fichier NCW tronqué : taille inférieure à l'en-tête minimal de 120 octets.");
        }

        var signature = reader.ReadBytes(8);
        ushort channels = reader.ReadUInt16();
        ushort bits = reader.ReadUInt16();
        uint sampleRate = reader.ReadUInt32();
        uint numSamples = reader.ReadUInt32();
        uint tableOffset = reader.ReadUInt32();
        uint dataOffset = reader.ReadUInt32();
        uint dataSize = reader.ReadUInt32();
        uint sampleFormatWord = reader.ReadUInt32();
        byte[] someData = reader.ReadBytes(84);

        var match = signatures.Match(signature);
        if (match is null && !bypassSignature)
        {
            throw new UnknownNcwSignatureException(signature);
        }

        return new NcwHeader
        {
            RawSignature = signature,
            Channels = channels,
            Bits = bits,
            SampleRate = sampleRate,
            NumSamples = numSamples,
            TableOffset = tableOffset,
            DataOffset = dataOffset,
            DataSize = dataSize,
            SampleFormatWord = sampleFormatWord,
            SomeData = someData,
            MatchedSignatureLabel = match?.Label
        };
    }

    public static NcwFile Open(string path, SignatureStore signatures, bool bypassSignature = false)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var header = ReadHeaderCore(reader, signatures, bypassSignature);

        if (bypassSignature)
        {
            // Validation structurelle minimale en mode bypass : header cohérent avant de tenter le décodage.
            if (!header.ToAudioFormat().IsPlausible())
            {
                throw new InvalidDataException("Header NCW incohérent (mode bypass) : channels/bits/samplerate implausibles.");
            }
        }

        if (header.TableOffset != NcwHeader.HeaderSize)
        {
            throw new InvalidDataException($"Offset de table NCW invalide : {header.TableOffset} (attendu : {NcwHeader.HeaderSize}).");
        }

        int blockCount = header.BlockCount;
        long expectedTableBytes = (long)(blockCount + 1) * 4;
        if ((long)header.TableOffset + expectedTableBytes > stream.Length)
        {
            throw new InvalidDataException("Fichier NCW tronqué : impossible de lire la table d'offsets complète.");
        }

        stream.Seek(header.TableOffset, SeekOrigin.Begin);
        var offsets = new uint[blockCount + 1];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = reader.ReadUInt32();
            if (i > 0 && offsets[i] <= offsets[i - 1])
            {
                throw new InvalidDataException($"Table d'offsets NCW corrompue : l'offset du bloc {i} ({offsets[i]}) n'est pas strictement supérieur au précédent ({offsets[i - 1]}).");
            }
        }

        if (blockCount > 0 && offsets[0] != 0)
        {
            throw new InvalidDataException($"Table d'offsets NCW invalide : le premier offset de bloc doit valoir 0 (obtenu : {offsets[0]}).");
        }

        if (blockCount > 0 && offsets[blockCount] != header.DataSize)
        {
            throw new InvalidDataException($"Sentinelle NCW invalide : l'offset sentinelle ({offsets[blockCount]}) ne correspond pas à DataSize ({header.DataSize}).");
        }

        if ((long)header.DataOffset + header.DataSize > stream.Length)
        {
            throw new InvalidDataException("Les données audio déclarées dans le header NCW dépassent la fin physique du fichier.");
        }

        header.BlockOffsets = offsets;

        var samples = DecodeBlocks(stream, reader, header);

        return new NcwFile(header, samples);
    }

    private static int[] DecodeBlocks(FileStream stream, BinaryReader reader, NcwHeader header)
    {
        int channels = Math.Max((int)header.Channels, 1);
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
                if (c == 0)
                {
                    // Conforme à la spécification Kontakt : seul le sous-bloc 0 (canal 0) détermine le mode Mid/Side
                    midSide = blockHeader.IsMidSide && channels == 2;
                }

                int effBits = blockHeader.EffectiveBits(header.Bits);
                int byteLen = effBits * 64;
                var blockBytes = reader.ReadBytes(byteLen);
                if (blockBytes.Length < byteLen)
                {
                    throw new InvalidDataException($"Données de bloc NCW incomplètes au bloc {b}, canal {c} : attendu {byteLen} octets, obtenu {blockBytes.Length}.");
                }

                DecodeBlockSamples(blockBytes, effBits, blockHeader, channelBuffers[c]);
            }

            int samplesInThisBlock = (int)Math.Min(NcwHeader.BlockSampleCount, totalFrames - frameCursor);

            for (int k = 0; k < samplesInThisBlock; k++)
            {
                if (midSide && channels == 2)
                {
                    int mid = channelBuffers[0][k];
                    int side = channelBuffers[1][k];
                    samples[frameCursor * 2] = unchecked(mid + side);
                    samples[frameCursor * 2 + 1] = unchecked(mid - side);
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                    {
                        samples[frameCursor * channels + c] = channelBuffers[c][k];
                    }
                }

                frameCursor++;
                if (frameCursor >= totalFrames)
                {
                    break;
                }
            }
        }

        return samples;
    }

    private static NcwBlockHeader ReadBlockHeader(BinaryReader reader)
    {
        var sig = reader.ReadBytes(4);
        if (sig.Length < 4 ||
            sig[0] != NcwHeader.BlockSignature[0] ||
            sig[1] != NcwHeader.BlockSignature[1] ||
            sig[2] != NcwHeader.BlockSignature[2] ||
            sig[3] != NcwHeader.BlockSignature[3])
        {
            throw new InvalidDataException($"Signature de bloc NCW invalide (obtenu {Convert.ToHexString(sig)}, attendu 160C9A3E).");
        }

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
    /// Décode un bloc (delta-decoding si bits > 0, valeurs absolues si bits &lt;= 0)
    /// en consommant le 512e delta requis par la spécification NCW.
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

            // Consommation du 512e delta requis par la spécification NCW (résultat non émis)
            if (bitReader.BitsRemaining >= effBits)
            {
                _ = bitReader.ReadSignedBits(effBits);
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
            SampleFormatWord = 0,
            SomeData = new byte[84]
        };
        return new NcwFile(header, interleavedSamples);
    }

    public void Save(string path)
    {
        int channels = Math.Max((int)Header.Channels, 1);
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
                int lastSample = samplesInBlock > 0
                    ? Samples[(b * NcwHeader.BlockSampleCount + samplesInBlock - 1) * channels + c]
                    : 0;

                for (int k = 0; k < NcwHeader.BlockSampleCount; k++)
                {
                    int frameIdx = b * NcwHeader.BlockSampleCount + k;
                    // Conforme à la spec : le padding répète le dernier échantillon valide pour éviter les deltas parasites
                    channelBuffer[k] = frameIdx < totalFrames ? Samples[frameIdx * channels + c] : lastSample;
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
        writer.Write(Header.SampleFormatWord);
        writer.Write(Header.SomeData.Length == 84 ? Header.SomeData : new byte[84]);

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
            int d = samples[i] - samples[i - 1];
            diffs[i] = d;
            if (d < min) min = d;
            if (d > max) max = d;
        }

        if (min == int.MaxValue) { min = 0; max = 0; }

        int neededBits = MinBitsForRange(min, max);
        bool useEncoded = neededBits < bitsPerSample;
        int effBits = useEncoded ? neededBits : bitsPerSample;

        var bitWriter = new BitWriter(effBits * 64);
        if (useEncoded)
        {
            for (int i = 1; i < NcwHeader.BlockSampleCount; i++)
            {
                bitWriter.WriteSignedBits(diffs[i], effBits);
            }
            // Conforme à la spec : 512e delta terminal égal à 0
            bitWriter.WriteSignedBits(0, effBits);
        }
        else
        {
            for (int i = 0; i < NcwHeader.BlockSampleCount; i++)
            {
                bitWriter.WriteSignedBits(samples[i], effBits);
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

/// <summary>
/// Classe top-level (et non imbriquée dans NcwFile) : c'est ce qui permet à
/// ConversionEngine.cs et aux autres consommateurs de la référencer directement
/// via "using ConNCW.Core.Ncw;" sans la qualifier en "NcwFile.UnknownNcwSignatureException".
/// </summary>
public sealed class UnknownNcwSignatureException : Exception
{
    public byte[] Signature { get; }

    public UnknownNcwSignatureException(byte[] signature)
        : base($"Signature NCW inconnue: {Convert.ToHexString(signature)}")
    {
        Signature = signature;
    }
}
