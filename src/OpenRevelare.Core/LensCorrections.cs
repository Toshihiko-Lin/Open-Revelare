namespace OpenRevelare.Core;

/// <summary>
/// Manual copy-lens corrections applied to the linear RAW BEFORE inversion —
/// port of negative/distortion.py and negative/vignette.py. Both are symmetric
/// about the full-frame centre (the copy-lens optical axis).
/// </summary>
public static class LensCorrections
{
    /// <summary>
    /// Single-parameter radial distortion correction (backward map, bilinear).
    /// k1&lt;0 corrects barrel, k1&gt;0 pincushion; 0 = pass-through. Out-of-bounds
    /// samples clamp to the edge (matches scipy map_coordinates mode='nearest').
    /// </summary>
    public static ImageBuffer ApplyDistortion(ImageBuffer img, double k1)
        => ApplyDistortion(img, k1, FrameRegion.Whole(img.Width, img.Height));

    /// <summary>
    /// The FRAME-space bounding box of source pixels an output region needs.
    ///
    /// The map is radial and monotonic in radius, so the extremes over a rectangle are attained
    /// on its boundary — walking the perimeter is exact, not a heuristic. Needed because a
    /// distortion of any strength pulls samples in from well outside the output rect (at k1=0.5
    /// a corner samples from twice its own radius), so a fixed margin cannot be right.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) DistortionSourceBounds(
        double rx0, double ry0, double rx1, double ry1, double k1, double frameW, double frameH)
    {
        double cx = (frameW - 1) * 0.5, cy = (frameH - 1) * 0.5;
        double norm = Math.Min(cx, cy);
        if (k1 == 0.0 || norm <= 0) return (rx0, ry0, rx1, ry1);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        void Probe(double x, double y)
        {
            double dx = (x - cx) / norm, dy = (y - cy) / norm;
            double s = 1.0 + k1 * (dx * dx + dy * dy);
            double sx = cx + (x - cx) * s, sy = cy + (y - cy) * s;
            if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
        }
        const int N = 64;
        for (int i = 0; i <= N; i++)
        {
            double t = (double)i / N;
            double x = rx0 + (rx1 - rx0) * t, y = ry0 + (ry1 - ry0) * t;
            Probe(x, ry0); Probe(x, ry1);      // top and bottom edges
            Probe(rx0, y); Probe(rx1, y);      // left and right edges
        }
        return (minX, minY, maxX, maxY);
    }

    /// <param name="region">Where this buffer sits in the frame. The barrel/pincushion term is
    /// radial about the FRAME centre and normalised by the FRAME's short half-axis; a slice given
    /// only its own size would warp about its own middle.</param>
    public static ImageBuffer ApplyDistortion(ImageBuffer img, double k1, FrameRegion region)
    {
        if (k1 == 0.0) return img;
        int w = img.Width, h = img.Height;
        double fw = region.FrameWidth, fh = region.FrameHeight;
        double cx = (fw - 1) * 0.5, cy = (fh - 1) * 0.5;
        double norm = Math.Min(cx, cy);
        if (norm <= 0) return img;

        var outImg = new ImageBuffer(w, h);
        float[] src = img.Data, dst = outImg.Data;

        // Source coordinates are computed in FLOAT32, not double, to mirror distortion.py:
        // it builds them from np.mgrid[...].astype(np.float32), and every scalar folded in
        // (cx, norm, k1, the 1.0) is a weakly-typed Python float that numpy demotes to the
        // array's float32 — k1=0.08 is really 0.079999998 there. scipy then widens the
        // coords to double for the interpolation itself, so ONLY the coordinates are
        // single-precision. Measured on the isolated pre-inversion chain this takes the
        // match from max 1 LSB / mean 0.001 to max 0-1 LSB / mean 0.0000.
        float cxf = (float)cx, cyf = (float)cy, normf = (float)norm, k1f = (float)k1;
        // Frame-space coordinates of this buffer's origin, and the frame-space clamp bounds.
        // With a whole-frame region these are 0 / (w-1, h-1) and every expression below reduces
        // to the original term for term.
        float ofsX = (float)region.OffsetX, ofsY = (float)region.OffsetY;
        int lastFx = (int)fw - 1, lastFy = (int)fh - 1;
        int iOfsX = (int)region.OffsetX, iOfsY = (int)region.OffsetY;
        Parallel.For(0, h, y =>
        {
            float fy0 = ofsY + y;
            for (int x = 0; x < w; x++)
            {
                float fx0 = ofsX + x;
                float dxf = (fx0 - cxf) / normf, dyf = (fy0 - cyf) / normf;
                float scalef = 1.0f + k1f * (dxf * dxf + dyf * dyf);
                double sx = cxf + (fx0 - cxf) * scalef;
                double sy = cyf + (fy0 - cyf) * scalef;

                // Bilinear sample with edge clamp (mode='nearest'). Clamp in FRAME space —
                // the edge the sampler must not read past is the frame's, not the slice's.
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                int x0c = Math.Clamp(x0, 0, lastFx), x1c = Math.Clamp(x0 + 1, 0, lastFx);
                int y0c = Math.Clamp(y0, 0, lastFy), y1c = Math.Clamp(y0 + 1, 0, lastFy);

                // …then back into this buffer. A correctly sized slice (see
                // DistortionSourceBounds) already contains these; the clamp is a guard, not a
                // policy, so an undersized slice degrades at its border instead of crashing.
                x0c = Math.Clamp(x0c - iOfsX, 0, w - 1); x1c = Math.Clamp(x1c - iOfsX, 0, w - 1);
                y0c = Math.Clamp(y0c - iOfsY, 0, h - 1); y1c = Math.Clamp(y1c - iOfsY, 0, h - 1);

                int i00 = (y0c * w + x0c) * 3, i10 = (y0c * w + x1c) * 3;
                int i01 = (y1c * w + x0c) * 3, i11 = (y1c * w + x1c) * 3;
                int d = (y * w + x) * 3;
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

    /// <summary>
    /// Radial corner gain in place: gain(r) = 1 + amount·r^falloff, r normalised so
    /// the corner = 1 (aspect-preserving). amount = 0 is a pass-through.
    /// </summary>
    public static void ApplyVignette(float[] data, int w, int h, double amount, double falloff)
        => ApplyVignette(data, w, h, amount, falloff, FrameRegion.Whole(w, h));

    /// <summary>
    /// <inheritdoc cref="ApplyVignette(float[], int, int, double, double)"/>
    /// </summary>
    /// <param name="region">Where this buffer sits in the frame. The falloff is radial about the
    /// FRAME centre and reaches 1 at the FRAME corner, so a slice must be told both — given only
    /// its own dimensions it would grow a fresh vignette out of the middle of the slice.</param>
    public static void ApplyVignette(float[] data, int w, int h, double amount, double falloff,
                                     FrameRegion region)
    {
        if (amount == 0.0) return;
        // Expression order matches the original exactly — `2.0 * v / (n - 1)`, not a
        // precomputed reciprocal. With OffsetX/Y = 0 and the frame size equal to the buffer
        // size this reduces term-for-term to what the whole-frame path always computed, which
        // is what keeps the parity baseline bit-identical.
        double fw = region.FrameWidth, fh = region.FrameHeight;
        Parallel.For(0, h, y =>
        {
            // yy in [-1,1] across the FRAME height, xx in [-1,1] across the FRAME width.
            float gy = fh > 1 ? (float)(-1.0 + 2.0 * (region.OffsetY + y) / (fh - 1)) : 0.0f;
            for (int x = 0; x < w; x++)
            {
                float gx = fw > 1 ? (float)(-1.0 + 2.0 * (region.OffsetX + x) / (fw - 1)) : 0.0f;
                double r = Math.Sqrt((gx * gx + gy * gy) * 0.5);
                float gain = (float)(1.0 + amount * Math.Pow(r, falloff));
                int b = (y * w + x) * 3;
                data[b] *= gain; data[b + 1] *= gain; data[b + 2] *= gain;
            }
        });
    }
}
