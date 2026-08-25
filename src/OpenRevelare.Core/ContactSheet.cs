namespace OpenRevelare.Core;

/// <summary>
/// Combine a roll's frames into a single contact-sheet grid — port of Python
/// <c>negative/contactsheet.py::build_contactsheet</c>. Thumbnails are laid out
/// left-to-right, top-to-bottom in a grid whose column count the caller either fixes or leaves
/// to the default ceil(sqrt(n)) (slightly wider than tall), separated by a black gap, with the
/// longer side capped at <paramref name="maxLong"/>.
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
    ///
    /// <paramref name="cols"/> forces a column count; null keeps the historic ceil(sqrt(n)) grid.
    /// A caller that wants the finished SHEET to land on a given proportion cannot pick the
    /// columns from the grid alone — the margins, header and info strip it adds change the
    /// answer — so it plans several column counts and measures them itself.
    /// </summary>
    public static Layout Plan(IReadOnlyList<ImageBuffer> images, int maxLong, int gapX, int gapY,
                              int? cols = null)
    {
        if (images.Count == 0) throw new ArgumentException("images list is empty");
        int n = images.Count;
        int c = cols ?? (int)Math.Ceiling(Math.Sqrt(n));
        c = Math.Clamp(c, 1, n);
        int rows = (int)Math.Ceiling((double)n / c);

        // Median aspect (W/H) → a uniform thumbnail shape.
        var aspects = new double[n];
        for (int i = 0; i < n; i++) aspects[i] = (double)images[i].Width / Math.Max(1, images[i].Height);
        Array.Sort(aspects);
        double medAspect = aspects[n / 2];
        if (medAspect <= 0) medAspect = 1.0;

        // Both axes are solved the same way: take the room the gaps do NOT occupy and divide it
        // by the cell count. Scaling a too-large thumbnail by maxLong/total instead — which is
        // what the height did — leaves the gaps at full size on top of the shrunk cells, and a
        // single-column grid of four frames came out 8% past the cap that way.
        int gx = FitGaps(gapX, c, maxLong), gy = FitGaps(gapY, rows, maxLong);
        int roomW = maxLong - gx * (c - 1), roomH = maxLong - gy * (rows - 1);

        int thumbW = Math.Max(1, roomW / c);
        int thumbH = Math.Max(1, (int)Math.Round(thumbW / medAspect));

        if (rows * thumbH > roomH)
        {
            thumbH = Math.Max(1, roomH / rows);
            thumbW = Math.Max(1, (int)Math.Round(thumbH * medAspect));
        }

        return new Layout
        {
            Cols = c, Rows = rows, ThumbW = thumbW, ThumbH = thumbH,
            GapX = gx, GapY = gy, Count = n,
        };
    }

    /// <summary>
    /// Squeeze a gap that would not leave a single pixel per cell. Only a degenerate grid gets
    /// here — one column of a whole roll is 35 row gaps, which at the printed spacing overflows
    /// a 2048 px sheet on its own — and no such grid would ever be chosen to print. It is solved
    /// anyway because the page-aspect search PLANS every column count before rejecting them, and
    /// a planner that can hand back a layout bigger than the cap it was given is a trap.
    /// </summary>
    private static int FitGaps(int gap, int cells, int maxLong)
    {
        if (cells <= 1 || gap * (cells - 1) <= maxLong - cells) return gap;
        return Math.Max(0, (maxLong - cells) / (cells - 1));
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
