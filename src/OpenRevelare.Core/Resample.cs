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
        return dst;
    }
}
