namespace OpenRevelare.Core;

/// <summary>
/// LCC (Lens Cast Correction) flat-field correction — port of negative/lcc.py.
///
/// Unlike the manual radial vignette (which *models* the copy-lens darkening with
/// a formula), LCC uses a real *measurement*: the user shoots one flat, featureless
/// light frame that records this specific lens+stand's per-pixel brightness AND
/// colour non-uniformity. Correction is a per-channel divide by the mean-normalised
/// flat field, so both corner darkening and any corner colour cast are flattened.
/// A perfectly even flat field is an exact pass-through.
///
/// The flat field MUST be decoded on the same UniWB baseline as the content frames
/// (RAW → <see cref="RawDecode"/> UniWB; TIFF → <see cref="TiffIO"/>). Correction
/// runs in the linear domain, before inversion, upstream of the manual vignette.
/// </summary>
public static class Lcc
{
    // Guard so a near-black pixel in the flat field cannot explode the divide
    // (corrected = raw / ff → ∞ as ff → 0). A real flat field is nowhere near zero;
    // this only defends against dead pixels / decode glitches. Mirrors lcc.py._MIN_FF.
    private const float MinFf = 1e-3f;

    // scipy.ndimage.gaussian_filter default truncation: kernel radius = int(4·σ+0.5).
    private const double Truncate = 4.0;

    /// <summary>
    /// Decode a flat-field reference file to a mean-normalised (H, W, 3) field.
    ///
    /// RAW inputs decode on the UniWB baseline (matching decode_raw(no_wb=True));
    /// TIFF inputs load through <see cref="TiffIO.LoadTiff"/>. The field is lightly
    /// Gaussian-blurred (σ ≈ 0.5% of the short edge, min 1 px) to remove sensor/grain
    /// noise so the divide is smooth, then each channel is divided by its own mean →
    /// a mean-1 field encoding only the non-uniformity. Values floor at 1e-3.
    /// </summary>
    /// <param name="tiffIsLinear">For TIFF inputs: true = already linear, false = sRGB-gamma
    /// (inverse-TRC on load). Ignored for RAW.</param>
    public static ImageBuffer LoadFlatField(string path, bool tiffIsLinear)
    {
        ImageBuffer ff = RawDecode.IsRawExtension(path)
            ? RawDecode.DecodeRaw(path)                       // UniWB baseline
            : TiffIO.LoadTiff(path, inputIsSrgb: !tiffIsLinear);

        int w = ff.Width, h = ff.Height;

        // Smooth away noise so the per-pixel divide doesn't amplify grain/read noise.
        // σ scales with the short side (resolution-independent), floored at 1 px.
        double sigma = Math.Max(1.0, Math.Min(h, w) * 0.005);
        BlurEachChannel(ff.Data, w, h, sigma);

        // Normalise each channel to mean 1 → removes overall exposure, keeps shape.
        double sum0 = 0, sum1 = 0, sum2 = 0;
        float[] d = ff.Data;
        for (int i = 0; i < d.Length; i += 3) { sum0 += d[i]; sum1 += d[i + 1]; sum2 += d[i + 2]; }
        int n = w * h;
        float m0 = (float)(sum0 / n), m1 = (float)(sum1 / n), m2 = (float)(sum2 / n);
        if (!(m0 > MinFf)) m0 = 1.0f;
        if (!(m1 > MinFf)) m1 = 1.0f;
        if (!(m2 > MinFf)) m2 = 1.0f;

        // Divide by the per-channel mean, then floor to avoid divide-by-near-zero
        // when this field is later used as a divisor.
        for (int i = 0; i < d.Length; i += 3)
        {
            d[i]     = Math.Max(d[i]     / m0, MinFf);
            d[i + 1] = Math.Max(d[i + 1] / m1, MinFf);
            d[i + 2] = Math.Max(d[i + 2] / m2, MinFf);
        }
        return ff;
    }

    /// <summary>
    /// Divide a linear (H, W, 3) image in place by a mean-normalised flat field.
    /// The flat field is bilinearly resized to the image dimensions when they differ
    /// (content and flat may be shot/cropped at different resolutions). A perfectly
    /// even flat field (all ones) is an exact pass-through.
    /// </summary>
    public static void Apply(float[] data, int w, int h, ImageBuffer ffNorm)
        => Apply(data, w, h, ffNorm, FrameRegion.Whole(w, h));

