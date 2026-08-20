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

    private static readonly FontFamily Face =
        new("Inter, Segoe UI, Microsoft YaHei, PingFang SC, sans-serif");

    /// <summary>What to print around the thumbnails. Only the palette is a choice — the lab-print
    /// furniture (header, keylines, frame numbers) is the house style, not a toggle.</summary>
    public sealed record Options
    {
        public SheetStyle Style { get; init; } = SheetStyle.Light;

        public SheetTheme Theme => SheetTheme.For(Style);
    }

    /// <summary>Grid pixels plus the geometry needed to annotate them. Built once per roll and
    /// reused across restyles — reprocessing the frames to change a colour would be absurd.</summary>
    public sealed record Grid(ImageBuffer Image, ContactSheet.Layout Layout);

    /// <summary>Lay out and render the thumbnail grid. Pure CPU; safe off the UI thread.</summary>
    public static Grid BuildGrid(IReadOnlyList<ImageBuffer> thumbs, int maxLong, Options opt)
    {
        Metrics m = Metrics.Of(maxLong);
        // Rows are spaced wider than columns: the extra band is where the frame number goes.
        ContactSheet.Layout layout = ContactSheet.Plan(thumbs, maxLong, m.GapXi, m.GapYi);
        return new Grid(ContactSheet.Build(thumbs, layout, opt.Theme.GapRgb), layout);
    }

    /// <summary>Total pixel size of the composed sheet for a given grid.</summary>
    public static PixelSize SizeOf(Grid grid) => SizeOf(grid.Layout);

    /// <summary>Size the export will be, without rendering a pixel of it — the planner alone
    /// decides the geometry, so the dialog can label the button without doing the work.</summary>
    public static PixelSize SizeFor(IReadOnlyList<ImageBuffer> thumbs, int maxLong)
    {
        Metrics m = Metrics.Of(maxLong);
        return SizeOf(ContactSheet.Plan(thumbs, maxLong, m.GapXi, m.GapYi));
    }

    private static PixelSize SizeOf(ContactSheet.Layout l)
    {
        Metrics m = Metrics.Of(l.Width);
        int w = l.Width + (int)Math.Round(m.Margin * 2);
        int h = (int)Math.Round(m.Margin + m.HeaderH + l.Height + m.Margin)
              + SheetInfoBar.HeightFor(w);
        return new PixelSize(w, h);
    }

    /// <summary>Compose the finished sheet. Must run on the UI thread (Avalonia rasteriser).</summary>
    public static RenderTargetBitmap Compose(Grid grid, RollNotes notes, Options opt)
    {
        SheetTheme theme = opt.Theme;
        PixelSize size = SizeOf(grid);
        // Deliberately the same basis SizeOf used — deriving margins from the composed width
        // instead would shift them a few px and leave the bottom margin not matching the top.
        Metrics m = Metrics.Of(grid.Layout.Width);

        double gridX = m.Margin;
        double gridY = m.Margin + m.HeaderH;

        var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (DrawingContext ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(theme.Paper, new Rect(0, 0, size.Width, size.Height));

            DrawHeader(ctx, m, notes, grid.Layout.Count, size.Width, theme);

            WriteableBitmap gridBmp = BitmapConvert.ToBitmap(grid.Image);
            ctx.DrawImage(gridBmp, new Rect(gridX, gridY, grid.Layout.Width, grid.Layout.Height));

            DrawCellAnnotations(ctx, m, grid.Layout, gridX, gridY, theme);

            SheetInfoBar.Draw(ctx, notes, size.Width,
                              size.Height - SheetInfoBar.HeightFor(size.Width), theme);
        }
        return rtb;
    }

    private static void DrawHeader(DrawingContext ctx, Metrics m, RollNotes n, int count,
                                   int width, SheetTheme theme)
    {
        // Roll identity, in the order you would read it off an envelope. The strip below carries
        // the full record; this line exists so the sheet is identifiable at a glance when it is
        // pinned to a wall and the bottom is out of view.
        string lead = string.Join("  ·  ", new[] { n.RollNumber, n.FilmStock, n.DevDate }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()));
        if (lead.Length == 0) lead = "CONTACT SHEET";

        FormattedText left = Text(lead, m.HeaderSize, theme.HeaderText, FontWeight.SemiBold);
        FormattedText right = Text(Loc.F($"{count} 帧"), m.HeaderSize, theme.HeaderDim, FontWeight.Normal);

        double baseline = m.Margin + (m.HeaderH - m.HeaderRuleGap - left.Height) / 2;
        ctx.DrawText(left, new Point(m.Margin, baseline));
        ctx.DrawText(right, new Point(width - m.Margin - right.Width, baseline));

        double ruleY = m.Margin + m.HeaderH - m.HeaderRuleGap;
        ctx.FillRectangle(theme.Rule, new Rect(m.Margin, ruleY, width - m.Margin * 2, m.Hairline));
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
