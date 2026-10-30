namespace ConNCW.Core.Models;

/// <summary>
/// En-tête NCW complet (120 octets), conforme à ncwformat.txt et au TNCWHeader
/// packed record du NCWParser3.pas d'origine.
/// </summary>
public sealed class NcwHeader
{
    public const int HeaderSize = 120;
    public const int BlockSampleCount = 512;
    public const int BlockHeaderSize = 16;
    public static readonly byte[] BlockSignature = { 0x16, 0x0C, 0x9A, 0x3E };

    public byte[] RawSignature { get; init; } = Array.Empty<byte>();
    public ushort Channels { get; init; }
    public ushort Bits { get; init; }
    public uint SampleRate { get; init; }
    public uint NumSamples { get; init; }
    public uint TableOffset { get; init; }   // block_def_offset, doit valoir 120 (0x78)
    public uint DataOffset { get; init; }    // blocks_offset
    public uint DataSize { get; init; }      // blocks_size
    public uint SampleFormatWord { get; init; } // mot de format à l'offset 32 (0x20)
    public byte[] SomeData { get; init; } = new byte[84]; // 84 octets opaques (offsets 36..119)

    public uint[] BlockOffsets { get; set; } = Array.Empty<uint>();

    /// <summary>Signature reconnue en base (label), ou null si inconnue / bypass.</summary>
    public string? MatchedSignatureLabel { get; set; }
    public bool SignatureRecognized => MatchedSignatureLabel is not null;

    public Models.AudioFormat ToAudioFormat() => new()
    {
        Channels = Channels,
        BitsPerSample = Bits,
        SampleRate = SampleRate,
        SampleCount = NumSamples
    };

    public int BlockCount
    {
        get
        {
            if (NumSamples == 0) return 0;
            return (int)((NumSamples + BlockSampleCount - 1) / BlockSampleCount);
        }
    }
}
