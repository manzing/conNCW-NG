using ConNCW.Core.Bit;
using Xunit;

namespace ConNCW.Core.Tests;

public class BitTests
{
    [Fact]
    public void BitWriterAndReader_RoundtripUnsigned_MatchesAllWidths()
    {
        for (int width = 1; width <= 32; width++)
        {
            var writer = new BitWriter();
            uint maxValue = width == 32 ? uint.MaxValue : (1u << width) - 1u;
            uint[] testValues = { 0, 1, maxValue / 2, maxValue };

            foreach (var val in testValues)
            {
                writer.WriteBits(val, width);
            }

            var bytes = writer.ToArray();
            var reader = new BitReader(bytes);

            foreach (var expected in testValues)
            {
                uint actual = reader.ReadUnsignedBits(width);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void BitWriterAndReader_RoundtripSigned_MatchesNegativeAndPositiveValues()
    {
        for (int width = 2; width <= 32; width++)
        {
            var writer = new BitWriter();
            int minVal = width == 32 ? int.MinValue : -(1 << (width - 1));
            int maxVal = width == 32 ? int.MaxValue : (1 << (width - 1)) - 1;
            int[] testValues = { 0, 1, -1, minVal, maxVal, minVal / 2, maxVal / 2 };

            foreach (var val in testValues)
            {
                writer.WriteSignedBits(val, width);
            }

            var bytes = writer.ToArray();
            var reader = new BitReader(bytes);

            foreach (var expected in testValues)
            {
                int actual = reader.ReadSignedBits(width);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void BitReader_ThrowsOnOutOfBuffer()
    {
        var bytes = new byte[] { 0x55 };
        var reader = new BitReader(bytes);
        reader.ReadUnsignedBits(8);

        Assert.Throws<InvalidDataException>(() => reader.ReadUnsignedBits(1));
    }
}
