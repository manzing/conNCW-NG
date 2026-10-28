using System.Runtime.CompilerServices;

namespace ConNCW.Core.Bit;

/// <summary>
/// Lecteur de bits sur un buffer, remplace les manipulations de pointeurs bruts
/// du BitProcess3.pas original par une API sûre basée sur Span/Memory.
/// Optimisé avec un accumulateur 64 bits (LSB-first) pour des performances maximales.
/// </summary>
public sealed class BitReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private ulong _bitBuffer;
    private int _bitsInBuffer;
    private int _byteOffset;

    public BitReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _bitBuffer = 0;
        _bitsInBuffer = 0;
        _byteOffset = 0;
    }

    public int BitsRemaining => ((_buffer.Length - _byteOffset) * 8) + _bitsInBuffer;

    /// <summary>Lit un entier signé de n bits (delta-decoding NCW), n dans [1..32].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSignedBits(int bitCount)
    {
        if (bitCount is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        uint raw = ReadUnsignedBits(bitCount);
        if (bitCount < 32 && (raw & (1u << (bitCount - 1))) != 0)
        {
            uint mask = uint.MaxValue << bitCount;
            return (int)(raw | mask);
        }

        return (int)raw;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUnsignedBits(int bitCount)
    {
        if (bitCount is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }
        if (bitCount == 0)
        {
            return 0;
        }

        if (BitsRemaining < bitCount)
        {
            throw new InvalidDataException("Données corrompues : fin de buffer atteinte pendant le décodage des bits.");
        }

        var span = _buffer.Span;
        while (_bitsInBuffer < bitCount && _byteOffset < span.Length)
        {
            _bitBuffer |= ((ulong)span[_byteOffset++]) << _bitsInBuffer;
            _bitsInBuffer += 8;
        }

        uint mask = (bitCount == 32) ? uint.MaxValue : (1u << bitCount) - 1u;
        uint result = (uint)(_bitBuffer & mask);
        _bitBuffer >>= bitCount;
        _bitsInBuffer -= bitCount;

        return result;
    }
}
