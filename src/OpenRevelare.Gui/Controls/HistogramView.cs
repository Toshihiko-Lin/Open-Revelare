using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    /// <summary>How many stops above SDR white the extended bins cover.</summary>
    public const float ExtendedStops = 5f;

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
    /// THE EXTENDED AXIS IS FIXED, NOT FITTED TO THE TARGET. Five stops covers a 4000-nit target
    /// (19.7x, 4.3 stops) with room to spare, and keeping it fixed is what lets 600, 1000 and 4000
    /// be compared on the same axis: the picture moves, the ruler does not.
    /// </para>
    /// </summary>
    public static HistogramData FromFrame(RenderedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Range != NumericRange.Extended ||
            frame.Encoding.Transfer != TransferState.LinearInProfilePrimaries)
        {
            return FromBuffer(frame.Pixels.Data);
        }

        float[] data = frame.Pixels.Data;
        var r = new float[256];
        var g = new float[256];
        var b = new float[256];
        var l = new float[256];
        for (int p = 0; p < data.Length; p += 3)
        {
            float rv = data[p], gv = data[p + 1], bv = data[p + 2];
            r[ExtendedBin(rv)]++; g[ExtendedBin(gv)]++; b[ExtendedBin(bv)]++;
            l[ExtendedBin(0.2126f * rv + 0.7152f * gv + 0.0722f * bv)]++;
        }
        return new HistogramData
        {
            R = r, G = g, B = b, L = l, IsExtended = true, SdrBinCount = ExtendedSdrBins,
        };
    }

    private static int Bin(float v)
    {
        int i = (int)(v * 256.0f);
        return i < 0 ? 0 : (i > 255 ? 255 : i);
    }

    private static int ExtendedBin(float v)
    {
        if (!(v > 1f))
        {
            // Linear -> sRGB-encoded, so the SDR zone reads like the SDR histogram does.
            int i = (int)(Srgb.LinearToSrgb(v < 0f ? 0f : v) * ExtendedSdrBins);
            return i < 0 ? 0 : (i > ExtendedSdrBins - 1 ? ExtendedSdrBins - 1 : i);
        }

        const int extendedBins = 256 - ExtendedSdrBins;
        float stops = MathF.Log2(v);
        int j = ExtendedSdrBins + (int)(stops / ExtendedStops * extendedBins);
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

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(21, 23, 26)), new Rect(0, 0, w, h));
        if (w < 2 || h < 2) return;

        HistogramData? d = Data;
        if (d is { IsExtended: true }) DrawExtendedZone(ctx, d, w, h);
        if (d is not null)
        {
            DrawChannel(ctx, d.R, w, h, Channels[0]);
            DrawChannel(ctx, d.G, w, h, Channels[1]);
            DrawChannel(ctx, d.B, w, h, Channels[2]);
        }
        if (d is { IsExtended: true }) DrawExtendedMarkers(ctx, d, w, h);

        // Zero baseline.
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(52, 55, 60)), 1),
                     new Point(0, h - 1), new Point(w - 1, h - 1));
    }

    /// <summary>
    /// Tints the zone above SDR white so it reads as a different kind of axis (the SDR bins are
    /// encoded value, these are stops) and draws the stop ticks.
    /// </summary>
    private static void DrawExtendedZone(DrawingContext ctx, HistogramData d, double w, double h)
    {
        double x0 = w * d.SdrBinCount / 256d;
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 210, 120)), new Rect(x0, 0, w - x0, h));

        var tick = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
        for (int stop = 1; stop < (int)HistogramData.ExtendedStops; stop++)
        {
            double x = x0 + (w - x0) * stop / HistogramData.ExtendedStops;
            ctx.DrawLine(tick, new Point(x, h * 0.6), new Point(x, h - 1));
        }
    }

    /// <summary>
    /// SDR white as a solid line and, when the display headroom is known, where the panel stops
    /// showing the render as a dashed one. Everything right of the dashed line is rendered and
    /// then clipped by the compositor: the part the user cannot see and should not grade against.
    /// </summary>
    private void DrawExtendedMarkers(DrawingContext ctx, HistogramData d, double w, double h)
    {
        double x0 = w * d.SdrBinCount / 256d;
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 230, 160)), 1.5),
                     new Point(x0, 0), new Point(x0, h - 1));

        double headroom = DisplayHeadroom;
        if (!(headroom > 1d) || !double.IsFinite(headroom)) return;
        double stops = Math.Log2(headroom);
        if (stops >= HistogramData.ExtendedStops) return;   // panel out-reaches the axis; nothing clips on screen
        double xClip = x0 + (w - x0) * stops / HistogramData.ExtendedStops;
        var dashed = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 120, 90)), 1.5)
        {
            DashStyle = new DashStyle(new double[] { 3d, 3d }, 0d),
        };
        ctx.DrawLine(dashed, new Point(xClip, 0), new Point(xClip, h - 1));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 80, 60)), new Rect(xClip, 0, w - xClip, h));
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
