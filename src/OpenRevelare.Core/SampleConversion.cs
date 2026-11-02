using System.Numerics;

namespace OpenRevelare.Core;

/// <summary>Native-order integer sample conversion, shared by RAW and TIFF I/O.</summary>
internal static class SampleConversion
{
    public static void ToFloat(ReadOnlySpan<ushort> source, Span<float> destination, float scale)
    {
        destination = destination[..source.Length];
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var multiplier = new Vector<float>(scale);
            int lanes = Vector<ushort>.Count;
            for (; i <= source.Length - lanes; i += lanes)
            {
                Vector.Widen(new Vector<ushort>(source.Slice(i, lanes)), out var low, out var high);
                (Vector.ConvertToSingle(low) * multiplier).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(high) * multiplier).CopyTo(destination[(i + Vector<float>.Count)..]);
            }
        }
        for (; i < source.Length; i++) destination[i] = source[i] * scale;
    }

    public static void ToUInt16(ReadOnlySpan<float> source, Span<ushort> destination)
    {
        destination = destination[..source.Length];
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var maximum = new Vector<float>(65535f);
            var half = new Vector<float>(0.5f);
            int lanes = Vector<float>.Count;
            for (; i <= source.Length - lanes * 2; i += lanes * 2)
            {
                var low = Quantize(new Vector<float>(source.Slice(i, lanes)));
                var high = Quantize(new Vector<float>(source.Slice(i + lanes, lanes)));
                Vector.Narrow(low, high).CopyTo(destination[i..]);
            }

            Vector<uint> Quantize(Vector<float> value)
            {
                // Comparisons also map NaN to zero, consistently with the scalar path.
                value = Vector.ConditionalSelect(Vector.GreaterThan(value, Vector<float>.Zero), value, Vector<float>.Zero);
                value = Vector.Min(value, Vector<float>.One);
                return Vector.ConvertToUInt32(value * maximum + half);
            }
        }
        for (; i < source.Length; i++)
        {
            float value = source[i];
            float clamped = value > 0f ? Math.Min(value, 1f) : 0f;
            destination[i] = (ushort)(clamped * 65535f + 0.5f);
        }
    }
}
