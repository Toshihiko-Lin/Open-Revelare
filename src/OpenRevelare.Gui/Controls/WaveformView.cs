using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// A waveform: for each COLUMN of the picture, how its samples are distributed from black to white.
///
/// WHY IT EARNS ITS PLACE NEXT TO THE HISTOGRAM. A histogram throws away where a value is. That is
/// exactly what you need for "is anything clipped" and exactly what you do not have for the two
/// questions a roll of negatives raises constantly: is this frame's cast uniform or does it drift
/// across the frame (an uneven light board, a corner of the copy stand), and does the sky in THIS
/// frame sit where the sky in the last one did. Both are visible at a glance here and invisible in
/// a histogram — a gradient across the frame and a uniform tint produce the same histogram.
///
/// It reads like a video waveform monitor: x is the frame's own x, y is level with white at the top,
/// brightness is how many samples landed there, and the three channels are drawn additively, so a
/// neutral is grey and a cast shows as the channels separating vertically.
/// </summary>
public sealed class WaveformData
{
    /// <summary>Cells, column-major: <c>channel[column * Levels + level]</c>.</summary>
    public required float[] R { get; init; }
    public required float[] G { get; init; }
    public required float[] B { get; init; }

    public required int Columns { get; init; }
    public required int Levels { get; init; }

    /// <summary>
    /// What a full cell would hold: the number of samples one output column covers. The trace is
    /// normalised against THIS rather than against the busiest cell in the frame, so a cell's
    /// brightness means "this fraction of the column sits at this level" — a reading that does not
    /// change when another part of the picture happens to contain a large flat area. Peak-relative
    /// normalising failed exactly there: one blown sky or a strip of clear film leader owns the
    /// maximum, and every real trace in the frame is then divided into the dark.
    /// </summary>
    public required float ColumnSamples { get; init; }

    /// <summary>Columns are the horizontal resolution of the plot. 256 is finer than the panel is
    /// wide, so the plot is never the limiting factor, and coarse enough to stay cheap.</summary>
    public const int DefaultColumns = 256;

    /// <summary>Levels are the vertical resolution. 160 over a ~70 px plot means every drawn row
    /// has samples behind it rather than being interpolated.</summary>
    public const int DefaultLevels = 160;

    /// <summary>
    /// Bin a display-referred [0,1] RGB buffer. Values above white (an extended render) are pinned
    /// to the top row: the waveform is a diagnostic of the picture's SHAPE, and an HDR frame's
    /// headroom belongs to the histogram, which has an axis for it.
    ///
    /// Parallelised by output COLUMN, so no two tasks touch the same cell and nothing has to be
    /// merged afterwards.
    /// </summary>
    public static WaveformData FromBuffer(float[] data, int width, int height,
                                          int columns = DefaultColumns, int levels = DefaultLevels)
    {
        columns = Math.Clamp(Math.Min(columns, Math.Max(1, width)), 1, DefaultColumns);
        levels = Math.Max(2, levels);

        var r = new float[columns * levels];
        var g = new float[columns * levels];
        var b = new float[columns * levels];

        Parallel.For(0, columns, column =>
        {
            int x0 = (int)((long)column * width / columns);
            int x1 = (int)((long)(column + 1) * width / columns);
            if (x1 <= x0) x1 = Math.Min(width, x0 + 1);
            int cell = column * levels;

            for (int x = x0; x < x1; x++)
                for (int y = 0; y < height; y++)
                {
                    int i = (y * width + x) * 3;
                    r[cell + Level(data[i], levels)]++;
                    g[cell + Level(data[i + 1], levels)]++;
                    b[cell + Level(data[i + 2], levels)]++;
                }
        });

        return new WaveformData
        {
            R = r, G = g, B = b, Columns = columns, Levels = levels,
            ColumnSamples = Math.Max(1f, (float)width * height / columns),
        };
    }

    /// <summary>Convenience overload for a rendered frame's pixels.</summary>
    public static WaveformData FromBuffer(ImageBuffer image)
        => FromBuffer(image.Data, image.Width, image.Height);

