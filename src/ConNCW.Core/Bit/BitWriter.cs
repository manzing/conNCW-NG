using System.Runtime.CompilerServices;

namespace ConNCW.Core.Bit;

/// <summary>
/// Écrivain de bits, contrepartie de BitReader pour l'encodage WAV→NCW.
/// Optimisé avec un accumulateur 64 bits (LSB-first) et un buffer contigu pour éliminer les allocations répétées.
/// </summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private int _byteCount;
    private ulong _bitBuffer;
    private int _bitsInBuffer;

    public BitWriter(int initialCapacity = 2048)
    {
        _buffer = new byte[initialCapacity];
        _byteCount = 0;
        _bitBuffer = 0;
        _bitsInBuffer = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(uint value, int bitCount)
    {
        if (bitCount is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }
        if (bitCount == 0)
        {
            return;
        }

        uint mask = (bitCount == 32) ? uint.MaxValue : (1u << bitCount) - 1u;
        _bitBuffer |= ((ulong)(value & mask)) << _bitsInBuffer;
        _bitsInBuffer += bitCount;

        while (_bitsInBuffer >= 8)
        {
            EnsureCapacity(_byteCount + 1);
            _buffer[_byteCount++] = (byte)(_bitBuffer & 0xFF);
            _bitBuffer >>= 8;
            _bitsInBuffer -= 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSignedBits(int value, int bitCount) => WriteBits(unchecked((uint)value), bitCount);

    public void Flush()
    {
        if (_bitsInBuffer > 0)
        {
            EnsureCapacity(_byteCount + 1);
            _buffer[_byteCount++] = (byte)(_bitBuffer & 0xFF);
            _bitBuffer = 0;
            _bitsInBuffer = 0;
        }
    }

    public byte[] ToArray()
    {
        Flush();
        var result = new byte[_byteCount];
        Buffer.BlockCopy(_buffer, 0, result, 0, _byteCount);
        return result;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        }
    }
}
