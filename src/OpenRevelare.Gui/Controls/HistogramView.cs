using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Controls;

/// <summary>256-bin histogram counts of the displayed image: R/G/B plus luma (L).</summary>
public sealed class HistogramData
{
    public required float[] R { get; init; }
    public required float[] G { get; init; }
    public required float[] B { get; init; }
    public required float[] L { get; init; }   // Rec.709 luma, for the W (master) curve backdrop

    /// <summary>
    /// True when the bins above <see cref="SdrBinCount"/> hold values ABOVE SDR white, in stops.
    /// False for the established display-referred histogram, which is unchanged.
    /// </summary>
    public bool IsExtended { get; init; }

    /// <summary>How many of the 256 bins span [0,1]. 256 for SDR; 192 for an extended render.</summary>
    public int SdrBinCount { get; init; } = 256;

    /// <summary>
    /// How many stops above SDR white the extended bins cover. FOUR, the way Lightroom's HDR
    /// histogram is laid out (four segments of one stop each, the display's headroom dimmed
    /// off), so the two can be read against each other and a tier change moves the picture, not
    /// the ruler. Only a target that out-reaches four stops widens it: 4000 nits is +4.3, so it
    /// gets five rather than piling its top into the last bin.
    /// </summary>
    public float ExtendedStops { get; init; } = LightroomStops;

    public const float LightroomStops = 4f;

    /// <summary>
    /// The headroom the frame was rendered for (<see cref="OutputTarget.HighlightHeadroom"/>);
    /// one for a display-referred render. Carried here because the histogram and the scene are
    /// published together from one render, and the composition root needs this number next to
    /// the scene to soft-proof it (D-028).
    /// </summary>
    public float TargetHeadroom { get; init; } = 1f;

    /// <summary>The axis length for a target with this much headroom above diffuse white.</summary>
    public static float StopsFor(float targetHeadroom)
        => targetHeadroom > 1f && float.IsFinite(targetHeadroom)
            ? Math.Max(LightroomStops, MathF.Ceiling(MathF.Log2(targetHeadroom) - 1e-4f))
            : LightroomStops;

    private const int ExtendedSdrBins = 192;

    /// <summary>Bin the [0,1] interleaved RGB buffer into 256 counts per channel + luma.</summary>
    public static HistogramData FromBuffer(float[] data)
    {
        var r = new float[256];
        var g = new float[256];
        var b = new float[256];
        var l = new float[256];
        for (int p = 0; p < data.Length; p += 3)
        {
            float rv = data[p], gv = data[p + 1], bv = data[p + 2];
            r[Bin(rv)]++; g[Bin(gv)]++; b[Bin(bv)]++;
            l[Bin(0.2126f * rv + 0.7152f * gv + 0.0722f * bv)]++;
        }
        return new HistogramData { R = r, G = g, B = b, L = l };
    }

    /// <summary>
    /// The histogram of a rendered frame: the established one for a display-referred render, and
    /// an extended one for a scene-referred render.
    ///
    /// <para>
    /// WHY AN EXTENDED RENDER NEEDS ITS OWN. The established histogram bins <c>[0,1]</c> and
    /// clamps; fed a scene-referred render it would pile every highlight the render exists to keep
    /// into the last bin, and read as a clipped picture — the opposite of the truth. Here the
    /// <c>[0,1]</c> part is binned through the sRGB curve so its SHAPE matches the SDR histogram
    /// of the same picture (that part of the two renders is the same photograph), and the values
    /// above SDR white get their own zone, in stops.
    /// </para>
    ///
    /// <para>
    /// THE EXTENDED AXIS IS LIGHTROOM'S: four stops, fixed, whatever the target — see
    /// <see cref="ExtendedStops"/>. A 400-nit target (+1 stop) therefore uses a quarter of the
    /// zone and the rest stays empty, which is the honest picture: the zone shows how much of the
    /// HDR range the render occupies, and a tier change moves the bars, not the scale.
    /// </para>
    /// </summary>
    /// <param name="targetHeadroom">
    /// <see cref="OutputTarget.HighlightHeadroom"/> of the target the frame was rendered for.
    /// Ignored for a display-referred frame.
    /// </param>
    public static HistogramData FromFrame(RenderedFrame frame, float targetHeadroom)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Range != NumericRange.Extended ||
            frame.Encoding.Transfer != TransferState.LinearInProfilePrimaries)
        {
            return FromBuffer(frame.Pixels.Data);
        }

        float stops = StopsFor(targetHeadroom);
        float[] data = frame.Pixels.Data;
        var r = new float[256];
        var g = new float[256];
        var b = new float[256];
        var l = new float[256];
        for (int p = 0; p < data.Length; p += 3)
        {
            float rv = data[p], gv = data[p + 1], bv = data[p + 2];
            r[ExtendedBin(rv, stops)]++; g[ExtendedBin(gv, stops)]++; b[ExtendedBin(bv, stops)]++;
            l[ExtendedBin(0.2126f * rv + 0.7152f * gv + 0.0722f * bv, stops)]++;
        }
        return new HistogramData
        {
            R = r, G = g, B = b, L = l, IsExtended = true, SdrBinCount = ExtendedSdrBins,
            ExtendedStops = stops,
            TargetHeadroom = targetHeadroom > 1f && float.IsFinite(targetHeadroom) ? targetHeadroom : 1f,
        };
    }

    private static int Bin(float v)
    {
        int i = (int)(v * 256.0f);
        return i < 0 ? 0 : (i > 255 ? 255 : i);
    }

    private static int ExtendedBin(float v, float axisStops)
    {
        if (!(v > 1f))
        {
            // Linear -> sRGB-encoded, so the SDR zone reads like the SDR histogram does.
            int i = (int)(Srgb.LinearToSrgb(v < 0f ? 0f : v) * ExtendedSdrBins);
            return i < 0 ? 0 : (i > ExtendedSdrBins - 1 ? ExtendedSdrBins - 1 : i);
        }

        const int extendedBins = 256 - ExtendedSdrBins;
        float stops = MathF.Log2(v);
        int j = ExtendedSdrBins + (int)(stops / axisStops * extendedBins);
        return j > 255 ? 255 : j;
    }
}

