using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// The roll record printed below the frames.
///
/// It follows a physical contact-print convention: a substantial lab record paired with
/// the roll identity, large enough to anchor a print while remaining secondary to the negatives.
/// Empty fields disappear and the remaining fields close up rather than reserving blank cells.
///
/// The left lock-up contains only the dedicated wordmark. Metadata keeps its semantic row groups,
/// but each row is packed from only the values that exist; completely empty rows disappear too.
/// </summary>
public static class SheetInfoBar
{
    private const double RefWidth = 2048.0;

    // A restrained CJK serif gives the lab record an archival/editorial voice while keeping
    // Chinese, Latin letters and numerals in one family rather than mixing per-glyph fallbacks.
    private static readonly FontFamily Face =
        new("Noto Serif SC, Source Han Serif SC, Songti SC, SimSun, serif");
    private static readonly Typeface ProvenanceTypeface = new(
        new FontFamily("Bahnschrift, DIN Alternate, Avenir Next Condensed, Inter, sans-serif"),
        FontStyle.Normal, FontWeight.SemiBold, FontStretch.SemiCondensed);

    private static Bitmap? _wordmark;
    private static Bitmap? _wordmarkInverted;

    /// <summary>Height the footer will occupy for a given sheet width.</summary>
    public static int HeightFor(int width) => (int)Math.Round(Metrics.Of(width).BarHeight);

