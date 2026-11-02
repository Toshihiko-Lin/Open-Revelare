using System.Numerics;

namespace OpenRevelare.Core;

internal static class TiffPredictor
{
    /// <summary>TIFF RGB horizontal prediction, modulo 65536, independently for each row.</summary>
    public static void ApplyHorizontal(Span<ushort> samples, int rowSamples)
    {
        for (int start = 0; start < samples.Length; start += rowSamples)
        {
            Span<ushort> row = samples.Slice(start, rowSamples);
            int i = row.Length;
            // Work backwards: both vectors must read original values before any overlapping
            // predecessor samples have been replaced by their differences.
            if (Vector.IsHardwareAccelerated)
            {
                int lanes = Vector<ushort>.Count;
                for (; i >= lanes + 3;)
                {
                    i -= lanes;
                    var current = new Vector<ushort>(row.Slice(i, lanes));
                    var previous = new Vector<ushort>(row.Slice(i - 3, lanes));
                    (current - previous).CopyTo(row[i..]);
                }
            }
            for (int s = i - 1; s >= 3; s--)
                row[s] = unchecked((ushort)(row[s] - row[s - 3]));
        }
    }
}