    private static int Level(float value, int levels)
    {
        // NaN is not a level and goes to the floor; +INFINITY is brighter than white and belongs at
        // the top with the rest of the clipped samples, not at the bottom with black.
        if (float.IsNaN(value) || value <= 0f) return 0;
        if (value >= 1f) return levels - 1;
        int level = (int)(value * levels);
        return level >= levels ? levels - 1 : level;
    }
}

/// <summary>
/// Draws a <see cref="WaveformData"/>. The cells are painted into a bitmap of exactly the data's
/// own size and stretched to the control: at 256×160 cells that is one image draw instead of forty
/// thousand rectangles, which matters because this repaints on every drag frame.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<WaveformData?> DataProperty =
        AvaloniaProperty.Register<WaveformView, WaveformData?>(nameof(Data));

    public WaveformData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    static WaveformView() => AffectsRender<WaveformView>(DataProperty);

    private WriteableBitmap? _bitmap;
    private int _bitmapColumns, _bitmapLevels;

    /// <summary>
    /// Trace brightness against the fraction of a column sitting in that cell. A linear ramp would
    /// show only the densest cells — a flat sky puts thousands of samples in one while a face
    /// spreads its samples over hundreds — so the fraction is raised to a fractional power, the
    /// standard way a waveform monitor keeps thin traces visible without blowing out thick ones.
    /// At 0.3 a cell holding 1% of its column still reads at a quarter brightness.
    /// </summary>
    private const float TraceGamma = 0.3f;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(21, 23, 26)), new Rect(0, 0, w, h));
        if (w < 2 || h < 2) return;

        WaveformData? d = Data;
        if (d is not null && Paint(d) is { } bitmap)
            ctx.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                          new Rect(0, 0, w, h));

        // The quarters of the range, as a ruler behind nothing: a waveform is read against levels,
        // and without them "how high is that sky" has no answer.
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(70, 120, 126, 134)), 1);
        for (int q = 1; q < 4; q++)
        {
            double y = h * q / 4d;
            ctx.DrawLine(grid, new Point(0, y), new Point(w, y));
        }
    }

    /// <summary>The cells as an image. The bitmap is kept and rewritten while the data keeps its
    /// shape, so a drag does not allocate one per frame.</summary>
    private WriteableBitmap? Paint(WaveformData d)
    {
        if (_bitmap is null || _bitmapColumns != d.Columns || _bitmapLevels != d.Levels)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(d.Columns, d.Levels), new Vector(96, 96),
                                          PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bitmapColumns = d.Columns;
            _bitmapLevels = d.Levels;
        }

        using ILockedFramebuffer buffer = _bitmap.Lock();
        unsafe
        {
            var row = (byte*)buffer.Address;
            float inverseColumn = 1f / d.ColumnSamples;
            for (int level = 0; level < d.Levels; level++)
            {
                // Level 0 is black and belongs at the BOTTOM: a waveform is read the way the
                // picture is, white at the top.
                int y = d.Levels - 1 - level;
                byte* line = row + (long)y * buffer.RowBytes;
                for (int column = 0; column < d.Columns; column++)
                {
                    int cell = column * d.Levels + level;
                    byte r = Trace(d.R[cell], inverseColumn);
                    byte g = Trace(d.G[cell], inverseColumn);
                    byte b = Trace(d.B[cell], inverseColumn);
                    byte a = Math.Max(r, Math.Max(g, b));
                    // Premultiplied: the channels ARE the colour, and the strongest of them is how
                    // opaque the cell is, so a cell lit in one channel only reads as that colour.
                    line[column * 4 + 0] = b;
                    line[column * 4 + 1] = g;
                    line[column * 4 + 2] = r;
                    line[column * 4 + 3] = a;
                }
            }
        }
        return _bitmap;
    }

    private static byte Trace(float count, float inverseColumn)
    {
        if (count <= 0f) return 0;
        float v = MathF.Pow(Math.Min(1f, count * inverseColumn), TraceGamma);
        return (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    }
}
