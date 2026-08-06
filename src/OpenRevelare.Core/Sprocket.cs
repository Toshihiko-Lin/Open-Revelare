namespace OpenRevelare.Core;

/// <summary>
/// Sprocket-hole / light-board masking — port of negative/sprocket.py. The mask is
/// detected on the raw negative BEFORE inversion (holes/board are the brightest raw
/// pixels) and the masked pixels are filled white AFTER inversion.
///
/// Also holds the import-time auto-threshold estimators, which are histogram
/// valley searches over the same luma: <see cref="EstimateSprocketThreshold"/>
/// (bright end, board↔base) and <see cref="EstimateDarkValley"/> (dark end,
/// opaque mask↔picture). <see cref="FilmBase"/> consumes both.
/// </summary>
public static class Sprocket
{
    /// <summary>Sentinel from <see cref="EstimateSprocketThreshold"/>: no light-board / sprockets.</summary>
    public const double NoBoard = 0.99;

    /// <summary>Sentinel from <see cref="EstimateDarkValley"/>: no light-blocking mask — use everything.</summary>
    public const double NoMaskDark = 0.0;

    /// <summary>Detect mask: per-pixel mean luma &gt; threshold (matches image.mean(axis=2)).</summary>
    public static bool[] MakeMask(float[] data, int pixelCount, float threshold)
    {
        var mask = new bool[pixelCount];
        Parallel.For(0, pixelCount, p =>
        {
            int b = p * 3;
            float luma = (data[b] + data[b + 1] + data[b + 2]) / 3.0f;
            mask[p] = luma > threshold;
        });
        return mask;
    }

    /// <summary>Fill masked pixels with 1.0 (white in the positive), in place.</summary>
    public static void ApplyMask(float[] data, bool[] mask)
    {
        Parallel.For(0, mask.Length, p =>
        {
            if (mask[p])
            {
                int b = p * 3;
                data[b] = 1.0f; data[b + 1] = 1.0f; data[b + 2] = 1.0f;
            }
        });
    }

    /// <summary>
    /// Auto-estimate the absolute-luma sprocket threshold — the BRIGHT-END valley.
    ///
    /// The light-board is always brighter than the film base but need NOT be blown to
    /// ≈1.0: a low copy-stand exposure can put the board at ≈0.9 and the orange base at
    /// ≈0.2, with picture content filling the gap. So a FIXED cut misses the real gap and
    /// lets the board pollute T_base. Instead: board peak = tallest bin above luma 0.55;
    /// base peak = tallest bin left of it; threshold = the deepest valley between them.
    /// Scanning from the bright end is deliberate — a frame that copied true black has a
    /// SECOND valley down in the shadows, and we want the board↔base one, always the
    /// brighter of the two.
    /// </summary>
    /// <returns>The valley luma, or <see cref="NoBoard"/> (0.99) when no clear board/base
    /// two-peak structure exists (board absent, degenerate histogram). Callers treat
    /// &gt;= 0.99 as "no board".</returns>
    public static double EstimateSprocketThreshold(ImageBuffer image)
    {
        float[] luma = Luma(image);
        if (luma.Length < 100) return NoBoard;
        double[] smooth = Smooth7(Histogram256(luma));

        // 1. Board peak: tallest bin in the bright region (luma > 0.55).
        int hiLo = SearchSortedLeftCentres(0.55);
        if (hiLo >= smooth.Length) return NoBoard;
        int boardPk = ArgMax(smooth, hiLo, smooth.Length);

        // 2. Base peak: tallest bin to the LEFT of the board peak.
        if (boardPk <= 0) return NoBoard;
        int basePk = ArgMax(smooth, 0, boardPk);
        if (basePk >= boardPk) return NoBoard;

        // 3. Valley: deepest point between the two peaks.
        int valleyIdx = ArgMin(smooth, basePk, boardPk);
        double valleyLuma = Centre(valleyIdx);

        // The valley must be a genuine gap — notably shallower than both flanking peaks.
        // Otherwise the "two peaks" are one cluster (no board in frame) and we fall back.
        double boardVal = smooth[boardPk], baseVal = smooth[basePk], valleyVal = smooth[valleyIdx];
        if (boardVal <= 0 || baseVal <= 0) return NoBoard;
        if (valleyVal > 0.5 * Math.Min(boardVal, baseVal)) return NoBoard;

        return Math.Clamp(valleyLuma, 0.1, 0.99);
    }

