namespace OpenRevelare.Core;

/// <summary>
/// Path-A decouple CALIBRATION — port of negative/decouple.py. Computes the 3×3
/// matrices and per-channel chroma-amplification that <see cref="Decouple"/> and
/// <see cref="Inversion"/> consume, from R/G/B light-source calibration frames.
///
/// Calibration frames must be decoded on the same UniWB baseline as content frames
/// (<see cref="RawDecode"/>) — the shared colour reference is what lets the
/// row-normalised matrix map content cleanly.
/// </summary>
public static class DecoupleCalibration
{
    // Orthonormal basis of the density-chroma plane (the sum=0 subspace):
    //   YB = yellow(+)/blue(-)  (R+G vs B),  RG = red(+)/green(-) (R vs G).
    private static readonly double[] ChromaYb = { 1.0 / Math.Sqrt(6.0), 1.0 / Math.Sqrt(6.0), -2.0 / Math.Sqrt(6.0) };
    private static readonly double[] ChromaRg = { 1.0 / Math.Sqrt(2.0), -1.0 / Math.Sqrt(2.0), 0.0 };

    /// <summary>
    /// Plain matrix apply with negatives clamped to 0 — port of apply_decouple_matrix
    /// (the calibration-stage apply, NOT the gamut-mapped <see cref="Decouple.Apply"/>).
    /// Used to build the decoupled frame the chroma-amp statistics are measured on.
    /// Returns a new buffer.
    /// </summary>
    public static ImageBuffer ApplyMatrixClip(ImageBuffer img, double[,] m)
    {
        double m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
        double m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
        double m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];
        var outImg = new ImageBuffer(img.Width, img.Height);
        float[] s = img.Data, o = outImg.Data;
        Parallel.For(0, img.PixelCount, p =>
        {
            int i = p * 3;
            double r = s[i], g = s[i + 1], b = s[i + 2];
            o[i]     = (float)Math.Max(m00 * r + m01 * g + m02 * b, 0.0);
            o[i + 1] = (float)Math.Max(m10 * r + m11 * g + m12 * b, 0.0);
            o[i + 2] = (float)Math.Max(m20 * r + m21 * g + m22 * b, 0.0);
        });
        return outImg;
    }

    /// <summary>
    /// Mean of the central 40–60% region → (3,) double. Mirrors _roi_mean, including its
    /// precision: numpy's <c>mean(axis=0)</c> on an (N,3) float32 array accumulates
    /// SEQUENTIALLY in float32 (the reduced axis is strided, so numpy's pairwise summation
    /// does not kick in) and divides by N in float32 before the .astype(float64). Matching
    /// that exactly — rather than accumulating in double, which is what the arithmetic
    /// "deserves" — is what makes the decouple matrices bit-exact instead of ~3e-6 off, and
    /// that error otherwise compounds through the matrix inverse into ~1e-5 on chroma_amp.
    /// </summary>
    public static double[] RoiMean(ImageBuffer img)
    {
        int h = img.Height, w = img.Width;
        int r0 = (int)(h * 0.4), r1 = (int)(h * 0.6);
        int c0 = (int)(w * 0.4), c1 = (int)(w * 0.6);
        if (r0 >= r1 || c0 >= c1) { r0 = 0; r1 = h; c0 = 0; c1 = w; }

        float s0 = 0, s1 = 0, s2 = 0;
        int n = 0;
        float[] d = img.Data;
        for (int y = r0; y < r1; y++)          // row-major, matching the reshape(-1, 3) order
        {
            int row = y * w * 3;
            for (int x = c0; x < c1; x++)
            {
                int i = row + x * 3;
                s0 += d[i]; s1 += d[i + 1]; s2 += d[i + 2];
                n++;
            }
        }
        return new double[] { s0 / (float)n, s1 / (float)n, s2 / (float)n };
    }

    /// <summary>
    /// Linear-domain decoupling matrix from three calibration images (row-normalised
    /// inverse of the ROI observation matrix). Result M satisfies decoupled = raw · Mᵀ.
    /// Throws if the observation matrix is near-singular (bad lighting / wrong assignment).
    /// </summary>
    public static double[,] ComputeDecoupleMatrix(ImageBuffer calR, ImageBuffer calG, ImageBuffer calB)
        => DecoupleMatrixFromRoiMeans(RoiMean(calR), RoiMean(calG), RoiMean(calB));

    /// <summary>
    /// The same matrix from three already-computed ROI means. Exists so a caller can decode a
    /// calibration frame, take its <see cref="RoiMean"/>, and release the buffer — the three full
    /// frames are ~288 MB each and nothing beyond their centre-ROI mean is ever used.
    /// </summary>
    public static double[,] DecoupleMatrixFromRoiMeans(double[] vR, double[] vG, double[] vB)
        => RowNormalisedInverse(ColumnStack(vR, vG, vB));

    /// <summary>
    /// Density-domain decoupling matrix. With calibration images, re-derives from the
    /// ROI means in density space; otherwise uses the same fixed proxy vectors as
    /// decouple.py's fallback.
    /// </summary>
    public static double[,] ComputeDensityMatrix(ImageBuffer? calR = null, ImageBuffer? calG = null, ImageBuffer? calB = null)
    {
        double[] vR, vG, vB;
        if (calR != null && calG != null && calB != null)
        {
            vR = RoiMean(calR); vG = RoiMean(calG); vB = RoiMean(calB);
        }
        else
        {
            vR = new[] { 1.0, 0.1, 0.05 };
            vG = new[] { 0.1, 1.0, 0.2 };
            vB = new[] { 0.05, 0.15, 1.0 };
        }
        double[] dR = Density(vR), dG = Density(vG), dB = Density(vB);
        return RowNormalisedInverse(ColumnStack(dR, dG, dB));

        static double[] Density(double[] v) => new[]
        {
            -Math.Log10(Math.Max(v[0], 1e-10)),
            -Math.Log10(Math.Max(v[1], 1e-10)),
            -Math.Log10(Math.Max(v[2], 1e-10)),
        };
    }

    /// <summary>
    /// Per-channel chroma amplification amp_c = std_post_c / std_pre_c (density chroma),
    /// clamped to [1, 4]. Degenerate → [1,1,1]. This is <see cref="FrameParams.DecoupleChromaAmp"/>.
    /// </summary>
    public static double[] ChromaAmplificationPerChannel(ImageBuffer pre, ImageBuffer post, bool[]? mask = null)
    {
        double[] p = DensityChromaStdPerChannel(pre, mask);
        double[] q = DensityChromaStdPerChannel(post, mask);
        if (!Finite(p) || !Finite(q) || p[0] <= 1e-6 || p[1] <= 1e-6 || p[2] <= 1e-6)
            return new[] { 1.0, 1.0, 1.0 };
        return new[]
        {
            Math.Clamp(q[0] / p[0], 1.0, 4.0),
            Math.Clamp(q[1] / p[1], 1.0, 4.0),
            Math.Clamp(q[2] / p[2], 1.0, 4.0),
        };
    }

    /// <summary>
    /// Pooled scalar chroma amplification std(post)/std(pre) over all density-chroma
    /// elements, clamped to [1, 4]. Degenerate → 1.0.
    /// </summary>
    public static double ChromaAmplification(ImageBuffer pre, ImageBuffer post, bool[]? mask = null)
    {
        double p = DensityChromaStdPooled(pre, mask);
        double q = DensityChromaStdPooled(post, mask);
        if (p <= 1e-6 || double.IsNaN(p) || double.IsNaN(q)) return 1.0;
        return Math.Clamp(q / p, 1.0, 4.0);
    }

    /// <summary>
    /// 3×3 density-chroma compensation matrix undoing the decouple matrix's anisotropic
    /// boost along the yellow-blue and red-green axes: C = B·diag(1/amp)·Bᵀ. Symmetric,
    /// maps into the sum=0 plane (no luminance leak). Degenerate → identity. This is
    /// <see cref="FrameParams.DecoupleChromaMatrix"/>.
    /// </summary>
    public static double[,] ChromaAxisCompensationMatrix(ImageBuffer pre, ImageBuffer post, bool[]? mask = null)
    {
        double[] p = AxisChromaStd(pre, mask);
        double[] q = AxisChromaStd(post, mask);
        if (double.IsNaN(p[0]) || double.IsNaN(p[1]) || double.IsNaN(q[0]) || double.IsNaN(q[1])
            || p[0] <= 1e-6 || p[1] <= 1e-6)
            return Identity3();

        double ampYb = Math.Clamp(q[0] / p[0], 1.0, 4.0);
        double ampRg = Math.Clamp(q[1] / p[1], 1.0, 4.0);
        double iYb = 1.0 / ampYb, iRg = 1.0 / ampRg;

        // C = B · diag(iYb, iRg) · Bᵀ, with B = [yb | rg] (3×2).
        var c = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = ChromaYb[i] * iYb * ChromaYb[j] + ChromaRg[i] * iRg * ChromaRg[j];
        return c;
    }

    /// <summary>
    /// Adaptively compute (alpha, chroma_amp) for the DENSITY-domain chroma decouple —
    /// port of compute_decouple_params.
    ///
    /// alpha blends the matrix toward identity: M_alpha = (1-a)·I + a·M. We take the
    /// largest alpha in [0,1] keeping the fraction of negative-density output pixels below
    /// <paramref name="negThreshold"/>. chroma_amp is std(D_chroma_after)/std(D_chroma_before)
    /// at the chosen alpha, measured on the actual image — it is image-dependent, not a pure
    /// matrix property, because real film chroma is anisotropic in the sum=0 plane.
    ///
    /// Uses a coarse stride-8 spatial subsample for speed, as Python does.
    /// </summary>
    public static (double Alpha, double ChromaAmp) ComputeDecoupleParams(
        double[,] m, ImageBuffer image, double dMax = 4.0, double negThreshold = 0.005)
    {
        double[][] sub = Subsample(image, 8);
        double[] tBase = PercentileAxis0(sub, 99.0);
        for (int c = 0; c < 3; c++) tBase[c] = Math.Max(tBase[c], 1e-6);

        int n = sub.Length;
        double clampD = Math.Pow(10.0, -dMax);
        var dMeanPx = new double[n];
        var dChroma = new double[n][];
        for (int i = 0; i < n; i++)
        {
            var d = new double[3];
            for (int c = 0; c < 3; c++)
                d[c] = -Math.Log10(Math.Max(sub[i][c] / tBase[c], clampD));
            double mean = (d[0] + d[1] + d[2]) / 3.0;
            dMeanPx[i] = mean;
            dChroma[i] = new[] { d[0] - mean, d[1] - mean, d[2] - mean };
        }

        double[] stdBefore = StdAxis0(dChroma);

        // Fraction of pixels whose reconstructed density (luminance + corrected chroma)
        // goes negative at blend strength a.
        double NegFracAt(double a)
        {
            double[,] mb = Blend(a, m);
            long neg = 0;
            for (int i = 0; i < n; i++)
            {
                double[] dc = MatVecT(mb, dChroma[i]);
                double mean = (dc[0] + dc[1] + dc[2]) / 3.0;
                for (int c = 0; c < 3; c++) if (dMeanPx[i] + (dc[c] - mean) < 0) neg++;
            }
            return (double)neg / (n * 3.0);
        }

        // Binary search for max alpha with neg_frac <= threshold.
        // Invariant: lo is always safe (neg_frac <= threshold), hi is not.
        double alpha;
        if (NegFracAt(1.0) <= negThreshold) alpha = 1.0;
        else
        {
            double lo = 0.0, hi = 1.0;
            for (int it = 0; it < 10; it++)
            {
                double mid = (lo + hi) / 2.0;
                if (NegFracAt(mid) <= negThreshold) lo = mid; else hi = mid;
            }
            alpha = lo;
        }
        alpha = Math.Round(alpha, 2, MidpointRounding.ToEven);

        double[,] mBlend = Blend(alpha, m);
        var dChromaCorr = new double[n][];
        for (int i = 0; i < n; i++)
        {
            double[] dc = MatVecT(mBlend, dChroma[i]);
            double mean = (dc[0] + dc[1] + dc[2]) / 3.0;
            dChromaCorr[i] = new[] { dc[0] - mean, dc[1] - mean, dc[2] - mean };
        }
        double[] stdAfter = StdAxis0(dChromaCorr);

        double ampSum = 0;
        for (int c = 0; c < 3; c++) ampSum += stdAfter[c] / Math.Max(stdBefore[c], 1e-10);
        return (alpha, Math.Max(ampSum / 3.0, 1.0));
    }

    /// <summary>
    /// Calibrate (alpha, chroma_amp, amp_per_channel) for the LINEAR-domain decouple path —
    /// port of compute_decouple_params_linear.
    ///
    /// The constraint lives in the DENSITY domain even though the matrix acts in the linear
    /// one: the linear matrix's small perturbations get amplified by -log10 in the shadows
    /// (0.05→0.005 in linear is a density jump of 1.0), which is exactly why floor-based
    /// constraints fail — they measure the wrong domain. So we measure how much the matrix
    /// widens the density-chroma extreme (per-channel 99.5th percentile of |chroma|) and
    /// binary-search the largest alpha keeping it within
    /// <paramref name="maxChromaRatio"/> × the pre-decouple baseline.
    /// </summary>
    public static (double Alpha, double ChromaAmp, double[] AmpPerChannel) ComputeDecoupleParamsLinear(
        double[,] m, ImageBuffer image, double dMax = 4.0, double maxChromaRatio = 1.5)
    {
        double[][] sub = Subsample(image, 8);
        double[] tBase = PercentileAxis0(sub, 99.0);
        for (int c = 0; c < 3; c++) tBase[c] = Math.Max(tBase[c], 1e-6);

        int n = sub.Length;
        var tNorm = new double[n][];
        for (int i = 0; i < n; i++)
            tNorm[i] = new[] { sub[i][0] / tBase[0], sub[i][1] / tBase[1], sub[i][2] / tBase[2] };

        const double Eps = 1e-4;   // note: NOT 10^-d_max — Python uses a fixed 1e-4 here

        double[][] chromaBefore = DensityChromaOf(tNorm, Eps);
        double[] baselineExtreme = PercentileAbsAxis0(chromaBefore, 99.5);
        var threshold = new double[3];
        for (int c = 0; c < 3; c++) threshold[c] = Math.Max(baselineExtreme[c], 1e-6) * maxChromaRatio;

        bool Exceeds(double a)
        {
            double[,] mb = Blend(a, m);
            var dec = new double[n][];
            for (int i = 0; i < n; i++) dec[i] = MatVecT(mb, tNorm[i]);
            double[] ext = PercentileAbsAxis0(DensityChromaOf(dec, Eps), 99.5);
            for (int c = 0; c < 3; c++) if (ext[c] > threshold[c]) return true;
            return false;
        }

        double alpha;
        if (!Exceeds(1.0)) alpha = 1.0;
        else
        {
            double lo = 0.0, hi = 1.0;
            for (int it = 0; it < 14; it++)
            {
                double mid = (lo + hi) / 2.0;
                if (!Exceeds(mid)) lo = mid; else hi = mid;
            }
            alpha = Math.Round(lo, 3, MidpointRounding.ToEven);
        }

        double[,] mEff = Blend(alpha, m);
        var decEff = new double[n][];
        for (int i = 0; i < n; i++) decEff[i] = MatVecT(mEff, tNorm[i]);
        double[][] chromaAfter = DensityChromaOf(decEff, Eps);

        double[] stdBefore = StdAxis0(chromaBefore);
        double[] stdAfter = StdAxis0(chromaAfter);
        var ampPerCh = new double[3];
        for (int c = 0; c < 3; c++)
            ampPerCh[c] = Math.Max(stdAfter[c] / Math.Max(stdBefore[c], 1e-10), 1.0);
        double chromaAmp = Math.Max((ampPerCh[0] + ampPerCh[1] + ampPerCh[2]) / 3.0, 1.0);
        return (alpha, chromaAmp, ampPerCh);
    }

    /// <summary>
    /// R/G/B channel assignment from per-file centre-ROI means — the content-based
    /// identification (argmax per channel) that lets users drop the three calibration RAWs
    /// in a directory under any filenames. Returns indices into <paramref name="vecs"/>.
    /// Throws when the three argmaxes collide, i.e. the light sources are not separable.
    /// </summary>
    public static (int R, int G, int B) IdentifyRgbIndices(IReadOnlyList<double[]> vecs)
    {
        int Argmax(int c)
        {
            int best = 0;
            for (int i = 1; i < vecs.Count; i++) if (vecs[i][c] > vecs[best][c]) best = i;
            return best;
        }
        int ir = Argmax(0), ig = Argmax(1), ib = Argmax(2);
        if (new HashSet<int> { ir, ig, ib }.Count < 3)
            throw new ArgumentException(
                "自动识别失败：三个通道的最强图像指向同一张或高度重叠，无法可靠区分 R/G/B 光源。\n" +
                "请检查校正图是否在纯红、纯绿、纯蓝光下分别拍摄，且目录内不含无关 RAW 文件。");
        return (ir, ig, ib);
    }

    /// <summary>Result of <see cref="AutoIdentifyRgbFiles"/> — paths plus the ROI stats a
    /// confirmation dialog shows before the assignment is committed.</summary>
    public sealed record RgbIdentifyResult(string R, string G, string B, double[][] Vecs, string[] FileNames);

    /// <summary>
    /// Scan <paramref name="calDir"/> for RAW files and identify which is the R / G / B
    /// light source by centre-ROI argmax — no renaming required. Files are probed on the
    /// same UniWB baseline as content frames (<see cref="RawDecode"/>); that shared colour
    /// reference is what makes the assignment meaningful.
    ///
    /// The probe is half-size and ROI-only (<see cref="RawDecode.RoiMeanProbe"/>), decoded in
    /// parallel: the calibration frames are flat single-colour fields, so argmax separation is
    /// decided by a huge per-channel margin that no amount of extra resolution can flip. The
    /// full-resolution decode is left to the matrix computation, which actually needs it.
    /// </summary>
    public static RgbIdentifyResult AutoIdentifyRgbFiles(string calDir)
    {
        string[] rawFiles = Directory.EnumerateFiles(calDir)
            .Where(p => RawDecode.IsRawExtension(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        if (rawFiles.Length < 3)
            throw new ArgumentException(
                $"校正图目录 {calDir} 中 RAW 文件不足 3 张（找到 {rawFiles.Length} 张）。\n" +
                "请确保目录内有且仅有拍摄红、绿、蓝光源的三张校正图。");

        var vecs = new double[rawFiles.Length][];
        // A few workers, NOT one per core. Each probe holds a live LibRaw context (unpacked
        // Bayer + demosaic working image), and this runs off the import dialog on a directory
        // the user picked — a folder with a dozen RAWs used to put a dozen decodes in flight at
        // once. Everything else that decodes goes through the GUI's single shared admission gate
        // (ImageIo.DecodeGate); this path sits in Core and cannot see it, so it carries the same
        // ceiling itself rather than silently multiplying the "safe" limit.
        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 3, 1, 3),
        };
        Parallel.For(0, rawFiles.Length, opts, i => vecs[i] = RawDecode.RoiMeanProbe(rawFiles[i]));
        (int r, int g, int b) = IdentifyRgbIndices(vecs);
        return new RgbIdentifyResult(rawFiles[r], rawFiles[g], rawFiles[b], vecs,
                                     rawFiles.Select(Path.GetFileName).ToArray()!);
    }

    /// <summary>Paths only — the thin wrapper over <see cref="AutoIdentifyRgbFiles"/>.</summary>
    public static (string R, string G, string B) FindRgbCalFiles(string calDir)
    {
        var res = AutoIdentifyRgbFiles(calDir);
        return (res.R, res.G, res.B);
    }

    // ── alpha/params helpers ──────────────────────────────────────────────────────

    /// <summary>M_alpha = (1-a)·I + a·M.</summary>
    private static double[,] Blend(double a, double[,] m)
    {
        var mb = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                mb[i, j] = (1.0 - a) * (i == j ? 1.0 : 0.0) + a * m[i, j];
        return mb;
    }

    /// <summary>One row of <c>pixels @ M.T</c>: out[j] = Σ_k v[k]·m[j,k].</summary>
    private static double[] MatVecT(double[,] m, double[] v) => new[]
    {
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    };

    /// <summary>image[::stride, ::stride].reshape(-1, 3) as float64.</summary>
    private static double[][] Subsample(ImageBuffer image, int stride)
    {
        int w = image.Width, h = image.Height;
        int rows = (h + stride - 1) / stride, cols = (w + stride - 1) / stride;
        var sub = new double[rows * cols][];
        float[] d = image.Data;
        int k = 0;
        for (int y = 0; y < h; y += stride)
            for (int x = 0; x < w; x += stride)
            {
                int i = (y * w + x) * 3;
                sub[k++] = new double[] { d[i], d[i + 1], d[i + 2] };
            }
        return sub;
    }

    /// <summary>Density chroma of rows: -log10(max(v, eps)) then subtract the per-row mean.</summary>
    private static double[][] DensityChromaOf(double[][] rows, double eps)
    {
        var outRows = new double[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            double d0 = -Math.Log10(Math.Max(rows[i][0], eps));
            double d1 = -Math.Log10(Math.Max(rows[i][1], eps));
            double d2 = -Math.Log10(Math.Max(rows[i][2], eps));
            double mean = (d0 + d1 + d2) / 3.0;
            outRows[i] = new[] { d0 - mean, d1 - mean, d2 - mean };
        }
        return outRows;
    }

    /// <summary>np.percentile(rows, q, axis=0) — per channel.</summary>
    private static double[] PercentileAxis0(double[][] rows, double q)
    {
        var res = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var col = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) col[i] = rows[i][c];
            res[c] = Percentile(col, q);
        }
        return res;
    }

    /// <summary>np.percentile(np.abs(rows), q, axis=0) — per channel.</summary>
    private static double[] PercentileAbsAxis0(double[][] rows, double q)
    {
        var res = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var col = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) col[i] = Math.Abs(rows[i][c]);
            res[c] = Percentile(col, q);
        }
        return res;
    }

    /// <summary>np.std(rows, axis=0) — population std per channel.</summary>
    private static double[] StdAxis0(double[][] rows)
    {
        var res = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double sum = 0;
            for (int i = 0; i < rows.Length; i++) sum += rows[i][c];
            double mean = sum / rows.Length;
            double acc = 0;
            for (int i = 0; i < rows.Length; i++) { double e = rows[i][c] - mean; acc += e * e; }
            res[c] = Math.Sqrt(acc / rows.Length);
        }
        return res;
    }

    // ── density-chroma statistics (shared fulcrum / luminance-removal pipeline) ────
    private static double[] AxisChromaStd(ImageBuffer img, bool[]? mask)
    {
        double[][] chroma = DensityChroma(img, mask, out int n);
        if (n == 0) return new[] { double.NaN, double.NaN };
        // Project onto (yb, rg), then population std of each coordinate.
        double sYb = 0, sRg = 0;
        var yb = new double[n]; var rg = new double[n];
        for (int k = 0; k < n; k++)
        {
            double c0 = chroma[0][k], c1 = chroma[1][k], c2 = chroma[2][k];
            yb[k] = c0 * ChromaYb[0] + c1 * ChromaYb[1] + c2 * ChromaYb[2];
            rg[k] = c0 * ChromaRg[0] + c1 * ChromaRg[1] + c2 * ChromaRg[2];
            sYb += yb[k]; sRg += rg[k];
        }
        return new[] { PopStd(yb, sYb / n), PopStd(rg, sRg / n) };
    }

    private static double[] DensityChromaStdPerChannel(ImageBuffer img, bool[]? mask)
    {
        double[][] chroma = DensityChroma(img, mask, out int n);
        if (n == 0) return new[] { double.NaN, double.NaN, double.NaN };
        var res = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double sum = 0;
            for (int k = 0; k < n; k++) sum += chroma[c][k];
            res[c] = PopStd(chroma[c], sum / n);
        }
        return res;
    }

    private static double DensityChromaStdPooled(ImageBuffer img, bool[]? mask)
    {
        double[][] chroma = DensityChroma(img, mask, out int n);
        if (n == 0) return double.NaN;
        // std over all 3N elements pooled (matches chroma.std()).
        double sum = 0;
        for (int c = 0; c < 3; c++) for (int k = 0; k < n; k++) sum += chroma[c][k];
        double mean = sum / (3.0 * n);
        double acc = 0;
        for (int c = 0; c < 3; c++) for (int k = 0; k < n; k++) { double e = chroma[c][k] - mean; acc += e * e; }
        return Math.Sqrt(acc / (3.0 * n));
    }

    // Returns density-domain chroma as 3 arrays of length n (kept pixels), matching
    // _density_chroma_*: exclude masked pixels, subsample to 200k, 99th-pct fulcrum,
    // density = -log10(t/fulcrum), chroma = density - per-pixel mean.
    private static double[][] DensityChroma(ImageBuffer img, bool[]? mask, out int n)
    {
        float[] d = img.Data;
        int total = img.PixelCount;

        // Gather kept pixel indices (mask == true means EXCLUDE).
        int keptCount = 0;
        for (int p = 0; p < total; p++) if (mask == null || !mask[p]) keptCount++;
        if (keptCount == 0) { n = 0; return new[] { System.Array.Empty<double>(), System.Array.Empty<double>(), System.Array.Empty<double>() }; }

        var kept = new int[keptCount];
        int ki = 0;
        for (int p = 0; p < total; p++) if (mask == null || !mask[p]) kept[ki++] = p;

        // Subsample to 200k via np.linspace(0, K-1, 200000).astype(intp) when K > 200k.
        int[] sel;
        if (keptCount > 200_000)
        {
            sel = new int[200_000];
            double step = (keptCount - 1) / (200_000 - 1.0);
            for (int i = 0; i < 200_000; i++) sel[i] = kept[(int)(i * step)];  // truncate toward zero
        }
        else sel = kept;
        n = sel.Length;

        var r = new double[n]; var g = new double[n]; var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            int px = sel[i] * 3;
            r[i] = d[px]; g[i] = d[px + 1]; b[i] = d[px + 2];
        }

        double fr = Math.Max(Percentile(r, 99.0), 1e-10);
        double fg = Math.Max(Percentile(g, 99.0), 1e-10);
        double fb = Math.Max(Percentile(b, 99.0), 1e-10);

        for (int i = 0; i < n; i++)
        {
            double d0 = -Math.Log10(Math.Max(r[i] / fr, 1e-10));
            double d1 = -Math.Log10(Math.Max(g[i] / fg, 1e-10));
            double d2 = -Math.Log10(Math.Max(b[i] / fb, 1e-10));
            double m = (d0 + d1 + d2) / 3.0;
            r[i] = d0 - m; g[i] = d1 - m; b[i] = d2 - m;
        }
        return new[] { r, g, b };
    }

    // ── small linear-algebra + numpy helpers ──────────────────────────────────────
    private static double[,] ColumnStack(double[] c0, double[] c1, double[] c2)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++) { m[i, 0] = c0[i]; m[i, 1] = c1[i]; m[i, 2] = c2[i]; }
        return m;
    }

    private static double[,] RowNormalisedInverse(double[,] mObs)
    {
        double det = Det3(mObs);
        if (Math.Abs(det) < 1e-12)
            throw new ArgumentException("decouple observation matrix near-singular — check R/G/B calibration frames");
        double[,] inv = Inv3(mObs, det);
        var outM = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            double rs = inv[i, 0] + inv[i, 1] + inv[i, 2];
            for (int j = 0; j < 3; j++) outM[i, j] = inv[i, j] / rs;
        }
        return outM;
    }

    private static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
      - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
      + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    private static double[,] Inv3(double[,] m, double det)
    {
        var inv = new double[3, 3];
        inv[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        inv[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        inv[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        inv[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        inv[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        inv[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        inv[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        inv[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        inv[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return inv;
    }

    private static double[,] Identity3() => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    // numpy order statistics live in NumpyStats (float32/float64 split documented there).
    private static double Percentile(double[] vals, double q) => NumpyStats.Percentile(vals, q);

    private static double PopStd(double[] a, double mean)
    {
        double acc = 0;
        for (int i = 0; i < a.Length; i++) { double e = a[i] - mean; acc += e * e; }
        return Math.Sqrt(acc / a.Length);
    }

    private static bool Finite(double[] v)
    {
        foreach (var x in v) if (double.IsNaN(x) || double.IsInfinity(x)) return false;
        return true;
    }
}
