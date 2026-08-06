using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// The identification strip burned onto the bottom of a contact sheet.
///
/// This is where roll annotations live now. They used to be folded into every export's EXIF
/// description, which nothing downstream ever surfaced usefully — a contact sheet is the one
/// artefact where "which roll is this" has to be readable without a metadata viewer, so the
/// notes are rendered as pixels instead.
///
/// Layout is the lab-envelope form: a logo block on the left, then two columns of label/value
/// pairs, with 备注 spanning both on the last row. All nine labels are always drawn — the strip
/// is an identification card, so a blank field reads as "not recorded" (—) rather than
/// silently reflowing the layout.
///
/// Every metric is expressed against a 2048 px reference width and scaled, so the preview
/// (rendered narrow, for speed) and the exported bar (rendered at sheet width) are the same
/// design at different sizes.
/// </summary>
public static class SheetInfoBar
{
    private const double RefWidth = 2048.0;

    // Same stack as the app chrome — Inter carries the Latin, the CJK faces cover the labels.
    private static readonly FontFamily Face =
        new("Inter, Segoe UI, Microsoft YaHei, PingFang SC, sans-serif");

    private static Bitmap? _logo;

    /// <summary>Height the bar will occupy for a given sheet width.</summary>
    public static int HeightFor(int width) => (int)Math.Round(Metrics.Of(width).BarHeight);

