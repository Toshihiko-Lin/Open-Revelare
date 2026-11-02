using System.Numerics;
using System.Buffers;

namespace OpenRevelare.Core;

/// <summary>Shares native sample conversion across several RAW preview sizes.</summary>
internal static class RawPreviewResampler
{
    internal delegate ReadOnlySpan<ushort> SampleSource();

    // A null result asks the caller to retain independent, output-row-parallel boxing:
    // incompatible box grids must not turn a large frame into a serial workload.
    public static ImageBuffer[]? TryBoxMany(SampleSource source, int width, int height, int channels,
        int[] factors, double sourceQuantisationStep)
    {
        // Only RGB16 benefits from the SIMD conversion shared here. Monochrome, four-channel
        // buffers and machines without SIMD retain the established independent kernel.
        if (channels != 3 || !Vector.IsHardwareAccelerated || factors.Length < 2) return null;
        int unit = 1;
        foreach (int factor in factors)
        {
            int a = unit, b = factor;
            while (b != 0) (a, b) = (b, a % b);
            long next = (long)unit / a * factor;
            if (next > height) return null;
            unit = (int)next;
        }
        int cores = Environment.ProcessorCount;
        bool small = (long)width * height < 65536;
        if (!small && height / unit < Math.Min(cores, 4)) return null;

        var outputs = new ImageBuffer[factors.Length];
        var inverse = new float[factors.Length];
        int lastRow = 0;
        for (int k = 0; k < factors.Length; k++)
        {
            int factor = factors[k];
            outputs[k] = new ImageBuffer(width / factor, height / factor)
            {
                SourceQuantisationStep = sourceQuantisationStep,
            };
            inverse[k] = 1f / (factor * factor);
            lastRow = Math.Max(lastRow, outputs[k].Height * factor);
        }
        int bandRows = small ? lastRow : Math.Max(unit, ((lastRow + cores - 1) / cores + unit - 1) / unit * unit);
        int bands = (lastRow + bandRows - 1) / bandRows;
        if (bands == 1) ReadBand(0);
        else Parallel.For(0, bands, ReadBand);
        return outputs;

        void ReadBand(int band)
        {
            const float scale = 1f / 65535f;
            // Keep converted samples and accumulators in the worker's cache. Tile boundaries
            // share every box grid, just like vertical band boundaries.
            int tileWidth = Math.Min(width, Math.Max(unit, 768 / unit * unit));
            float[] rented = ArrayPool<float>.Shared.Rent(checked(tileWidth * 3));
            try
            {
                ReadOnlySpan<ushort> samples = source();
                int end = Math.Min(lastRow, (band + 1) * bandRows);
                for (int y = band * bandRows; y < end; y++)
                for (int x0 = 0; x0 < width; x0 += tileWidth)
                {
                    int pixels = Math.Min(tileWidth, width - x0);
                    Span<float> row = rented.AsSpan(0, pixels * 3);
                    ReadOnlySpan<ushort> input = samples.Slice((y * width + x0) * channels, pixels * channels);
                    SampleConversion.ToFloat(input, row, scale);
                    for (int k = 0; k < factors.Length; k++)
                    {
                        int factor = factors[k], oy = y / factor;
                        ImageBuffer output = outputs[k];
                        if (oy >= output.Height) continue;
                        int firstOx = x0 / factor;
                        int count = Math.Min(pixels / factor, output.Width - firstOx);
                        Span<float> destination = output.Data.AsSpan((oy * output.Width + firstOx) * 3, count * 3);
                        if (factor == 1) { row.CopyTo(destination); continue; }
                        for (int ox = 0; ox < count; ox++)
                        {
                            int di = ox * 3, si = ox * factor * 3;
                            float r = destination[di], g = destination[di + 1], b = destination[di + 2];
                            for (int fx = 0; fx < factor; fx++, si += 3)
                            {
                                r += row[si]; g += row[si + 1]; b += row[si + 2];
                            }
                            destination[di] = r;
                            destination[di + 1] = g;
                            destination[di + 2] = b;
                        }
                        if ((y + 1) % factor == 0) Scale(destination, inverse[k]);
                    }
                }
            }
            finally { ArrayPool<float>.Shared.Return(rented); }
        }
    }

    private static void Scale(Span<float> row, float scale)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var multiplier = new Vector<float>(scale);
            int lanes = Vector<float>.Count;
            for (; i <= row.Length - lanes; i += lanes)
                (new Vector<float>(row.Slice(i, lanes)) * multiplier).CopyTo(row[i..]);
        }
        for (; i < row.Length; i++) row[i] *= scale;
    }
}
