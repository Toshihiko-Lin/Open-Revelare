using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class SampleConversionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Every_integer_code_decodes_bit_exactly_with_unaligned_slices(int offset)
    {
        ushort[] source = new ushort[65536 + offset];
        for (int i = 0; i < 65536; i++) source[i + offset] = (ushort)i;
        float[] result = new float[65536 + offset + 1];
        Array.Fill(result, -1f);
        const float scale = 1f / 65535f;
        SampleConversion.ToFloat(source.AsSpan(offset), result.AsSpan(offset, 65536), scale);
        for (int i = 0; i < 65536; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(i * scale), BitConverter.SingleToInt32Bits(result[i + offset]));
        Assert.Equal(-1f, result[^1]);
        if (offset > 0) Assert.Equal(-1f, result[0]);
    }

    [Fact]
    public void Quantization_matches_scalar_at_every_half_code_boundary_and_adjacent_floats()
    {
        var values = new List<float> { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -1f, -0f, 0f, 1f, 2f };
        for (int i = 0; i < 65535; i++)
        {
            float boundary = (i + 0.5f) / 65535f;
            values.Add(MathF.BitDecrement(boundary));
            values.Add(boundary);
            values.Add(MathF.BitIncrement(boundary));
        }
        float[] source = values.ToArray();
        ushort[] result = new ushort[source.Length];
        SampleConversion.ToUInt16(source, result);
        for (int i = 0; i < source.Length; i++)
        {
            float c = source[i] < 0f ? 0f : source[i] > 1f ? 1f : source[i];
            Assert.Equal((ushort)(c * 65535f + 0.5f), result[i]);
        }
    }

    [Fact]
    public void Small_buffers_and_vector_tails_roundtrip_all_codes()
    {
        for (int length = 0; length < 100; length++)
        {
            ushort[] codes = Enumerable.Range(0, length).Select(i => (ushort)(i * 661)).ToArray();
            float[] decoded = new float[length];
            ushort[] encoded = new ushort[length + 2];
            encoded[0] = encoded[^1] = 12345;
            SampleConversion.ToFloat(codes, decoded, 1f / 65535f);
            SampleConversion.ToUInt16(decoded, encoded.AsSpan(1, length));
            Assert.Equal(codes, encoded[1..^1]);
            Assert.Equal((ushort)12345, encoded[0]);
            Assert.Equal((ushort)12345, encoded[^1]);
        }
    }
}
