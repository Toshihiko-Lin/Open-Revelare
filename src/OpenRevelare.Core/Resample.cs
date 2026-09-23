namespace OpenRevelare.Core;

/// <summary>
/// Integer box-average downsampling — the one definition of how a full-resolution frame becomes
/// a preview.
///
/// It lives in Core, and the factor rule is exposed separately from the pixel loop, because two
/// call sites have to agree exactly: <see cref="Box"/> itself, which averages an
/// already-decoded float frame, and <see cref="RawDecode"/>'s downsampling decode, which averages
/// straight off LibRaw's 16-bit buffer so the full-resolution float frame is never allocated at
/// all. Those two must produce identical dimensions AND identical pixels — every Stage-1
/// measurement (t_base, wb_high, d_max, film base) is taken on the preview, so a discrepancy would
/// silently move the numbers depending on which path decoded the frame.
/// </summary>
public static class Resample
{
    /// <summary>
    /// The integer factor that brings the long edge to <paramref name="maxEdge"/> or below, or 1
    /// when the image is already small enough. Ceiling division: a factor that only just misses
    /// would leave the preview over budget.
    /// </summary>
    public static int BoxFactor(int width, int height, int maxEdge)
    {
        int longEdge = Math.Max(width, height);
        if (longEdge <= maxEdge) return 1;
        int factor = (longEdge + maxEdge - 1) / maxEdge;
        // A factor that would collapse either axis to nothing is not usable.
        return width / factor < 1 || height / factor < 1 ? 1 : factor;
    }

    /// <summary>
    /// Box-average <paramref name="src"/> by an integer factor so the preview render stays cheap on
    /// a 60 MP frame. Averaging happens in linear light (correct), and only when the long edge
    /// exceeds <paramref name="maxEdge"/> — otherwise the original is returned UNCHANGED (callers
    /// rely on that reference identity to skip work).
    /// </summary>
    public static ImageBuffer Box(ImageBuffer src, int maxEdge)
    {
        int factor = BoxFactor(src.Width, src.Height, maxEdge);
        if (factor == 1) return src;

        int sw = src.Width;
        int ow = sw / factor, oh = src.Height / factor;
        var dst = new ImageBuffer(ow, oh);
        float[] s = src.Data, d = dst.Data;
        float inv = 1.0f / (factor * factor);
        Parallel.For(0, oh, oy =>
        {
            for (int ox = 0; ox < ow; ox++)
            {
                float r = 0f, g = 0f, b = 0f;
                for (int fy = 0; fy < factor; fy++)
                {
                    int sy = oy * factor + fy;
                    int rowBase = (sy * sw + ox * factor) * 3;
                    for (int fx = 0; fx < factor; fx++)
                    {
                        int i = rowBase + fx * 3;
                        r += s[i]; g += s[i + 1]; b += s[i + 2];
                    }
                }
                int di = (oy * ow + ox) * 3;
                d[di] = r * inv; d[di + 1] = g * inv; d[di + 2] = b * inv;
            }
        });
        // The SOURCE lattice, not the averaged one: box-averaging factor² samples moves the values
        // onto a finer grid without recovering any information the file did not have.
        return dst.InheritSourceFrom(src);
    }

    /// <summary>
    /// The size <see cref="ToLongEdge"/> would produce: the long edge EXACTLY on
    /// <paramref name="longEdge"/> with the aspect ratio kept, or the source size unchanged when the
    /// picture is already smaller and <paramref name="allowUpscale"/> is false.
    ///
    /// Exposed so a dialog can state the outcome before the render runs, and so the two halves of a
    /// paired render (a gain-map JPEG's SDR base and its HDR layer) cannot disagree about it.
    /// </summary>
    public static (int Width, int Height) LongEdgeSize(int width, int height, int longEdge,
                                                      bool allowUpscale)
    {
        if (width <= 0 || height <= 0 || longEdge <= 0) return (Math.Max(1, width), Math.Max(1, height));
        int source = Math.Max(width, height);
        if (longEdge >= source && !allowUpscale) return (width, height);

        // The long edge lands on the requested value by construction rather than by rounding, so
        // "long edge 2048" means 2048 and not 2047. The short edge is the rounded one.
        return width >= height
            ? (longEdge, Math.Max(1, (int)Math.Round(height * (double)longEdge / width)))
            : (Math.Max(1, (int)Math.Round(width * (double)longEdge / height)), longEdge);
    }