    /// <param name="region">Where this buffer sits in the frame. The flat field describes the
    /// WHOLE frame's illumination, so a slice has to read its own window of it — stretching the
    /// entire field across the slice would divide the middle of a crop by the corner falloff.</param>
    public static void Apply(float[] data, int w, int h, ImageBuffer ffNorm, FrameRegion region)
    {
        int fw = (int)region.FrameWidth, fh = (int)region.FrameHeight;
        // The field is matched to the FRAME, then indexed at this buffer's offset.
        float[] ff = (ffNorm.Width == fw && ffNorm.Height == fh)
            ? ffNorm.Data
            : ResizeFlatField(ffNorm, fw, fh);

        if (region.IsWhole(w, h))
        {
            Parallel.For(0, h, y =>
            {
                int b = y * w * 3;
                for (int x = 0; x < w * 3; x++)
                    data[b + x] /= ff[b + x];
            });
            return;
        }

        int ox = (int)region.OffsetX, oy = (int)region.OffsetY;
        Parallel.For(0, h, y =>
        {
            int b = y * w * 3;
            int fb = ((oy + y) * fw + ox) * 3;
            for (int x = 0; x < w * 3; x++)
                data[b + x] /= ff[fb + x];
        });
    }

    // ── scipy-compatible separable Gaussian (mode='reflect', truncate=4.0) ────────
    private static void BlurEachChannel(float[] data, int w, int h, double sigma)
    {
        double[] kernel = GaussianKernel1D(sigma);
        int r = kernel.Length / 2;
        var plane = new float[w * h];
        var tmp = new float[w * h];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < plane.Length; i++) plane[i] = data[i * 3 + c];
            // Horizontal pass → tmp, then vertical pass → plane.
            Parallel.For(0, h, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double acc = 0;
                    for (int t = -r; t <= r; t++)
                        acc += plane[row + Reflect(x + t, w)] * kernel[t + r];
                    tmp[row + x] = (float)acc;
                }
            });
            Parallel.For(0, w, x =>
            {
                for (int y = 0; y < h; y++)
                {
                    double acc = 0;
                    for (int t = -r; t <= r; t++)
                        acc += tmp[Reflect(y + t, h) * w + x] * kernel[t + r];
                    plane[y * w + x] = (float)acc;
                }
            });
            for (int i = 0; i < plane.Length; i++) data[i * 3 + c] = plane[i];
        }
    }

    private static double[] GaussianKernel1D(double sigma)
    {
        int radius = (int)(Truncate * sigma + 0.5);
        double sigma2 = sigma * sigma;
        var k = new double[2 * radius + 1];
        double sum = 0;
        for (int x = -radius; x <= radius; x++)
        {
            double v = Math.Exp(-0.5 / sigma2 * x * x);
            k[x + radius] = v;
            sum += v;
        }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return k;
    }

    // scipy 'reflect' boundary (half-sample symmetric, period 2n): (…c b a | a b c…).
    private static int Reflect(int i, int n)
    {
        if (n == 1) return 0;
        int p = 2 * n;
        i %= p;
        if (i < 0) i += p;
        return i < n ? i : p - 1 - i;
    }

    // scipy.ndimage.zoom order=1, grid_mode=False: endpoints align exactly, so the
    // effective input coordinate for output o is o·(in-1)/(out-1) — align-corners
    // bilinear. Coordinates stay in-bounds; clamp guards float overshoot only.
    private static float[] ResizeFlatField(ImageBuffer ffNorm, int W, int H)
    {
        int w = ffNorm.Width, h = ffNorm.Height;
        float[] src = ffNorm.Data;
        var outp = new float[W * H * 3];
        double sy = h > 1 ? (double)(h - 1) / (H - 1) : 0.0;
        double sx = w > 1 ? (double)(w - 1) / (W - 1) : 0.0;

        Parallel.For(0, H, oy =>
        {
            double fy = oy * sy;
            int y0 = (int)Math.Floor(fy);
            double wy = fy - y0;
            int y0c = Math.Clamp(y0, 0, h - 1), y1c = Math.Clamp(y0 + 1, 0, h - 1);
            for (int ox = 0; ox < W; ox++)
            {
                double fx = ox * sx;
                int x0 = (int)Math.Floor(fx);
                double wx = fx - x0;
                int x0c = Math.Clamp(x0, 0, w - 1), x1c = Math.Clamp(x0 + 1, 0, w - 1);
                int i00 = (y0c * w + x0c) * 3, i10 = (y0c * w + x1c) * 3;
                int i01 = (y1c * w + x0c) * 3, i11 = (y1c * w + x1c) * 3;
                int od = (oy * W + ox) * 3;
                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * (1 - wx) + src[i10 + c] * wx;
                    double bot = src[i01 + c] * (1 - wx) + src[i11 + c] * wx;
                    outp[od + c] = Math.Max((float)(top * (1 - wy) + bot * wy), MinFf);
                }
            }
        });
        return outp;
    }
}
