using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRevelare.Core;

internal static class RawPreviewBenchmark
{
    public static void Run()
    {
        foreach (var (width, height, channels, factors) in new[]
        {
            (4000, 3000, 3, new[] { 3, 13 }), (6000, 4000, 3, new[] { 4, 19 }),
            (4000, 3000, 1, new[] { 3, 13 }), (4000, 3000, 4, new[] { 3, 13 }),
        })
        {
            ushort[] samples = new ushort[width * height * channels];
            new Random(42).NextBytes(MemoryMarshal.AsBytes(samples.AsSpan()));
            ImageBuffer[] Independent() => factors.Select(factor => Reference(samples, width, height, channels, factor)).ToArray();
            ImageBuffer[] Shared() => RawPreviewResampler.TryBoxMany(() => samples, width, height, channels, factors, 1d / 65535)
                ?? throw new InvalidOperationException("Benchmark needs compatible box grids.");
            var actual = RawPreviewResampler.TryBoxMany(() => samples, width, height, channels, factors, 1d / 65535);
            if (actual is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Width = width, Height = height, Channels = channels,
                    SharedPath = false, Reason = "Independent path retained for unsupported layouts, SIMD or box grids" }));
                continue;
            }
            var expected = Independent();
            for (int k = 0; k < expected.Length; k++)
                if (!MemoryMarshal.AsBytes(expected[k].Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual[k].Data.AsSpan())))
                    throw new InvalidOperationException("RAW preview parity failed.");
            var separate = new List<double>();
            var shared = new List<double>();
            for (int run = -2; run < 7; run++)
            {
                void Measure(Func<ImageBuffer[]> action, List<double> timings)
                {
                    long start = Stopwatch.GetTimestamp();
                    var frames = action();
                    if (run >= 0) timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    GC.KeepAlive(frames);
                }
                if (run % 2 == 0) { Measure(Independent, separate); Measure(Shared, shared); }
                else { Measure(Shared, shared); Measure(Independent, separate); }
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Width = width, Height = height, Channels = channels, Factors = factors, BitExact = true,
                HardwareAccelerated = System.Numerics.Vector.IsHardwareAccelerated,
                IndependentMs = separate.Order().ElementAt(3), SharedMs = shared.Order().ElementAt(3),
                Scope = "Generated native 16-bit sample conversion and box averaging only; no RAW codec or disk I/O",
            }));
        }
    }

    // Previous production kernel, retained here to compare identical source pixels and sums.
    private static ImageBuffer Reference(ushort[] source, int width, int height, int channels, int factor)
    {
        const float scale = 1f / 65535f;
        var output = new ImageBuffer(width / factor, height / factor);
        float inv = 1f / (factor * factor);
        Parallel.For(0, output.Height, oy =>
        {
            ReadOnlySpan<ushort> data = source;
            for (int ox = 0; ox < output.Width; ox++)
            {
                float r = 0, g = 0, b = 0;
                for (int fy = 0; fy < factor; fy++)
                {
                    int row = ((oy * factor + fy) * width + ox * factor) * channels;
                    for (int fx = 0; fx < factor; fx++)
                    {
                        int i = row + fx * channels;
                        if (channels == 1) { float v = data[i] * scale; r += v; g += v; b += v; }
                        else { r += data[i] * scale; g += data[i + 1] * scale; b += data[i + 2] * scale; }
                    }
                }
                int di = (oy * output.Width + ox) * 3;
                output.Data[di] = r * inv; output.Data[di + 1] = g * inv; output.Data[di + 2] = b * inv;
            }
        });
        return output;
    }
}
