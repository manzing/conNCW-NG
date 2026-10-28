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
    public int EffectiveBits(int headerBitsPerSample)
    {
        if (Bits == 0)
        {
            return headerBitsPerSample;
        }
        int abs = Math.Abs((int)Bits);
        return Math.Clamp(abs, 1, 32);
    }
    public bool IsMidSide => (Flags & 0x01) != 0;
    public bool IsFloat => (Flags & 0x02) != 0;
}