    /// <summary>Render the footer at <paramref name="width"/> px. Must run on the UI thread.</summary>
    public static RenderTargetBitmap Render(RollNotes n, int width, SheetTheme theme)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(width, HeightFor(width)), new Vector(96, 96));
        using (DrawingContext ctx = rtb.CreateDrawingContext())
            Draw(ctx, n, width, 0, theme);
        return rtb;
    }

    /// <summary>Draw the footer into an existing context at vertical offset
    /// <paramref name="top"/>.</summary>
    public static void Draw(DrawingContext ctx, RollNotes n, int width, double top, SheetTheme theme)
    {
        Metrics m = Metrics.Of(width);
        ctx.FillRectangle(theme.BarBg, new Rect(0, top, width, m.BarHeight));
        ctx.FillRectangle(theme.Rule, new Rect(0, top, width, m.Hairline));

        DrawBrandLockup(ctx, m, top, theme);

        double fieldsW = width - m.BrandReserveW - m.Pad;
        var rows = new[]
        {
            Filled((Loc.T("卷号"), n.RollNumber), (Loc.T("胶卷"), n.FilmStock)),
            Filled((Loc.T("相机"), n.CameraBody), ("ISO / EI", n.FilmIso),
                   (Loc.T("日期"), n.DevDate)),
            Filled((Loc.T("冲洗店"), n.DevLab), (Loc.T("工艺"), n.DevProcess),
                   (Loc.T("地点"), n.Location)),
            Filled((Loc.T("备注"), n.RollNote)),
        };
        var visibleRows = rows.Where(row => row.Length > 0).ToArray();
        double[] tracks = ColumnTracks(fieldsW, m.ItemGap);
        double[] labelCols = new double[3];
        foreach ((string Label, string Value)[] row in visibleRows)
        for (int col = 0; col < row.Length; col++)
        {
            double measured = Text(row[col].Label, m.MetadataSize,
                                   theme.BarLabel, FontWeight.Medium).Width + m.LabelValueGap;
            labelCols[col] = Math.Max(labelCols[col], Math.Min(measured, tracks[col] * 0.68));
        }
        double firstY = top + (m.BarHeight - m.RowH * Math.Max(0, visibleRows.Length - 1)) / 2;

        for (int row = 0; row < visibleRows.Length; row++)
            DrawRow(ctx, m, m.BrandReserveW, firstY + row * m.RowH,
                    tracks, labelCols, visibleRows[row], theme);
    }

    private static (string Label, string Value)[] Filled(
        params (string Label, string Value)[] fields) =>
        fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)).ToArray();

    private static double[] ColumnTracks(double width, double gap)
    {
        double usable = width - gap * 2;
        return [usable * 0.38, usable * 0.24, usable * 0.38];
    }

    private static void DrawRow(DrawingContext ctx, Metrics m, double x, double midY,
                                double[] tracks, double[] labelCols,
                                (string Label, string Value)[] fields,
                                SheetTheme theme)
    {
        // Every row shares three tracks: broad / compact / broad. ISO and process use the compact
        // centre because their values are short; camera/lab and date/location keep the room that
        // long real-world entries need. Missing fields move left, and the final value spans the
        // tracks that remain instead of leaving a placeholder cell behind.
        const int columns = 3;
        double[] starts = [x, x + tracks[0] + m.ItemGap,
                           x + tracks[0] + m.ItemGap + tracks[1] + m.ItemGap];
        for (int i = 0; i < fields.Length; i++)
        {
            int span = i == fields.Length - 1 ? columns - i : 1;
            double cellW = tracks.Skip(i).Take(span).Sum() + m.ItemGap * (span - 1);
            DrawPair(ctx, m, starts[i], midY, cellW, labelCols[i],
                     fields[i].Label, fields[i].Value, theme);
        }
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
        const string provenanceText = "NEGATIVE CONVERSION BY";
        FormattedText provenance = Text(provenanceText, ProvenanceTypeface,
                                        m.ProvenanceSize, theme.BarValue);
        double lockupH = provenance.Height + m.ProvenanceGap + wordmarkH;
        double provenanceY = top + (m.BarHeight - lockupH) / 2;
        double wordmarkY = provenanceY + provenance.Height + m.ProvenanceGap;
        double provenanceW = TrackedWidth(provenanceText, m.ProvenanceSize,
                                          theme.BarValue, m.ProvenanceTracking);
        double provenanceX = wordmarkX + (wordmarkW - provenanceW) / 2;
        DrawTrackedText(ctx, provenanceText, provenanceX, provenanceY, m.ProvenanceSize,
                        theme.BarValue, m.ProvenanceTracking);
        ctx.DrawImage(mark, new Rect(wordmarkX, wordmarkY, wordmarkW, wordmarkH));

        ctx.FillRectangle(theme.Rule,
            new Rect(m.BrandReserveW - m.BrandRuleGap, top + m.BrandRuleInset,
                     m.Hairline, m.BarHeight - m.BrandRuleInset * 2));
    }

    private static void DrawPair(DrawingContext ctx, Metrics m, double x, double midY,
                                 double width, double labelColW,
                                 string labelText, string valueText,
                                 SheetTheme theme)
    {
        if (string.IsNullOrWhiteSpace(valueText)) return;

        FormattedText label = Text(labelText, m.MetadataSize, theme.BarLabel, FontWeight.Medium);
        FormattedText value = Text(valueText.Trim(), m.MetadataSize,
                                   theme.BarValue, FontWeight.Bold);
        double labelLineH = label.Height;
        double valueLineH = value.Height;
        label.MaxTextHeight = labelLineH;
        label.MaxTextWidth = Math.Max(m.MetadataSize, labelColW - m.LabelValueGap);
        label.Trimming = TextTrimming.CharacterEllipsis;
        double valueX = x + labelColW;
        value.MaxTextHeight = valueLineH;
        value.MaxTextWidth = Math.Max(m.MetadataSize, width - labelColW);
        value.Trimming = TextTrimming.CharacterEllipsis;

        ctx.DrawText(label, new Point(x, midY - label.Height / 2));
        ctx.DrawText(value, new Point(valueX, midY - value.Height / 2));
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face, FontStyle.Normal, weight), size, brush);

    private static FormattedText Text(string s, Typeface typeface, double size, IBrush brush) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, brush);

    private static double TrackedWidth(string text, double size, IBrush brush, double tracking)
    {
        double width = 0;
        for (int i = 0; i < text.Length; i++)
        {
            width += text[i] == ' '
                ? size * 0.36
                : Text(text[i].ToString(), ProvenanceTypeface, size, brush).Width;
            if (i < text.Length - 1) width += tracking;
        }
        return width;
    }

    private static void DrawTrackedText(DrawingContext ctx, string text, double x, double y,
                                        double size, IBrush brush, double tracking)
    {
        double cursor = x;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
                cursor += size * 0.36;
            else
            {
                FormattedText glyph = Text(text[i].ToString(), ProvenanceTypeface, size, brush);
                ctx.DrawText(glyph, new Point(cursor, y));
                cursor += glyph.Width;
            }
            if (i < text.Length - 1) cursor += tracking;
        }
    }

    private readonly struct Metrics
    {
        public readonly double Pad, Hairline, BarHeight, RowH;
        public readonly double BrandReserveW, BrandRuleGap, BrandRuleInset;
        public readonly double WordmarkMaxH, ProvenanceSize, ProvenanceGap, ProvenanceTracking;
        public readonly double MetadataSize, LabelValueGap, ItemGap;

        private Metrics(double s)
        {
            Pad = 32 * s;
            Hairline = Math.Max(1, Math.Round(2 * s));
            BarHeight = 248 * s;
            RowH = 60 * s;

            // Stable wordmark lock-up; no separate image mark is printed on the sheet.
            BrandReserveW = 590 * s;
            BrandRuleGap = 28 * s;
            BrandRuleInset = 36 * s;
            WordmarkMaxH = 118 * s;
            ProvenanceSize = 20 * s;
            ProvenanceGap = 14 * s;
            ProvenanceTracking = 1.8 * s;

            MetadataSize = 35 * s;
            LabelValueGap = 16 * s;
            ItemGap = 36 * s;
        }

        public static Metrics Of(int width) => new(width / RefWidth);
    }
}