    /// <summary>
    /// Scale <paramref name="src"/> so its long edge is exactly <paramref name="longEdge"/>, keeping
    /// the aspect ratio. Returns the source UNCHANGED (the same reference) when there is nothing to
    /// do — it is already that size, or it is smaller and <paramref name="allowUpscale"/> is false.
    ///
    /// WHY THIS EXISTS ALONGSIDE <see cref="Box"/>. Box averages by an INTEGER factor, which is
    /// exactly right for a preview — it has to match the downsampling decode sample for sample — and
    /// wrong for a delivery file: asked for 2048 it lands whereever ceil(edge / 2048) puts it, 1943 px
    /// on a 5832 px frame, and an export that quietly misses the size it was given is a bug report
    /// waiting to happen. Here the size is the requirement and the filter follows from it.
    ///
    /// DOWNSCALE is an exact area average: each output pixel is the mean of the source rectangle it
    /// covers, with edge samples weighted by how much of them that rectangle contains. It is the
    /// same operation Box performs, generalised to a fractional factor — so an integer request
    /// reproduces Box, and every other request keeps the property that matters: no source pixel is
    /// dropped and none is counted twice.
    ///
    /// UPSCALE is Catmull-Rom, the usual choice for photographic enlargement: it interpolates (the
    /// output passes through the source samples) and its slight overshoot at an edge reads as
    /// sharpness rather than as the softness of a bilinear enlargement. It cannot invent detail,
    /// which is why <paramref name="allowUpscale"/> is off by default and stated as a choice.
    ///
    /// Both run in whatever light the buffer holds. Every caller is a post-render linear or
    /// display-referred float frame, and averaging is correct in either; what would be wrong is
    /// averaging gamma-encoded integers, which never reaches here.
    /// </summary>
    public static ImageBuffer ToLongEdge(ImageBuffer src, int longEdge, bool allowUpscale)
    {
        var (dw, dh) = LongEdgeSize(src.Width, src.Height, longEdge, allowUpscale);
        if (dw == src.Width && dh == src.Height) return src;

        // Horizontal first, into a full-height intermediate, then vertical: two 1-D passes cost
        // (dw·h + dw·dh) weighted samples against the (dw·dh·taps²) of a single 2-D pass.
        float[] horizontal = ResampleAxis(src.Data, src.Width, src.Height, dw, horizontally: true);
        float[] both = ResampleAxis(horizontal, dw, src.Height, dh, horizontally: false);
        return new ImageBuffer(dw, dh, both).InheritSourceFrom(src);
    }

    /// <summary>
    /// Resample one axis of an RGB float image to <paramref name="length"/> samples.
    /// <paramref name="horizontally"/> selects which axis; the other is carried through untouched.
    /// </summary>
    private static float[] ResampleAxis(float[] src, int width, int height, int length,
                                        bool horizontally)
    {
        int srcLength = horizontally ? width : height;
        if (srcLength == length) return src;

        int dstWidth = horizontally ? length : width;
        int dstHeight = horizontally ? height : length;
        var dst = new float[dstWidth * dstHeight * 3];
        // Computed once per axis: every output row (or column) shares the same weights.
        (int Start, float[] Weights)[] taps = length < srcLength
            ? AreaTaps(srcLength, length)
            : CatmullRomTaps(srcLength, length);

        int lines = horizontally ? height : width;
        Parallel.For(0, lines, line =>
        {
            for (int i = 0; i < length; i++)
            {
                var (start, weights) = taps[i];
                float r = 0f, g = 0f, b = 0f;
                for (int t = 0; t < weights.Length; t++)
                {
                    float w = weights[t];
                    if (w == 0f) continue;
                    int si = horizontally
                        ? (line * width + (start + t)) * 3
                        : ((start + t) * width + line) * 3;
                    r += src[si] * w; g += src[si + 1] * w; b += src[si + 2] * w;
                }
                int di = horizontally
                    ? (line * dstWidth + i) * 3
                    : (i * dstWidth + line) * 3;
                dst[di] = r; dst[di + 1] = g; dst[di + 2] = b;
            }
        });
        return dst;
    }

