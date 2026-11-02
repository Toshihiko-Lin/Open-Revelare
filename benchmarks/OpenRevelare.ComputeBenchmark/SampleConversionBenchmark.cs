using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using OpenRevelare.Core;

internal static class SampleConversionBenchmark
{
    // Run Release with --sample-conversion. Generated samples only; timings exclude allocation
    // and codec/disk work, so they measure the conversion stage rather than total file I/O.
    public static void Run()
    {
        const int samples = 6000 * 4000 * 3;
        ushort[] codes = new ushort[samples];
        float[] floats = new float[samples];
        ushort[] packed = new ushort[samples];
        for (int i = 0; i < samples; i++) codes[i] = (ushort)i;
        const float scale = 1f / 65535f;
        void DecodeScalar()
        {
            for (int i = 0; i < codes.Length; i++) floats[i] = codes[i] * scale;
        }
        void EncodeScalar()
        {
            for (int i = 0; i < floats.Length; i++)
            {
                float v = floats[i];
                float c = v < 0f ? 0f : v > 1f ? 1f : v;
                packed[i] = (ushort)(c * 65535f + 0.5f);
            }
        }
        static double Median(Action action)
        {
            // Warm tiered compilation and the buffers before collecting timed runs.
            action();
            double[] timings = new double[7];
            for (int i = 0; i < timings.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(timings);
            return timings[timings.Length / 2];
        }
        double decodeScalar = Median(DecodeScalar);
        double decodeVector = Median(() => SampleConversion.ToFloat(codes, floats, scale));
        for (int i = 0; i < samples; i++)
            if (BitConverter.SingleToInt32Bits(floats[i]) != BitConverter.SingleToInt32Bits(codes[i] * scale))
                throw new InvalidOperationException("Decode parity failed.");
        double encodeScalar = Median(EncodeScalar);
        double encodeVector = Median(() => SampleConversion.ToUInt16(floats, packed));
        if (!codes.AsSpan().SequenceEqual(packed)) throw new InvalidOperationException("Encode parity failed.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Samples = samples, VectorBits = Vector<byte>.Count * 8,
            DecodeScalarMs = decodeScalar, DecodeVectorMs = decodeVector,
            EncodeScalarMs = encodeScalar, EncodeVectorMs = encodeVector, BitExact = true,
        }));
    }
}