/// <summary>
/// RGB overlay histogram — self-drawn port of Python's <c>gui/histogram_widget.py</c>:
/// semi-transparent per-channel fill + stroke, vertical ceiling at the 99.5th-percentile
/// count so black/white spikes don't crush the mid-tones.
/// </summary>
public sealed class HistogramView : Control
{
    public static readonly StyledProperty<HistogramData?> DataProperty =
        AvaloniaProperty.Register<HistogramView, HistogramData?>(nameof(Data));

    public HistogramData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>
    /// How far above SDR white the display under the window can show, as a multiple. Draws the
    /// "the panel stops here" marker in the extended zone; one means unknown or SDR and draws
    /// nothing. Informational only: it never changes the render (I5).
    /// </summary>
    public static readonly StyledProperty<double> DisplayHeadroomProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(DisplayHeadroom), 1d);

    public double DisplayHeadroom
    {
        get => GetValue(DisplayHeadroomProperty);
        set => SetValue(DisplayHeadroomProperty, value);
    }

    static HistogramView()
    {
        AffectsRender<HistogramView>(DataProperty);
        AffectsRender<HistogramView>(DisplayHeadroomProperty);
    }

    private static readonly (Color Fill, Color Stroke)[] Channels =
    {
        (Color.FromArgb(60, 200, 60, 60),  Color.FromArgb(200, 200, 60, 60)),
        (Color.FromArgb(60, 60, 180, 60),  Color.FromArgb(200, 60, 180, 60)),
        (Color.FromArgb(60, 60, 100, 220), Color.FromArgb(200, 60, 100, 220)),
    };

    /// <summary>
    /// Two kinds of marking, two places. The BIG divisions stay in the plot, because they are
    /// what the plot means: the tint says "this zone is a different axis", the solid line is SDR
    /// white, the dashed line and red wash are where this panel stops showing the render. The
    /// FINE scale — every stop, the encoded-value quarters, the numbers — lives in a ruler under
    /// the plot, because a row of ticks through the bars read as features of the picture.
    /// </summary>
    private const double RulerHeight = 14d;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(21, 23, 26)), new Rect(0, 0, w, h));
        if (w < 2 || h < RulerHeight + 2) return;

        double ph = h - RulerHeight;   // plot height; the ruler sits below
        HistogramData? d = Data;
        if (d is { IsExtended: true }) DrawExtendedZone(ctx, d, w, ph);
        if (d is not null)
        {
            DrawChannel(ctx, d.R, w, ph, Channels[0]);
            DrawChannel(ctx, d.G, w, ph, Channels[1]);
            DrawChannel(ctx, d.B, w, ph, Channels[2]);
        }
        if (d is { IsExtended: true }) DrawExtendedMarkers(ctx, d, w, ph);

        // Zero baseline doubles as the ruler's top edge.
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(52, 55, 60)), 1),
                     new Point(0, ph - 0.5), new Point(w, ph - 0.5));

        if (d is { IsExtended: true }) DrawExtendedRuler(ctx, d, w, ph, h);
        else DrawSdrRuler(ctx, w, ph, h);
    }

    private static double SdrWhiteX(HistogramData d, double w) => w * d.SdrBinCount / 256d;

    /// <summary>Where this panel stops showing the render, as an x; null when unknown or beyond the axis.</summary>
    private double? ClipX(HistogramData d, double w)
    {
        double headroom = DisplayHeadroom;
        if (!(headroom > 1d) || !double.IsFinite(headroom)) return null;
        double stops = Math.Log2(headroom);
        if (stops >= d.ExtendedStops) return null;   // panel out-reaches the axis; nothing clips on screen
        double x0 = SdrWhiteX(d, w);
        return x0 + (w - x0) * stops / d.ExtendedStops;
    }

    /// <summary>Tints the zone above SDR white so it reads as a different kind of axis.</summary>
    private static void DrawExtendedZone(DrawingContext ctx, HistogramData d, double w, double h)
    {
        double x0 = SdrWhiteX(d, w);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 210, 120)), new Rect(x0, 0, w - x0, h));
    }

    /// <summary>
    /// SDR white as a solid line and, when the display headroom is known, where the panel stops
    /// showing the render as a dashed one with a red wash beyond it: rendered, then clipped by
    /// the compositor — the part the user cannot see and should not grade against.
    /// </summary>
    private void DrawExtendedMarkers(DrawingContext ctx, HistogramData d, double w, double h)
    {
        double x0 = SdrWhiteX(d, w);
        ctx.DrawLine(new Pen(WhiteTickBrush, 1.5), new Point(x0, 0), new Point(x0, h - 1));

        if (ClipX(d, w) is not { } xClip) return;
        var dashed = new Pen(ClipTickBrush, 1.5) { DashStyle = new DashStyle(new double[] { 3d, 3d }, 0d) };
        ctx.DrawLine(dashed, new Point(xClip, 0), new Point(xClip, h - 1));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 80, 60)), new Rect(xClip, 0, w - xClip, h));
    }

    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly IBrush WhiteTickBrush = new SolidColorBrush(Color.FromArgb(220, 255, 230, 160));
    private static readonly IBrush ClipTickBrush = new SolidColorBrush(Color.FromArgb(230, 255, 120, 90));
    private const double LabelSize = 9d;

    /// <summary>Encoded value 0–1, quarter ticks, labelled at 0 / 0.5 / 1.</summary>
    private static void DrawSdrRuler(DrawingContext ctx, double w, double top, double bottom)
    {
        for (int q = 0; q <= 4; q++)
        {
            double x = Math.Clamp(w * q / 4d, 0.5, w - 0.5);
            bool major = q % 2 == 0;
            Tick(ctx, TickBrush, x, top, major ? 5 : 3);
            if (major) Label(ctx, q == 0 ? "0" : q == 4 ? "1" : "0.5", x, top, bottom, w);
        }
    }

    /// <summary>
    /// Two scales end to end, like the plot they sit under: encoded value up to SDR white on the
    /// left, then one tick per stop above it — as many stops as the target's headroom needs. The
    /// panel's limit, when known, is the same red mark the plot carries, continued down here.
    /// </summary>
    private void DrawExtendedRuler(DrawingContext ctx, HistogramData d, double w, double top, double bottom)
    {
        double x0 = SdrWhiteX(d, w);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(34, 255, 210, 120)), new Rect(x0, top, w - x0, bottom - top));

        Tick(ctx, TickBrush, 0.5, top, 5);
        Label(ctx, "0", 0.5, top, bottom, w);
        Tick(ctx, TickBrush, x0 / 2d, top, 3);
        Tick(ctx, WhiteTickBrush, x0, top, 7);
        Label(ctx, "1", x0, top, bottom, w);

        int stops = (int)d.ExtendedStops;
        for (int stop = 1; stop <= stops; stop++)
        {
            double x = Math.Min(x0 + (w - x0) * stop / d.ExtendedStops, w - 0.5);
            Tick(ctx, TickBrush, x, top, 5);
            Label(ctx, "+" + stop, x, top, bottom, w);
        }

        if (ClipX(d, w) is not { } xClip) return;
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 80, 60)), new Rect(xClip, top, w - xClip, bottom - top));
        ctx.DrawLine(new Pen(ClipTickBrush, 2), new Point(xClip, top), new Point(xClip, bottom));
    }

    private static void Tick(DrawingContext ctx, IBrush brush, double x, double top, double length)
        => ctx.DrawLine(new Pen(brush, 1), new Point(x, top), new Point(x, top + length));

    /// <summary>A label centred under its tick, pulled inside the edges at either end.</summary>
    private static void Label(DrawingContext ctx, string text, double x, double top, double bottom, double w)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   Typeface.Default, LabelSize, LabelBrush);
        double left = Math.Clamp(x - ft.Width / 2d, 1d, Math.Max(1d, w - ft.Width - 1d));
        ctx.DrawText(ft, new Point(left, bottom - ft.Height - 0.5));
    }

    private static void DrawChannel(DrawingContext ctx, float[] counts, double w, double h,
                                    (Color Fill, Color Stroke) col)
    {
        int n = counts.Length;
        // 99.5th-percentile ceiling so extreme spikes don't crush mid-tones.
        var sorted = (float[])counts.Clone();
        Array.Sort(sorted);
        float peak = sorted[Math.Min((int)(n * 0.995), n - 1)];
        if (peak <= 0f) peak = 1f;

        double baseline = h - 1;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            double x0 = 0.5 * w / n;
            double y0 = baseline - Math.Min(counts[0] / peak, 1.0) * (h - 2);
            gc.BeginFigure(new Point(x0, baseline), isFilled: true);
            gc.LineTo(new Point(x0, y0));
            for (int i = 1; i < n; i++)
            {
                double x = (i + 0.5) * w / n;
                double y = baseline - Math.Min(counts[i] / peak, 1.0) * (h - 2);
                gc.LineTo(new Point(x, y));
            }
            gc.LineTo(new Point((n - 0.5) * w / n, baseline));
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(col.Fill), new Pen(new SolidColorBrush(col.Stroke), 1.2), geo);
    }
}
