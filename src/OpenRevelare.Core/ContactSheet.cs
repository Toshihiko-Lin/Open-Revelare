using System;
using System.Collections.Generic;

namespace OpenRevelare.Core;

/// <summary>
/// Combine a roll's frames into a single contact-sheet grid — port of Python
/// <c>negative/contactsheet.py::build_contactsheet</c>. Thumbnails are laid out
/// left-to-right, top-to-bottom in a grid slightly wider than tall (cols ≥ rows),
/// separated by a black gap, with the longer side capped at <paramref name="maxLong"/>.
/// Inputs are already-processed sRGB positives in [0,1]; the output is one image.
/// </summary>
public static class ContactSheet
{
    /// <summary>
    /// Where every cell of the grid lands. Split out from <see cref="Build"/> because the
    /// presentation layer has to draw on top of the grid — frame numbers under each cell,
    /// keylines around them — and cannot re-derive the geometry without duplicating the
    /// aspect/fit maths that decides it.
    /// </summary>
    public sealed class Layout
    {
        public required int Cols { get; init; }
        public required int Rows { get; init; }
        public required int ThumbW { get; init; }
        public required int ThumbH { get; init; }
        public required int GapX { get; init; }
        public required int GapY { get; init; }
        public required int Count { get; init; }

        public int Width => Cols * ThumbW + GapX * (Cols - 1);
        public int Height => Rows * ThumbH + GapY * (Rows - 1);

        /// <summary>Top-left of cell <paramref name="index"/> within the grid.</summary>
        public (int X, int Y) Origin(int index) =>
            (index % Cols * (ThumbW + GapX), index / Cols * (ThumbH + GapY));
    }

    /// <summary>
    /// Decide the grid without drawing it. <paramref name="gapY"/> is separate from
    /// <paramref name="gapX"/> so a caller that prints frame numbers between rows can buy the
    /// room for them here rather than stretching the sheet afterwards.
    /// </summary>
    public static Layout Plan(IReadOnlyList<ImageBuffer> images, int maxLong, int gapX, int gapY)
    {
        if (images.Count == 0) throw new ArgumentException("images list is empty");
        int n = images.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);

        // Median aspect (W/H) → a uniform thumbnail shape.
        var aspects = new double[n];
        for (int i = 0; i < n; i++) aspects[i] = (double)images[i].Width / Math.Max(1, images[i].Height);
        Array.Sort(aspects);
        double medAspect = aspects[n / 2];
        if (medAspect <= 0) medAspect = 1.0;

        int thumbW = Math.Max(1, (maxLong - gapX * (cols - 1)) / cols);
        int thumbH = Math.Max(1, (int)Math.Round(thumbW / medAspect));

        int totalH = rows * thumbH + gapY * (rows - 1);
        if (totalH > maxLong)
        {
            double scale = (double)maxLong / totalH;
            thumbH = Math.Max(1, (int)(thumbH * scale));
            thumbW = Math.Max(1, (int)Math.Round(thumbH * medAspect));
        }

        return new Layout
        {
            Cols = cols, Rows = rows, ThumbW = thumbW, ThumbH = thumbH,
            GapX = gapX, GapY = gapY, Count = n,
        };
    }

    /// <summary>Draw the thumbnails into a canvas whose gaps are filled with
    /// <paramref name="gapRgb"/> (sRGB [0,1]; black is the film rebate a real contact print
    /// shows between frames).</summary>
    public static ImageBuffer Build(IReadOnlyList<ImageBuffer> images, Layout layout,
                                    float[]? gapRgb = null)
    {
        var canvas = new ImageBuffer(layout.Width, layout.Height);
        if (gapRgb is { Length: 3 } && (gapRgb[0] != 0 || gapRgb[1] != 0 || gapRgb[2] != 0))
        {
            float[] d = canvas.Data;
            for (int i = 0; i < d.Length; i += 3)
            {
                d[i] = gapRgb[0]; d[i + 1] = gapRgb[1]; d[i + 2] = gapRgb[2];
            }
        }

        for (int idx = 0; idx < layout.Count; idx++)
        {
            (int x0, int y0) = layout.Origin(idx);
            BoxResizeInto(images[idx], canvas, x0, y0, layout.ThumbW, layout.ThumbH);
        }
        return canvas;
    }

    /// <summary>Plan and draw in one call — the plain grid, no surround.</summary>
    public static ImageBuffer Build(IReadOnlyList<ImageBuffer> images, int maxLong = 2048, int gap = 4)
        => Build(images, Plan(images, maxLong, gap, gap));

    /// <summary>Area-average (box) downscale of <paramref name="src"/> into a canvas rect.</summary>
    private static void BoxResizeInto(ImageBuffer src, ImageBuffer dst, int dx, int dy, int outW, int outH)
    {
        float[] s = src.Data, d = dst.Data;
        int sw = src.Width, sh = src.Height, dw = dst.Width;
        for (int oy = 0; oy < outH; oy++)
        {
            int sy0 = (int)((long)oy * sh / outH);
            int sy1 = Math.Max(sy0 + 1, (int)((long)(oy + 1) * sh / outH));
            for (int ox = 0; ox < outW; ox++)
            {
                int sx0 = (int)((long)ox * sw / outW);
                int sx1 = Math.Max(sx0 + 1, (int)((long)(ox + 1) * sw / outW));
                float r = 0, g = 0, b = 0; int cnt = 0;
                for (int yy = sy0; yy < sy1; yy++)
                {
                    int rowBase = yy * sw * 3;
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int i = rowBase + xx * 3;
                        r += s[i]; g += s[i + 1]; b += s[i + 2]; cnt++;
                    }
                }
                float inv = cnt > 0 ? 1f / cnt : 0f;
                int o = ((dy + oy) * dw + (dx + ox)) * 3;
                d[o] = r * inv; d[o + 1] = g * inv; d[o + 2] = b * inv;
            }
        }
    }
}
