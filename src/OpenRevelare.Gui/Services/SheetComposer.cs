using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Assembles the finished contact sheet: paper, an optional header, the thumbnail grid with a
/// frame number under every cell, and the identification strip along the bottom — one image.
///
/// The grid itself comes from <see cref="ContactSheet"/> as pixels; everything drawn around it
/// needs a text rasteriser, so the surround is drawn here through Avalonia rather than in Core.
/// Every metric scales off a 2048 px reference width, so the dialog's cheap narrow preview is
/// the same design as the full-size export.
/// </summary>
public static class SheetComposer
{
    private const double RefWidth = 2048.0;

    /// <summary>
    /// How tall the row gap may grow, as a multiple of the thumbnail height it separates.
    ///
    /// Row spacing is the ONLY lever the page fit has — the column gap is the rebate between
    /// frames and does not move — so this bound decides which proportions are reachable at all.
    /// Measured against the FRAME rather than against the sheet because that is what the eye
    /// judges: a band up to about a frame tall reads as generous spacing, and past that it reads
    /// as a hole. A 12-frame roll of square negatives asked to be a wide 4:3 is what set it —
    /// at 1.4 frames the two rows looked like a mistake.
    ///
    /// It costs coverage on SHORT rolls, which have too few candidate grids to be spaced into
    /// every shape: from 20 frames up — every real roll — each target is met exactly.
    /// </summary>
    private const double RowGapCapFrames = 1.0;

    private static readonly FontFamily Face =
        new("Inter, Segoe UI, Microsoft YaHei, PingFang SC, sans-serif");

    /// <summary>What to print around the thumbnails. The palette and the page proportion are the
    /// choices — the lab-print furniture (header, keylines, frame numbers) is the house style,
    /// not a toggle.</summary>
    public sealed record Options
    {
        public SheetStyle Style { get; init; } = SheetStyle.Light;

        /// <summary>Proportion the finished page is solved for. Lives here rather than in
        /// <see cref="ContactSheet"/> because only this class knows how much paper the surround
        /// adds, and the surround is what makes a grid-shaped answer the wrong one.</summary>
        public SheetAspect Aspect { get; init; } = SheetAspect.Auto;

        /// <summary>Which way round <see cref="Aspect"/> is read. Moot for
        /// <see cref="SheetAspect.Square"/>.</summary>
        public SheetOrientation Orientation { get; init; } = SheetOrientation.Landscape;

        public SheetTheme Theme => SheetTheme.For(Style);
    }

    /// <summary>Grid pixels plus the geometry needed to annotate them. Built once per roll and
    /// reused across restyles — reprocessing the frames to change a colour would be absurd.</summary>
    public sealed record Grid(ImageBuffer Image, ContactSheet.Layout Layout);

    /// <summary>Lay out and render the thumbnail grid. Pure CPU; safe off the UI thread.</summary>
    public static Grid BuildGrid(IReadOnlyList<ImageBuffer> thumbs, int maxLong, Options opt)
    {
        ContactSheet.Layout layout = Plan(thumbs, maxLong, opt);
        return new Grid(ContactSheet.Build(thumbs, layout, opt.Theme.GapRgb), layout);
    }

    /// <summary>
    /// Pick the grid the requested proportion asks for. Every column count from 1 to n is planned
    /// and MEASURED as a finished sheet — the margins, header and info strip are a fixed slab of
    /// paper the grid does not know about, and at 12 frames they are enough to turn what looks
    /// like a square grid into a portrait page — then scored against the target.
    ///
    /// Ties are broken on empty cells: two column counts that land equally close to 4:3 are not
    /// equally good if one of them prints a half-empty last row.
    /// </summary>
    public static ContactSheet.Layout Plan(IReadOnlyList<ImageBuffer> thumbs, int maxLong, Options opt)
    {
        Metrics m = Metrics.Of(maxLong);
        int n = thumbs.Count;

        ContactSheet.Layout? best = null;
        (double Pad, int Empty) bestScore = default;

        for (int cols = 1; cols <= n; cols++)
        {
            // Rows are spaced wider than columns: the extra band is where the frame number goes.
            // Both gaps are the design values and stay that way — see PadFor.
            ContactSheet.Layout l = ContactSheet.Plan(thumbs, maxLong, m.GapXi, m.GapYi, cols);
            (int padX, int padY) = PadFor(l, opt);
            PixelSize bare = BareSize(l);
            // What fraction of the page is margin bought purely to make the proportion — the one
            // thing that separates candidates now that every one of them can reach the target.
            double pad = (double)(padX * 2) / (bare.Width + padX * 2)
                       + (double)(padY * 2) / (bare.Height + padY * 2);
            int empty = l.Rows * l.Cols - n;

            // A per cent of the page is not a difference anyone sees, so inside that the fuller
            // grid wins — which keeps a 36-frame roll on a complete 6×6.
            const double Same = 0.01;
            if (best is null || pad < bestScore.Pad - Same ||
                (pad < bestScore.Pad + Same && empty < bestScore.Empty))
            {
                best = l;
                bestScore = (pad, empty);
            }
        }
        return best!;
    }

