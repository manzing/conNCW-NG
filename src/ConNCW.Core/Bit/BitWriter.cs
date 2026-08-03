namespace ConNCW.Core.Bit;

/// <summary>Écrivain de bits, contrepartie de BitReader pour l'encodage WAV→NCW.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _buffer = new();
    private int _bitPosition;

    public byte[] ToArray() => _buffer.ToArray();

    public void WriteBits(uint value, int bitCount)
    {
        for (int i = 0; i < bitCount; i++)
        {
            int byteIndex = _bitPosition / 8;
            int bitIndex = _bitPosition % 8;

            if (byteIndex >= _buffer.Count)
            {
                _buffer.Add(0);
            }

            int bit = (int)((value >> i) & 1);
            _buffer[byteIndex] |= (byte)(bit << bitIndex);
            _bitPosition++;
        }
    }

    public void WriteSignedBits(int value, int bitCount) => WriteBits(unchecked((uint)value), bitCount);
}