    /// <summary>
    /// Exact area-average weights: output sample <c>i</c> covers the source interval
    /// <c>[i·s, (i+1)·s)</c> where <c>s = srcLength / length</c>, and each source sample contributes
    /// the fraction of itself that falls inside it. The weights of one output sample sum to 1, so a
    /// flat field keeps its value.
    /// </summary>
    private static (int Start, float[] Weights)[] AreaTaps(int srcLength, int length)
    {
        double scale = (double)srcLength / length;
        var taps = new (int, float[])[length];
        for (int i = 0; i < length; i++)
        {
            double from = i * scale, to = (i + 1) * scale;
            int first = Math.Clamp((int)Math.Floor(from), 0, srcLength - 1);
            int last = Math.Clamp((int)Math.Ceiling(to) - 1, first, srcLength - 1);
            var weights = new float[last - first + 1];
            double total = 0d;
            for (int s = first; s <= last; s++)
            {
                double covered = Math.Max(0d, Math.Min(to, s + 1) - Math.Max(from, s));
                weights[s - first] = (float)covered;
                total += covered;
            }
            Normalise(weights, total);
            taps[i] = (first, weights);
        }
        return taps;
    }

    /// <summary>
    /// Catmull-Rom weights for enlargement, sampling the source at output-pixel CENTRES
    /// (<c>(i + 0.5)·s - 0.5</c>) so the picture is not shifted by half a pixel. Four taps; a tap
    /// that falls outside the image is folded onto the border sample, which repeats the border
    /// instead of darkening towards it.
    /// </summary>
    private static (int Start, float[] Weights)[] CatmullRomTaps(int srcLength, int length)
    {
        double scale = (double)srcLength / length;
        var taps = new (int, float[])[length];
        for (int i = 0; i < length; i++)
        {
            double centre = (i + 0.5) * scale - 0.5;
            int first = (int)Math.Floor(centre) - 1;
            int lo = Math.Clamp(first, 0, srcLength - 1);
            int hi = Math.Clamp(first + 3, 0, srcLength - 1);
            var weights = new float[hi - lo + 1];
            double total = 0d;
            for (int t = 0; t < 4; t++)
            {
                double w = CatmullRom(centre - (first + t));
                weights[Math.Clamp(first + t, lo, hi) - lo] += (float)w;
                total += w;
            }
            Normalise(weights, total);
            taps[i] = (lo, weights);
        }
        return taps;
    }

    /// <summary>Scale <paramref name="weights"/> to sum to 1. A zero total cannot happen for either
    /// kernel, and is left alone rather than turned into a division by zero.</summary>
    private static void Normalise(float[] weights, double total)
    {
        if (total == 0d) return;
        for (int t = 0; t < weights.Length; t++) weights[t] = (float)(weights[t] / total);
    }

    /// <summary>The Catmull-Rom kernel (a = -0.5), zero outside |x| ≥ 2.</summary>
    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x >= 2d) return 0d;
        return x <= 1d
            ? 1.5 * x * x * x - 2.5 * x * x + 1d
            : -0.5 * x * x * x + 2.5 * x * x - 4d * x + 2d;
    }
}