    /// <summary>
    /// The extra margin, per side, that brings the page to the requested proportion — the ONLY
    /// thing the fit is allowed to move.
    ///
    /// The column count alone cannot deliver a shape: it is one candidate per integer, and on the
    /// tall side those are far apart — 36 frames of 3:2 can be 4×9 (0.47), 5×8 (0.69) or 6×6
    /// (1.18), so a portrait 4:3 (0.75) and a portrait 3:2 (0.67) both had to settle for 5×8 and
    /// came out as literally the same file.
    ///
    /// Spending the residual on the GAPS was the first answer and it was wrong twice over: the
    /// gap between columns is the rebate between frames on a strip and must not move at all, and
    /// stretching the ROW gap alone left visible holes between rows on the shapes it had to work
    /// hardest for. Margin is the right place for it — the grid keeps the density it was designed
    /// with, and, unlike a gap, margin can widen the page as well as heighten it, so a grid with
    /// an extra ROW is usable too instead of only ever approaching from the wide side.
    ///
    /// Because it works in both directions every column count can reach every target, so the
    /// search above is free to pick on how little margin it costs rather than on what it can
    /// reach. Nothing is capped: margin is white paper around a grid that keeps every pixel of
    /// its thumbnails, which is what a print with a generous border looks like — not a hole.
    ///
    /// Bisected rather than solved: the identification strip's height is derived from the page
    /// WIDTH, so the page is a fixed point of the margin, not a formula with a closed form.
    /// </summary>
    private static (int X, int Y) PadFor(ContactSheet.Layout l, Options opt)
    {
        PixelSize bare = BareSize(l);
        double got = (double)bare.Width / Math.Max(1, bare.Height);
        if (TargetAspect(opt.Aspect, opt.Orientation, got) is not { } target) return (0, 0);

        bool widen = got < target;
        // One side's worth. The page grows by twice this, centred, so the grid stays in the middle.
        int lo = 0, hi = Math.Max(bare.Width, bare.Height);
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (Reached(mid)) hi = mid; else lo = mid;
        }
        int pad = Reached(lo) ? lo : hi;
        return widen ? (pad, 0) : (0, pad);

