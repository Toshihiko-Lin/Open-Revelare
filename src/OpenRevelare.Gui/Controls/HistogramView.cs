using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenRevelare.Gui.Controls;

/// <summary>256-bin histogram counts of the displayed image: R/G/B plus luma (L).</summary>
public sealed class HistogramData
{
    public required float[] R { get; init; }
    public required float[] G { get; init; }
    public required float[] B { get; init; }
    public required float[] L { get; init; }   // Rec.709 luma, for the W (master) curve backdrop

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

    private static int Bin(float v)
    {
        int i = (int)(v * 256.0f);
        return i < 0 ? 0 : (i > 255 ? 255 : i);
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

    static HistogramView() => AffectsRender<HistogramView>(DataProperty);

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
        if (d is not null)
        {
            DrawChannel(ctx, d.R, w, h, Channels[0]);
            DrawChannel(ctx, d.G, w, h, Channels[1]);
            DrawChannel(ctx, d.B, w, h, Channels[2]);
        }

        // Zero baseline.
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(52, 55, 60)), 1),
                     new Point(0, h - 1), new Point(w - 1, h - 1));
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
