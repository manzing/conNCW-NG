namespace ConNCW.Core.Bit;

/// <summary>
/// Lecteur de bits sur un buffer, remplace les manipulations de pointeurs bruts
/// du BitProcess3.pas original par une API sûre basée sur Span/Memory.
/// </summary>
public sealed class BitReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _bitPosition;

    public BitReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _bitPosition = 0;
    }

    public int BitsRemaining => (_buffer.Length * 8) - _bitPosition;

    /// <summary>Lit un entier signé de n bits (delta-decoding NCW), n dans [1..32].</summary>
    public int ReadSignedBits(int bitCount)
    {
        if (bitCount is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        uint raw = ReadUnsignedBits(bitCount);
        bool isNegative = (raw & (1u << (bitCount - 1))) != 0;
        if (!isNegative)
        {
            return (int)raw;
        }

        uint mask = (bitCount == 32) ? 0u : uint.MaxValue << bitCount;
        return (int)(raw | mask);
    }

    public uint ReadUnsignedBits(int bitCount)
    {
        if (BitsRemaining < bitCount)
        {
            throw new InvalidOperationException("Fin de buffer atteinte pendant le décodage des bits.");
        }

        uint result = 0;
        var span = _buffer.Span;

        for (int i = 0; i < bitCount; i++)
        {
            int byteIndex = _bitPosition / 8;
            int bitIndex = _bitPosition % 8;
            int bit = (span[byteIndex] >> bitIndex) & 1;
            result |= (uint)(bit << i);
            _bitPosition++;
        }

        return result;
    }
}