        bool Reached(int pad)
        {
            PixelSize p = PaddedSize(l, widen ? pad : 0, widen ? 0 : pad);
            double a = (double)p.Width / Math.Max(1, p.Height);
            return widen ? a >= target : a <= target;
        }
    }

    /// <summary>
    /// The exact proportion the page should be brought to, or null when it already satisfies the
    /// request. <see cref="SheetAspect.Auto"/> asks for a BAND, so anything inside it is already
    /// right and only a page outside gets pulled to the nearer edge — which is why an auto sheet
    /// usually carries no extra margin at all.
    /// </summary>
    private static double? TargetAspect(SheetAspect want, SheetOrientation orient, double got)
    {
        const double Square = 1.0, FourThree = 4.0 / 3.0, ThreeTwo = 1.5;
        double Face(double landscape) =>
            orient == SheetOrientation.Portrait ? 1.0 / landscape : landscape;

        if (want != SheetAspect.Auto)
        {
            double t = Face(want switch
            {
                SheetAspect.Square => Square,
                SheetAspect.FourThree => FourThree,
                _ => ThreeTwo,
            });
            return Math.Abs(Math.Log(got / t)) < 1e-6 ? null : t;
        }

        double lo = Math.Min(Face(Square), Face(FourThree));
        double hi = Math.Max(Face(Square), Face(FourThree));
        if (got < lo) return lo;
        if (got > hi) return hi;
        return null;   // already in the band
    }

    /// <summary>Size the export will be, without rendering a pixel of it — the planner alone
    /// decides the geometry, so the dialog can label the button without doing the work.</summary>
    public static PixelSize SizeFor(IReadOnlyList<ImageBuffer> thumbs, int maxLong, Options opt)
        => SizeOf(Plan(thumbs, maxLong, opt), opt);

    /// <summary>The page at its design margins, before any is added to make the proportion.</summary>
    private static PixelSize BareSize(ContactSheet.Layout l) => PaddedSize(l, 0, 0);

    /// <summary>The page with <paramref name="padX"/> / <paramref name="padY"/> of extra margin on
    /// each side. Everything scales off the GRID's width, which the padding never touches, so the
    /// header and strip keep the proportions they were designed with however wide the border.</summary>
    private static PixelSize PaddedSize(ContactSheet.Layout l, int padX, int padY)
    {
        Metrics m = Metrics.Of(l.Width);
        int w = l.Width + (int)Math.Round(m.Margin * 2) + padX * 2;
        int h = (int)Math.Round(m.Margin + m.HeaderH + l.Height + m.Margin) + padY * 2
              + SheetInfoBar.HeightFor(w);
        return new PixelSize(w, h);
    }

    /// <summary>The finished page, margin included.</summary>
    private static PixelSize SizeOf(ContactSheet.Layout l, Options opt)
    {
        (int padX, int padY) = PadFor(l, opt);
        return PaddedSize(l, padX, padY);
    }

    /// <summary>Compose the finished sheet. Must run on the UI thread (Avalonia rasteriser).</summary>
    public static RenderTargetBitmap Compose(Grid grid, RollNotes notes, Options opt)
    {
        SheetTheme theme = opt.Theme;
        PixelSize size = SizeOf(grid.Layout, opt);
        // Deliberately the same basis SizeOf used — deriving margins from the composed width
        // instead would shift them a few px and leave the bottom margin not matching the top.
        Metrics m = Metrics.Of(grid.Layout.Width);
        // The margin the requested proportion bought, on top of the design margin. Recomputed
        // from the layout rather than carried, for the same reason the metrics are: two
        // derivations of the same number cannot drift apart if there is only one of them.
        (int padX, int padY) = PadFor(grid.Layout, opt);
        double marginX = m.Margin + padX, marginY = m.Margin + padY;

        double gridX = marginX;
        double gridY = marginY + m.HeaderH;

        var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (DrawingContext ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(theme.Paper, new Rect(0, 0, size.Width, size.Height));

            DrawHeader(ctx, m, notes, grid.Layout.Count, size.Width, marginX, marginY, theme);

            WriteableBitmap gridBmp = BitmapConvert.ToBitmap(grid.Image);
            ctx.DrawImage(gridBmp, new Rect(gridX, gridY, grid.Layout.Width, grid.Layout.Height));

            DrawCellAnnotations(ctx, m, grid.Layout, gridX, gridY, theme);

            SheetInfoBar.Draw(ctx, notes, size.Width,
                              size.Height - SheetInfoBar.HeightFor(size.Width), theme);
        }
        return rtb;
    }

    private static void DrawHeader(DrawingContext ctx, Metrics m, RollNotes n, int count,
                                   int width, double marginX, double marginY, SheetTheme theme)
    {
        // Roll identity, in the order you would read it off an envelope. The strip below carries
        // the full record; this line exists so the sheet is identifiable at a glance when it is
        // pinned to a wall and the bottom is out of view.
        string lead = string.Join("  ·  ", new[] { n.RollNumber, n.FilmStock, n.DevDate }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()));
        if (lead.Length == 0) lead = "CONTACT SHEET";

        FormattedText left = Text(lead, m.HeaderSize, theme.HeaderText, FontWeight.SemiBold);
        FormattedText right = Text(Loc.F($"{count} 帧"), m.HeaderSize, theme.HeaderDim, FontWeight.Normal);

        double baseline = marginY + (m.HeaderH - m.HeaderRuleGap - left.Height) / 2;
        ctx.DrawText(left, new Point(marginX, baseline));
        ctx.DrawText(right, new Point(width - marginX - right.Width, baseline));

        double ruleY = marginY + m.HeaderH - m.HeaderRuleGap;
        ctx.FillRectangle(theme.Rule, new Rect(marginX, ruleY, width - marginX * 2, m.Hairline));
    }

    private static void DrawCellAnnotations(DrawingContext ctx, Metrics m, ContactSheet.Layout l,
                                            double gridX, double gridY, SheetTheme theme)
    {
        var pen = new Pen(theme.Keyline, m.Hairline);

        // Repaint paper wherever film would not actually be: the bands between rows (which carry
        // the frame numbers) and any empty cells in a short last row. Core fills those from a
        // float triple while everything here is painted with the brush, so this also guarantees
        // the two agree exactly rather than to within a rounding step — a one-count mismatch
        // across a full-width band would read as a seam.
        for (int r = 0; r < l.Rows - 1; r++)
        {
            double bandY = gridY + r * (l.ThumbH + l.GapY) + l.ThumbH;
            ctx.FillRectangle(theme.Paper, new Rect(gridX, bandY, l.Width, l.GapY));
        }
        for (int i = l.Count; i < l.Rows * l.Cols; i++)
        {
            (int ex, int ey) = l.Origin(i);
            ctx.FillRectangle(theme.Paper, new Rect(gridX + ex, gridY + ey, l.ThumbW, l.ThumbH));
        }

        for (int i = 0; i < l.Count; i++)
        {
            (int cx, int cy) = l.Origin(i);
            double x = gridX + cx, y = gridY + cy;

            // Offset by half the stroke so the hairline sits just outside the frame rather than
            // straddling the edge and eating a pixel of the photo.
            double h = m.Hairline / 2;
            ctx.DrawRectangle(null, pen, new Rect(x - h, y - h, l.ThumbW + m.Hairline,
                                                  l.ThumbH + m.Hairline));

            // Frame numbers are 1-based, like the edge printing on a roll.
            FormattedText num = Text((i + 1).ToString(), m.NumberSize, theme.FrameNumber,
                                     FontWeight.Medium);
            ctx.DrawText(num, new Point(x, y + l.ThumbH + m.NumberGap));
        }
    }

    /// <summary>
    /// Read a composed sheet back out as an sRGB [0,1] buffer for the encoders.
    /// Call on the UI thread.
    ///
    /// The byte order is taken from the bitmap rather than assumed. A render target's layout is
    /// the platform's choice, not Avalonia's: Skia hands back Bgra8888 on Windows and Linux but
    /// Rgba8888 on macOS, and this used to unpack Bgra unconditionally. On macOS that swapped R
    /// and B in the EXPORTED file while the on-screen preview — which never goes through this
    /// method, it draws the RenderTargetBitmap directly — stayed correct, so a blue sky came out
    /// orange in the saved sheet and nowhere else.
    ///
    /// Anything other than the two 32-bit RGB orders would be a channel layout this loop cannot
    /// describe, so it is rejected rather than silently mis-unpacked.
    /// </summary>
    public static ImageBuffer ToBuffer(RenderTargetBitmap sheet)
    {
        int w = sheet.PixelSize.Width, h = sheet.PixelSize.Height;
        int stride = w * 4;
        byte[] px = new byte[stride * h];
        var buf = new ImageBuffer(w, h);
        float[] d = buf.Data;

        PixelFormat fmt = sheet.Format ?? PixelFormat.Bgra8888;
        if (fmt != PixelFormat.Bgra8888 && fmt != PixelFormat.Rgba8888)
            throw new NotSupportedException($"contact sheet render target has unsupported pixel format {fmt}");
        // Byte offsets of R and B within each 4-byte pixel; G and A sit in the same place either way.
        int rOff = fmt == PixelFormat.Rgba8888 ? 0 : 2;
        int bOff = fmt == PixelFormat.Rgba8888 ? 2 : 0;

        unsafe
        {
            fixed (byte* p = px)
                sheet.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, px.Length, stride);
        }

        for (int i = 0, o = 0; i < px.Length; i += 4, o += 3)
        {
            // Premultiplied, but the sheet is fully opaque so the straight channels
            // are already correct.
            d[o] = px[i + rOff] / 255f;
            d[o + 1] = px[i + 1] / 255f;
            d[o + 2] = px[i + bOff] / 255f;
        }
        return buf;
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face, FontStyle.Normal, weight), size, brush);

    /// <summary>The surround, scaled from the 2048 px reference design.</summary>
    private readonly struct Metrics
    {
        public readonly double Margin, Hairline, GapX, GapYExtra, NumberSize, NumberGap;
        public readonly double HeaderSize, HeaderH, HeaderRuleGap;

        private Metrics(double s)
        {
            Margin = Math.Round(58 * s);
            Hairline = Math.Max(1, Math.Round(2 * s));
            GapX = Math.Round(10 * s);
            // Row gap = column gap plus the band the frame number sits in.
            GapYExtra = Math.Round(34 * s);
            NumberSize = 20 * s;
            NumberGap = Math.Round(8 * s);
            HeaderSize = 26 * s;
            HeaderH = Math.Round(74 * s);
            HeaderRuleGap = Math.Round(18 * s);
        }

        public int GapXi => (int)GapX;
        public int GapYi => (int)(GapX + GapYExtra);

        public static Metrics Of(int width) => new(width / RefWidth);
    }
}
