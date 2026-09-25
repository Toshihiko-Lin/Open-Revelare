using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// The roll record printed below the frames.
///
/// It follows a physical contact-print convention: a substantial four-row lab record paired with
/// the roll identity, large enough to anchor a print while remaining secondary to the negatives.
/// Empty fields disappear rather than printing placeholder dashes.
///
/// The left lock-up contains only the dedicated wordmark. Metadata uses a fixed three-column grid:
/// roll identity occupies the first row, processing details the middle rows, and the note the last.
/// </summary>
public static class SheetInfoBar
{
    private const double RefWidth = 2048.0;

    private static readonly FontFamily Face =
        new("Bahnschrift, Noto Sans SC, Inter, Segoe UI, Microsoft YaHei, PingFang SC, sans-serif");

    private static Bitmap? _wordmark;
    private static Bitmap? _wordmarkInverted;

    /// <summary>Height the footer will occupy for a given sheet width.</summary>
    public static int HeightFor(int width) => (int)Math.Round(Metrics.Of(width).BarHeight);

    /// <summary>Render the footer at <paramref name="width"/> px. Must run on the UI thread.</summary>
    public static RenderTargetBitmap Render(RollNotes n, int width, SheetTheme theme, int count = 0)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(width, HeightFor(width)), new Vector(96, 96));
        using (DrawingContext ctx = rtb.CreateDrawingContext())
            Draw(ctx, n, width, 0, theme, count);
        return rtb;
    }

    /// <summary>Draw the footer into an existing context at vertical offset
    /// <paramref name="top"/>.</summary>
    public static void Draw(DrawingContext ctx, RollNotes n, int width, double top, SheetTheme theme)
        => Draw(ctx, n, width, top, theme, 0);

    /// <summary>Draw the footer and include the roll's frame count in its identity lock-up.</summary>
    public static void Draw(DrawingContext ctx, RollNotes n, int width, double top,
                            SheetTheme theme, int count)
    {
        Metrics m = Metrics.Of(width);
        ctx.FillRectangle(theme.BarBg, new Rect(0, top, width, m.BarHeight));
        ctx.FillRectangle(theme.Rule, new Rect(0, top, width, m.Hairline));

        DrawBrandLockup(ctx, m, top, theme);

        double identityY = top + (m.BarHeight - m.RowH * 3) / 2;
        double topY = identityY + m.RowH;
        double middleY = topY + m.RowH;
        double bottomY = middleY + m.RowH;
        double fieldsW = width - m.BrandReserveW - m.Pad;
        double colW = (fieldsW - m.ItemGap * 2) / 3;
        double X(int col) => m.BrandReserveW + col * (colW + m.ItemGap);

        DrawPair(ctx, m, X(0), identityY, colW, Loc.T("卷号"), n.RollNumber, theme);
        DrawPair(ctx, m, X(1), identityY, colW, Loc.T("胶卷"), n.FilmStock, theme);
        DrawPair(ctx, m, X(2), identityY, colW, Loc.T("帧数"),
                 count > 0 ? count.ToString() : string.Empty, theme);

        DrawPair(ctx, m, X(0), topY, colW, Loc.T("相机"), n.CameraBody, theme);
        DrawPair(ctx, m, X(1), topY, colW, "ISO/ASA", n.FilmIso, theme);
        DrawPair(ctx, m, X(2), topY, colW, Loc.T("日期"), n.DevDate, theme);

        DrawPair(ctx, m, X(0), middleY, colW, Loc.T("冲洗店"), n.DevLab, theme);
        DrawPair(ctx, m, X(1), middleY, colW, Loc.T("工艺"), n.DevProcess, theme);
        DrawPair(ctx, m, X(2), middleY, colW, Loc.T("地点"), n.Location, theme);

        DrawPair(ctx, m, X(0), bottomY, fieldsW,
                 Loc.T("备注"), n.RollNote, theme);
    }

    private static void DrawBrandLockup(DrawingContext ctx, Metrics m, double top, SheetTheme theme)
    {
        _wordmark ??= new Bitmap(AssetLoader.Open(
            new Uri("avares://OpenRevelare/Assets/branding/contact-sheet-wordmark.png")));
        _wordmarkInverted ??= new Bitmap(AssetLoader.Open(
            new Uri("avares://OpenRevelare/Assets/branding/contact-sheet-wordmark-inverted.png")));

        Bitmap mark = theme.PaperRgb == SheetTheme.Dark.PaperRgb ? _wordmarkInverted : _wordmark;
        double wordmarkX = m.Pad;
        double wordmarkMaxW = m.BrandReserveW - m.BrandRuleGap - wordmarkX;
        double aspect = (double)mark.PixelSize.Width / mark.PixelSize.Height;
        double wordmarkW = Math.Min(wordmarkMaxW, m.WordmarkMaxH * aspect);
        double wordmarkH = wordmarkW / aspect;
        double wordmarkY = top + (m.BarHeight - wordmarkH) / 2;
        ctx.DrawImage(mark, new Rect(wordmarkX, wordmarkY, wordmarkW, wordmarkH));

        ctx.FillRectangle(theme.Rule,
            new Rect(m.BrandReserveW - m.BrandRuleGap, top + m.BrandRuleInset,
                     m.Hairline, m.BarHeight - m.BrandRuleInset * 2));
    }

    private static void DrawPair(DrawingContext ctx, Metrics m, double x, double midY,
                                 double width, string labelText, string valueText,
                                 SheetTheme theme)
    {
        if (string.IsNullOrWhiteSpace(valueText)) return;

        FormattedText label = Text(labelText, m.MetadataSize, theme.BarLabel, FontWeight.Medium);
        FormattedText value = Text(valueText.Trim(), m.MetadataSize,
                                   theme.BarValue, FontWeight.Medium);
        double valueX = x + m.LabelColW;
        value.MaxTextWidth = Math.Max(m.MetadataSize, width - m.LabelColW);
        value.MaxTextHeight = value.Height;
        value.Trimming = TextTrimming.CharacterEllipsis;

        ctx.DrawText(label, new Point(x, midY - label.Height / 2));
        ctx.DrawText(value, new Point(valueX, midY - value.Height / 2));
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face, FontStyle.Normal, weight), size, brush);

    private readonly struct Metrics
    {
        public readonly double Pad, Hairline, BarHeight, RowH;
        public readonly double BrandReserveW, BrandRuleGap, BrandRuleInset;
        public readonly double WordmarkMaxH;
        public readonly double MetadataSize, LabelColW, ItemGap;

        private Metrics(double s)
        {
            Pad = 32 * s;
            Hairline = Math.Max(1, Math.Round(2 * s));
            BarHeight = 232 * s;
            RowH = 48 * s;

            // Stable wordmark lock-up; no separate image mark is printed on the sheet.
            BrandReserveW = 590 * s;
            BrandRuleGap = 28 * s;
            BrandRuleInset = 36 * s;
            WordmarkMaxH = 132 * s;

            MetadataSize = 26 * s;
            LabelColW = 100 * s;
            ItemGap = 36 * s;
        }

        public static Metrics Of(int width) => new(width / RefWidth);
    }
}
