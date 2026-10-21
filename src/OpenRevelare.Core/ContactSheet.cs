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
        public readonly record struct Cell(int X, int Y, int Width, int Height);

        /// <summary>Per-frame rectangles for a roll with mixed aspect ratios.</summary>
        public IReadOnlyList<Cell>? Cells { get; init; }
        public required int Cols { get; init; }
        public required int Rows { get; init; }
        public required int ThumbW { get; init; }
        public required int ThumbH { get; init; }
        public required int GapX { get; init; }
        public required int GapY { get; init; }
        public required int Count { get; init; }

        public int Width => Cells is { } cells ? cells.Max(c => c.X + c.Width)
                                               : Cols * ThumbW + GapX * (Cols - 1);
        public int Height => Cells is { } cells ? cells.Max(c => c.Y + c.Height)
                                                : Rows * ThumbH + GapY * (Rows - 1);

        public Cell Frame(int index)
        {
            if (Cells is { } cells) return cells[index];
            return new Cell(index % Cols * (ThumbW + GapX), index / Cols * (ThumbH + GapY),
                            ThumbW, ThumbH);
        }

        public int RowBottom(int row)
        {
            Cell first = Frame(row * Cols);
            return first.Y + first.Height;
        }

        /// <summary>Top-left of cell <paramref name="index"/> within the grid. Rows always begin
        /// at the left edge, as they do on a physical contact print.</summary>
        public (int X, int Y) Origin(int index)
        {
            Cell frame = Frame(index);
            return (frame.X, frame.Y);
        }
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

        // Uniform rolls retain their traditional grid. Mixed rolls need actual frame rectangles:
        // a median aspect ratio cannot describe both a portrait and a landscape photograph.
        if (images.Any(image => (long)image.Width * images[0].Height !=
                                (long)images[0].Width * image.Height))
            return PlanMixed(images, maxLong, gapX, gapY, c, rows);

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

    private static Layout PlanMixed(IReadOnlyList<ImageBuffer> images, int maxLong,
                                    int gapX, int gapY, int cols, int rows)
    {
        int gx = FitGaps(gapX, cols, maxLong), gy = FitGaps(gapY, rows, maxLong);
        // Give frames the same long edge, pack each row in source order, then align its bottoms.
        // A single-column sheet uses a common width so every row also shares both edges.
        // A short last row stays at the left; rows use only the height their pictures need.
        Layout At(double edge)
        {
            var cells = new Layout.Cell[images.Count];
            int top = 0, maxW = 0, maxH = 0;
            for (int first = 0; first < images.Count; first += cols)
            {
                int end = Math.Min(first + cols, images.Count), x = 0, rowH = 0;
                for (int i = first; i < end; i++)
                {
                    ImageBuffer image = images[i];
                    double scale = edge / (cols == 1 ? image.Width : Math.Max(image.Width, image.Height));
                    int w = Math.Max(1, (int)Math.Floor(image.Width * scale));
                    int h = Math.Max(1, (int)Math.Floor(image.Height * scale));
                    cells[i] = new Layout.Cell(x, top, w, h);
                    x += w + gx;
                    rowH = Math.Max(rowH, h);
                    maxW = Math.Max(maxW, w);
                    maxH = Math.Max(maxH, h);
                }
                for (int i = first; i < end; i++)
                    cells[i] = cells[i] with { Y = top + rowH - cells[i].Height };
                top += rowH + gy;
            }
            return new Layout
            {
                Cols = cols, Rows = rows, ThumbW = maxW, ThumbH = maxH,
                GapX = gx, GapY = gy, Count = images.Count, Cells = cells,
            };
        }

        // Solve using rounded rectangles too, so neither accumulated row sizes nor gaps can
        // push the raster beyond its budget at preview or export resolution.
        double lo = 0, hi = maxLong + 1.0;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            Layout candidate = At(mid);
            if (candidate.Width <= maxLong && candidate.Height <= maxLong) lo = mid;
            else hi = mid;
        }
        Layout result = At(lo);
        if (cols > 1)
        {
            var cells = (Layout.Cell[])result.Cells!;
            int width = result.Width;
            // Justify full rows by spreading spare width across their gaps. The accumulated
            // integer offset keeps both edges exact and gap differences within one pixel.
            // Leave incomplete rows at the design gap rather than stretching a few frames apart.
            for (int first = 0; first + cols <= images.Count; first += cols)
            {
                Layout.Cell last = cells[first + cols - 1];
                int spare = width - (last.X + last.Width);
                for (int col = 1; col < cols; col++)
                {
                    int i = first + col;
                    cells[i] = cells[i] with { X = cells[i].X + (int)((long)spare * col / (cols - 1)) };
                }
            }
        }
        return result;
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

        PasteCells(canvas, images, layout, 0, 0);
        return canvas;
    }

    /// <summary>
    /// Draw every cell of <paramref name="layout"/> into <paramref name="canvas"/>, the grid's
    /// top-left at (<paramref name="gridX"/>, <paramref name="gridY"/>). Only the cell rectangles
    /// are touched — gaps, empty cells and whatever surrounds the grid stay as they were — so a
    /// caller can lay a second set of thumbnails over a sheet that was composed from a first.
    /// Numbers are copied as they are: the canvas and the images must agree on what they encode.
    /// </summary>
    public static void PasteCells(ImageBuffer canvas, IReadOnlyList<ImageBuffer> images,
                                  Layout layout, int gridX, int gridY)
    {
        if (images.Count < layout.Count)
            throw new ArgumentException("fewer images than the layout has cells", nameof(images));
        if (gridX < 0 || gridY < 0 ||
            gridX + layout.Width > canvas.Width || gridY + layout.Height > canvas.Height)
            throw new ArgumentException("the grid does not fit inside the canvas at that origin");
        for (int idx = 0; idx < layout.Count; idx++)
        {
            Layout.Cell cell = layout.Frame(idx);
            if (layout.Cells is not null)
            {
                BoxResizeInto(images[idx], canvas, gridX + cell.X, gridY + cell.Y,
                              cell.Width, cell.Height);
                continue;
            }
            // Contain also protects callers supplying a manually constructed uniform layout.
            double scale = Math.Min((double)cell.Width / images[idx].Width,
                                    (double)cell.Height / images[idx].Height);
            int w = Math.Clamp((int)Math.Round(images[idx].Width * scale), 1, cell.Width);
            int h = Math.Clamp((int)Math.Round(images[idx].Height * scale), 1, cell.Height);
            BoxResizeInto(images[idx], canvas, gridX + cell.X + (cell.Width - w) / 2,
                          gridY + cell.Y + cell.Height - h, w, h);
        }
    }

    /// <summary>
    /// The extended (HDR) rendition of a composed sheet (D-031): the finished SDR sheet —
    /// paper, header, keylines, frame numbers and its SDR cells — taken back to linear, with the
    /// cells overwritten by <paramref name="extendedThumbs"/>, which are linear in the canonical
    /// extended carrier and may exceed 1.0.
    ///
    /// <para>
    /// The surround is paper, not picture: nothing above diffuse white was ever measured there,
    /// so it is pinned at the carrier's 1.0 and below, exactly as the sprocket fill is in the
    /// frame render — on an HDR display the paper reads as it does in SDR, and a gain map built
    /// from the pair is empty everywhere but inside the frames. The cells are pasted at the same
    /// geometry the SDR composer drew them at, so the two renditions are one sheet at two
    /// headrooms, which is what a gain-map reader requires of them.
    /// </para>
    /// </summary>
    /// <param name="sdrSheet">The composed sheet, encoded in <paramref name="sheetSpace"/>, [0,1].</param>
    /// <param name="sheetSpace">The display space <paramref name="sdrSheet"/> is encoded in. Its
    /// primaries must be the carrier's (sRGB): the surround is only linearised, never rotated.</param>
    /// <param name="extendedThumbs">One extended-carrier thumbnail per cell, same order as the SDR set.</param>
    /// <param name="layout">The grid the SDR sheet was built from.</param>
    /// <param name="gridX">Where the composer placed the grid's top-left on the page.</param>
    /// <param name="gridY">Likewise, vertically.</param>
    public static ImageBuffer WithExtendedCells(ImageBuffer sdrSheet, ColorSpaceDef sheetSpace,
                                                IReadOnlyList<ImageBuffer> extendedThumbs,
                                                Layout layout, int gridX, int gridY)
    {
        ArgumentNullException.ThrowIfNull(sdrSheet);
        ArgumentNullException.ThrowIfNull(sheetSpace);
        ArgumentNullException.ThrowIfNull(extendedThumbs);
        ArgumentNullException.ThrowIfNull(layout);
        ColorSpaceDef carrier = ColorSpaces.Srgb;
        if (sheetSpace.Red != carrier.Red || sheetSpace.Green != carrier.Green ||
            sheetSpace.Blue != carrier.Blue || sheetSpace.White != carrier.White)
        {
            throw new ArgumentException(
                $"the sheet is encoded in {sheetSpace.Name}, whose primaries are not the extended carrier's",
                nameof(sheetSpace));
        }
        var linear = new ImageBuffer(sdrSheet.Width, sdrSheet.Height, (float[])sdrSheet.Data.Clone());
        OutputRender.Decode(linear.Data, sheetSpace);
        PasteCells(linear, extendedThumbs, layout, gridX, gridY);
        return linear;
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