    /// <summary>Render the strip at <paramref name="width"/> px. Must run on the UI thread.</summary>
    public static RenderTargetBitmap Render(RollNotes n, int width, SheetTheme theme)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(width, HeightFor(width)), new Vector(96, 96));
        using (DrawingContext ctx = rtb.CreateDrawingContext())
            Draw(ctx, n, width, 0, theme);
        return rtb;
    }

    /// <summary>Draw the strip into an existing context at vertical offset
    /// <paramref name="top"/> — used when the strip is part of a larger composed sheet.</summary>
    public static void Draw(DrawingContext ctx, RollNotes n, int width, double top, SheetTheme theme)
    {
        Metrics m = Metrics.Of(width);
        double height = m.BarHeight;

        // One flat ground across the whole strip — the logo sits on the same colour as the
        // fields rather than in a tinted pane of its own.
        ctx.FillRectangle(theme.BarBg, new Rect(0, top, width, height));

        // One hairline, above the strip, and that is the whole of it — it keeps the strip from
        // bleeding into the last row of frames. The logo is separated from the fields by the
        // gap between them, not by a rule.
        ctx.FillRectangle(theme.Rule, new Rect(0, top, width, m.Hairline));

        DrawMark(ctx, m, top, height, theme);
        DrawFields(ctx, m, n, top, height, theme);
    }

    private static void DrawMark(DrawingContext ctx, Metrics m, double top, double height,
                                 SheetTheme theme)
    {
        _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://OpenRevelare/Assets/icons/app-512.png")));

        FormattedText wordmark = Text("REVELARE", m.WordmarkSize, theme.Wordmark, FontWeight.SemiBold);
        // Mark and wordmark are centred as one unit, both ways — horizontally in the whole gap
        // to the left of the fields, not in some nominal pane. Once the field block was centred,
        // pane-centring left the mark visibly off to one side of its own whitespace.
        double blockH = m.LogoSize + m.Gutter * 0.5 + wordmark.Height;
        double y = top + (height - blockH) / 2;
        double midX = m.FieldsX / 2;

        ctx.DrawImage(_logo, new Rect(midX - m.LogoSize / 2, y, m.LogoSize, m.LogoSize));
        ctx.DrawText(wordmark, new Point(midX - wordmark.Width / 2,
                                         y + m.LogoSize + m.Gutter * 0.5));
    }

    private static void DrawFields(DrawingContext ctx, Metrics m, RollNotes n, double top,
                                   double height, SheetTheme theme)
    {
        // Column 1 = the shot; column 2 = the lab and the occasion. 备注 spans both underneath.
        (string Label, string Value)[] col1 =
        {
            (Loc.T("相机"), n.CameraBody),
            (Loc.T("胶卷"), n.FilmStock),
            (Loc.T("卷号"), n.RollNumber),
        };
        (string Label, string Value)[] col2 =
        {
            (Loc.T("冲洗店"), n.DevLab),
            (Loc.T("日期"), n.DevDate),
            (Loc.T("地点"), n.Location),
        };

        double fieldsTop = top + (height - (m.Rows * m.RowH)) / 2;
        double col1X = m.FieldsX;
        double col2X = col1X + m.ColW;

        for (int i = 0; i < col1.Length; i++)
        {
            double y = fieldsTop + i * m.RowH;
            DrawPair(ctx, m, col1X, y, col1[i].Label, col1[i].Value, m.ColW - m.Gutter, theme);
            DrawPair(ctx, m, col2X, y, col2[i].Label, col2[i].Value, m.ColW - m.Gutter, theme);
        }

        // 备注 runs across both columns — it is the one field that runs long.
        double noteY = fieldsTop + col1.Length * m.RowH;
        DrawPair(ctx, m, col1X, noteY, Loc.T("备注"), n.RollNote, m.ColW * 2 - m.Gutter, theme);
    }

    /// <summary>One label + value on a baseline, value ellipsised to <paramref name="maxW"/>.</summary>
    private static void DrawPair(DrawingContext ctx, Metrics m, double x, double y,
                                 string label, string value, double maxW, SheetTheme theme)
    {
        FormattedText lab = Text(label, m.LabelSize, theme.BarLabel, FontWeight.Normal);
        double valX = x + m.LabelColW;

        FormattedText val = Text(string.IsNullOrWhiteSpace(value) ? "—" : value.Trim(),
                                 m.ValueSize, theme.BarValue, FontWeight.Medium);
        // Measure unconstrained first — that height is one line. Capping MaxTextHeight to it is
        // how a FormattedText is held to a single line; without it a long 备注 would wrap and
        // grow the row past the strip.
        double lineH = val.Height;
        val.MaxTextWidth = Math.Max(m.ValueSize, maxW - m.LabelColW);
        val.MaxTextHeight = lineH;
        val.Trimming = TextTrimming.CharacterEllipsis;

        // Align the two on their vertical centres — the label is the smaller face, so sharing a
        // top edge would leave it visibly floating above its own value.
        double rowMid = y + m.RowH / 2;
        ctx.DrawText(lab, new Point(x, rowMid - lab.Height / 2));
        ctx.DrawText(val, new Point(valX, rowMid - val.Height / 2));
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face, FontStyle.Normal, weight), size, brush);

    /// <summary>Everything in the strip, scaled from the 2048 px reference design.</summary>
    private readonly struct Metrics
    {
        public readonly double Pad, Gutter, Hairline, MarkReserveW, LogoSize, WordmarkSize;
        public readonly double LabelSize, ValueSize, LabelColW, RowH, ColW, FieldsX, BarHeight;
        public readonly int Rows;

        private Metrics(double s, int width)
        {
            Rows = 4;                        // three paired rows + 备注
            Pad = 34 * s;
            Gutter = 20 * s;
            Hairline = Math.Max(1, Math.Round(2 * s));
            MarkReserveW = Math.Round(250 * s);   // room kept clear for the mark, not a drawn pane
            LogoSize = 104 * s;
            WordmarkSize = 19 * s;
            LabelSize = 21 * s;
            ValueSize = 29 * s;
            LabelColW = 108 * s;             // widest label is ISO/ASA
            RowH = 54 * s;

            // Columns are sized to their content, not stretched to fill. Dividing the leftover
            // width in two pushed column 2 out to the far right and left the whole strip
            // left-weighted, with a dead quarter past 地点 — so fix the column width and centre
            // the pair in the space beside the logo instead.
            ColW = 640 * s;
            double fieldsW = width - MarkReserveW;
            FieldsX = MarkReserveW + Math.Max(Pad, (fieldsW - ColW * 2) / 2);
            BarHeight = Rows * RowH + Pad * 2;
        }

        public static Metrics Of(int width) => new(width / RefWidth, width);
    }

}
