namespace ConNCW.Core.Ncw;

/// <summary>Portage de TBlockHeader (16 octets, en tête de chaque bloc de données).</summary>
public readonly struct NcwBlockHeader
{
    public byte[] Signature { get; init; }
    public int BaseValue { get; init; }
    public short Bits { get; init; }
    public ushort Flags { get; init; }
    public uint Zero { get; init; }

    public bool IsUnencoded => Bits <= 0;
    public int EffectiveBits(int headerBitsPerSample) => Bits == 0 ? headerBitsPerSample : Math.Abs(Bits);
    public bool IsMidSide => Flags == 1;
}
