using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>Display-only coverage. The saved mask and repair footprint stay on the source grid.</summary>
internal static class DustMaskDisplay
{
    public static float[] Coverage(DustMask mask, int width, int height)
    {
        var coverage = new float[checked(width * height)];
        double sx = mask.Bounds.W * width / mask.Width, sy = mask.Bounds.H * height / mask.Height;
        double ox = mask.Bounds.X * width, oy = mask.Bounds.Y * height;
        foreach (var run in mask.EnumerateRuns())
        {
            double left = ox + run.Start * sx, right = ox + run.End * sx;
            double top = oy + run.Y * sy, bottom = oy + (run.Y + 1) * sy;
            int x0 = (int)Math.Clamp(Math.Floor(left), 0, width);
            int x1 = (int)Math.Clamp(Math.Ceiling(right), 0, width);
            int y0 = (int)Math.Clamp(Math.Floor(top), 0, height);
            int y1 = (int)Math.Clamp(Math.Ceiling(bottom), 0, height);
            for (int y = y0; y < y1; y++)
            {
                double vertical = Math.Max(0, Math.Min(y + 1, bottom) - Math.Max(y, top));
                for (int x = x0; x < x1; x++)
                {
                    double horizontal = Math.Max(0, Math.Min(x + 1, right) - Math.Max(x, left));
                    int index = y * width + x;
                    coverage[index] = Math.Min(1, coverage[index] + (float)(horizontal * vertical));
                }
            }
        }
        return coverage;
    }

    public static void WritePixel(byte[] rgba, int pixel, double coverage)
    {
        if (coverage <= 0) return;
        int offset = pixel * 4;
        rgba[offset] = 255;
        rgba[offset + 1] = 154;
        rgba[offset + 2] = 45;
        // Keep subpixel dust legible without expanding its outline or hiding photo detail.
        rgba[offset + 3] = (byte)Math.Clamp(Math.Round(112 * Math.Sqrt(coverage)), 0, 112);
    }
}