    /// <summary>
    /// Auto-estimate the DARK-END valley separating a light-blocking mask (the opaque card
    /// / film-edge line a copy stand uses to block stray light) from the picture's own
    /// darkest highlights — the mirror of <see cref="EstimateSprocketThreshold"/>.
    ///
    /// On a negative the densest (darkest) pixels are the scene's brightest highlights
    /// (most light → most dye → most opaque). But an opaque mask is darker still, so "the
    /// darkest pixel in frame" is the mask, not the highlight auto-WB wants. Pixels at or
    /// below the returned valley are the mask; those just above it are genuine highlights.
    /// </summary>
    /// <returns>The valley luma, or <see cref="NoMaskDark"/> (0.0) when no clear
    /// mask/picture structure exists — no mask in frame, degenerate histogram, or the dark
    /// cluster failed the neutrality gate. Callers treat &lt;= 0.0 as "use everything".</returns>
    public static double EstimateDarkValley(ImageBuffer image)
    {
        float[] luma = Luma(image);
        if (luma.Length < 100) return NoMaskDark;
        double[] smooth = Smooth7(Histogram256(luma));

        // 1. Mask peak: the DARKEST significant peak, NOT the tallest bin in the dark
        //    region — the opaque mask sits near luma 0 but mid-tone content easily
        //    out-counts it, so "tallest bin below 0.45" would land on the picture. Scan
        //    from the dark end for the first local maximum clearing 1% of the global peak.
        int darkHi = SearchSortedLeftCentres(0.45);
        if (darkHi <= 1) return NoMaskDark;
        double floor = 0.01 * smooth.Max();
        int maskPk = -1;
        for (int i = 1; i < darkHi; i++)
        {
            if (smooth[i] > floor && smooth[i] >= smooth[i - 1] && smooth[i] >= smooth[i + 1])
            {
                maskPk = i;
                break;
            }
        }
        if (maskPk < 0) return NoMaskDark;

        // 2. Picture peak: tallest bin to the RIGHT of the mask peak (the darkest real
        //    picture tones — the highlight cluster we want to keep).
        if (maskPk >= smooth.Length - 1) return NoMaskDark;
        int picPk = maskPk + 1 + ArgMax(smooth, maskPk + 1, smooth.Length);
        if (picPk <= maskPk) return NoMaskDark;

        // 3. Valley: deepest point between the two peaks.
        int valleyIdx = ArgMin(smooth, maskPk, picPk);
        double valleyLuma = Centre(valleyIdx);

        double maskVal = smooth[maskPk], picVal = smooth[picPk], valleyVal = smooth[valleyIdx];
        if (maskVal <= 0 || picVal <= 0) return NoMaskDark;
        if (valleyVal > 0.5 * Math.Min(maskVal, picVal)) return NoMaskDark;

        // Neutrality gate: an opaque mask blocks all light equally, so it is NEUTRAL
        // (R≈G≈B); a real negative highlight is seen THROUGH the orange base and carries a
        // strong cast. A coloured dark cluster is therefore a highlight, not a mask — and
        // clipping it would throw away the very pixel auto-WB needs. >8% cast → not a mask.
        double s0 = 0, s1 = 0, s2 = 0;
        long n = 0;
        float[] d = image.Data;
        for (int p = 0; p < luma.Length; p++)
        {
            if (luma[p] > valleyLuma) continue;
            int i = p * 3;
            s0 += d[i]; s1 += d[i + 1]; s2 += d[i + 2];
            n++;
        }
        if (n == 0) return NoMaskDark;
        double m0 = s0 / n, m1 = s1 / n, m2 = s2 / n;
        double cast = (Math.Max(Math.Max(m0, m1), m2) - Math.Min(Math.Min(m0, m1), m2))
                      / Math.Max((m0 + m1 + m2) / 3.0, 1e-6);
        if (cast > 0.08) return NoMaskDark;

        return Math.Clamp(valleyLuma, 0.0, 0.9);
    }

    /// <summary>
    /// (board_level, filmbase_highlight) luma for the frame — the reference numbers an
    /// import dialog shows. Uses the same valley threshold as
    /// <see cref="EstimateSprocketThreshold"/> to split board from base, so the displayed
    /// numbers stay consistent with the auto cut. board_level is 0.0 when no board is found.
    /// </summary>
    public static (double BoardLevel, double FilmbaseHighlight) MeasureBoardAndFilmbase(ImageBuffer image)
    {
        double threshold = EstimateSprocketThreshold(image);
        float[] luma = Luma(image);

        if (threshold >= 0.99)
            return (0.0, Quantise(Percentile(luma, 99.0)));   // no board — sentinel came back

        var board = new List<float>();
        var nonBoard = new List<float>();
        foreach (float v in luma) (v > threshold ? board : nonBoard).Add(v);

        double boardLvl = board.Count > 0 ? Quantise(Median(board.ToArray())) : 0.0;
        // Film-base highlight = the brightest non-board pixels (the bare orange base, the
        // brightest thing once the board is excluded). p99 (not p99.5) avoids the thin
        // transition shoulder just below the valley.
        double filmbaseHi = nonBoard.Count > 0 ? Quantise(Percentile(nonBoard.ToArray(), 99.0)) : 0.0;
        return (boardLvl, filmbaseHi);
    }

