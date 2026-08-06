namespace OpenRevelare.Core;

/// <summary>
/// Export-path geometry — port of negative/crop.py. Applied in the order
/// pipeline.py uses: discrete orientation (90° turns + flips) → straighten
/// rotation → crop. Orientation and crop are lossless index remaps; rotation is
/// bilinear with white fill (visually matches scipy's order-1 rotate; not claimed
/// bit-exact).
/// </summary>
public static class Geometry
{
    /// <summary>Discrete orientation: <paramref name="quarterTurns"/> 90° CW turns, then flips.</summary>
    public static ImageBuffer ApplyOrientation(ImageBuffer img, int quarterTurns, bool flipH, bool flipV)
    {
        ImageBuffer outImg = img;
        int k = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < k; i++) outImg = Rotate90Cw(outImg);
        if (flipH) outImg = FlipHorizontal(outImg);
        if (flipV) outImg = FlipVertical(outImg);
        return outImg;
    }

    private static ImageBuffer Rotate90Cw(ImageBuffer img)
    {
        int w = img.Width, h = img.Height;
        var outImg = new ImageBuffer(h, w); // dims swap
        float[] src = img.Data, dst = outImg.Data;
        // dst[c][h-1-r] = src[r][c]  → dst dims: width=h, height=w
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int s = (r * w + c) * 3;
                int dr = c, dc = h - 1 - r;
                int dstd = (dr * h + dc) * 3;
                dst[dstd] = src[s]; dst[dstd + 1] = src[s + 1]; dst[dstd + 2] = src[s + 2];
            }
        return outImg;
    }

    private static ImageBuffer FlipHorizontal(ImageBuffer img)
    {
        int w = img.Width, h = img.Height;
        var outImg = new ImageBuffer(w, h);
        float[] src = img.Data, dst = outImg.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3;
                int d = (y * w + (w - 1 - x)) * 3;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2];
            }
        return outImg;
    }

    private static ImageBuffer FlipVertical(ImageBuffer img)
    {
        int w = img.Width, h = img.Height;
        var outImg = new ImageBuffer(w, h);
        float[] src = img.Data, dst = outImg.Data;
        for (int y = 0; y < h; y++)
            Array.Copy(src, y * w * 3, dst, (h - 1 - y) * w * 3, w * 3);
        return outImg;
    }

    /// <summary>Crop to a normalised rect (x,y,w,h) in [0,1], origin top-left.</summary>
    public static ImageBuffer ApplyCrop(ImageBuffer img, (double X, double Y, double W, double H) rect)
    {
        int w = img.Width, h = img.Height;
        int x0 = Math.Max(0, (int)Math.Round(rect.X * w));
        int y0 = Math.Max(0, (int)Math.Round(rect.Y * h));
        int x1 = Math.Min(w, (int)Math.Round((rect.X + rect.W) * w));
        int y1 = Math.Min(h, (int)Math.Round((rect.Y + rect.H) * h));
        if (x1 <= x0 || y1 <= y0)
            throw new ArgumentException($"crop rect {rect} yields empty region for {w}×{h}");

        int cw = x1 - x0, ch = y1 - y0;
        var outImg = new ImageBuffer(cw, ch);
        float[] src = img.Data, dst = outImg.Data;
        for (int y = 0; y < ch; y++)
            Array.Copy(src, ((y0 + y) * w + x0) * 3, dst, y * cw * 3, cw * 3);
        return outImg;
    }

    /// <summary>Rotate clockwise by <paramref name="degrees"/>, same shape, white-filled corners.</summary>
    public static ImageBuffer ApplyRotation(ImageBuffer img, double degrees, float fill = 1.0f)
    {
        if (degrees == 0.0) return img;
        int w = img.Width, h = img.Height;
        var outImg = new ImageBuffer(w, h);
        float[] src = img.Data, dst = outImg.Data;

        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double th = degrees * Math.PI / 180.0;       // clockwise
        double cos = Math.Cos(th), sin = Math.Sin(th);

        // Inverse map (output → input), derived to match crop.py's
        //   scipy.ndimage.rotate(img, -degrees, axes=(1, 0), reshape=False, order=1)
        // TRAP: scipy sorts its axes — `if axes[0] > axes[1]: axes = axes[::-1]` — so
        // axes=(1,0) silently becomes (0,1) and the rotation plane is (row, col), NOT
        // (x, y). Combined with crop.py's -degrees that flips the sign of both sin
        // terms relative to the textbook (x, y) form. Getting this wrong rotates the
        // image the WRONG WAY and still looks plausible in isolation.
        Parallel.For(0, h, yo =>
        {
            for (int xo = 0; xo < w; xo++)
            {
                double dx = xo - cx, dy = yo - cy;
                double xi = cx + dx * cos + dy * sin;
                double yi = cy - dx * sin + dy * cos;
                int d = (yo * w + xo) * 3;

                // scipy's mode='constant' fills with cval OUTSIDE [0, n-1] and performs
                // no interpolation beyond the edge — it does not blend against cval, and
                // the live area is [0, n-1], not [-0.5, n-0.5].
                if (xi < 0.0 || xi > w - 1 || yi < 0.0 || yi > h - 1)
                {
                    dst[d] = fill; dst[d + 1] = fill; dst[d + 2] = fill;
                    continue;
                }

                int x0 = (int)Math.Floor(xi), y0 = (int)Math.Floor(yi);
                double fx = xi - x0, fy = yi - y0;
                // A coordinate landing exactly on the last row/column has no neighbour to
                // interpolate towards; step back one and weight it fully.
                if (x0 >= w - 1) { x0 = w - 2; fx = 1.0; }
                if (y0 >= h - 1) { y0 = h - 2; fy = 1.0; }
                int i00 = (y0 * w + x0) * 3, i10 = i00 + 3;
                int i01 = i00 + w * 3, i11 = i01 + 3;
                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * (1 - fx) + src[i10 + c] * fx;
                    double bot = src[i01 + c] * (1 - fx) + src[i11 + c] * fx;
                    dst[d + c] = (float)(top * (1 - fy) + bot * fy);
                }
            }
        });
        return outImg;
    }
}