    /// <summary>
    /// Index of the frame whose film-base highlight end is brightest — the frame the
    /// import-time threshold should be calibrated on. It is the worst case for false
    /// positives, so a line clearing its film base clears every other frame's too.
    /// Returns 0 for an empty list.
    /// </summary>
    public static int BrightestFilmbaseFrame(IReadOnlyList<ImageBuffer> images)
    {
        int bestIdx = 0;
        double bestVal = -1.0;
        for (int i = 0; i < images.Count; i++)
        {
            double filmbaseHi = MeasureBoardAndFilmbase(images[i]).FilmbaseHighlight;
            if (!double.IsNaN(filmbaseHi) && filmbaseHi > bestVal)
            {
                bestIdx = i;
                bestVal = filmbaseHi;
            }
        }
        return bestIdx;
    }

    // ── histogram helpers (numpy-exact) ───────────────────────────────────────────

    /// <summary>
    /// Per-pixel luma, float32 — matches numpy's <c>image.mean(axis=2)</c> on a float32
    /// array bit for bit: sequential ((r+g)+b) then /3, all in single precision. The
    /// float32 quantisation is load-bearing, not incidental: histogram binning below keys
    /// off these exact values.
    /// </summary>
    private static float[] Luma(ImageBuffer image)
    {
        var luma = new float[image.PixelCount];
        float[] d = image.Data;
        for (int p = 0; p < luma.Length; p++)
        {
            int i = p * 3;
            luma[p] = (d[i] + d[i + 1] + d[i + 2]) / 3.0f;
        }
        return luma;
    }

    /// <summary>
    /// numpy.histogram(luma, bins=256, range=(0,1)) — counts only.
    /// Reproduces numpy's uniform-bin fast path exactly: with float32 input the bin edges
    /// are float32 too, and i/256 and luma*256 are both exact there (pure power-of-two
    /// scaling), so truncating luma*256 in double gives numpy's index bit for bit. The ULP
    /// fix-ups numpy applies are then no-ops, but are kept so the correspondence is visible.
    /// Values outside [0,1] are dropped, as numpy drops out-of-range.
    /// </summary>
    private static double[] Histogram256(float[] luma)
    {
        var counts = new double[256];
        foreach (float a in luma)
        {
            if (a < 0.0f || a > 1.0f) continue;
            int idx = (int)((double)a * 256.0);
            if (idx == 256) idx = 255;
            if (a < Edge(idx)) idx--;
            else if (a >= Edge(idx + 1) && idx != 255) idx++;
            counts[idx]++;
        }
        return counts;
    }

    // Bin edges of linspace(0, 1, 257): exactly i/256. Centres: exactly (2i+1)/512.
    private static double Edge(int i) => i / 256.0;
    private static double Centre(int i) => (2 * i + 1) / 512.0;

    /// <summary>np.convolve(counts, np.ones(7)/7, mode="same") — zero-padded centred box mean.</summary>
    private static double[] Smooth7(double[] counts)
    {
        const double K = 1.0 / 7.0;
        var smooth = new double[counts.Length];
        for (int i = 0; i < counts.Length; i++)
        {
            double acc = 0;
            for (int t = -3; t <= 3; t++)
            {
                int j = i + t;
                if (j >= 0 && j < counts.Length) acc += counts[j] * K;
            }
            smooth[i] = acc;
        }
        return smooth;
    }

    // np.searchsorted(centres, v, side='left') over the 256 bin centres.
    private static int SearchSortedLeftCentres(double v)
    {
        int i = 0;
        while (i < 256 && Centre(i) < v) i++;
        return i;
    }

    // np.argmax / np.argmin over [lo, hi) — FIRST occurrence on ties, as numpy does.
    private static int ArgMax(double[] a, int lo, int hi)
    {
        int best = lo;
        for (int i = lo + 1; i < hi; i++) if (a[i] > a[best]) best = i;
        return best;
    }

    private static int ArgMin(double[] a, int lo, int hi)
    {
        int best = lo;
        for (int i = lo + 1; i < hi; i++) if (a[i] < a[best]) best = i;
        return best;
    }

    // numpy.percentile / numpy.median — the FLOAT32 overloads, because both return float32 for
    // a float32 input; that is also why the call sites Quantise. See NumpyStats.
    private static double Percentile(float[] vals, double q) => NumpyStats.Percentile(vals, q);

    private static double Median(float[] vals) => NumpyStats.Median(vals);

    private static double Quantise(double v) => (float)v;
}
