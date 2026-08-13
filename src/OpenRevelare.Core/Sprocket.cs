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

    /// <summary>
    /// Share of the global histogram peak a bin must clear to count as a cluster rather than
    /// scatter, in <see cref="TopCluster"/>. Low enough for a thin sliver of board, high enough
    /// that film grain and dust specks in the highlight tail are not "the topmost cluster".
    /// </summary>
    private const double ClusterFloor = 0.01;

    /// <summary>
    /// How shallow the valley below the board must be, as a share of the board peak, before the
    /// two are accepted as separate populations. This is what distinguishes a genuine gap (bare
    /// light source against film) from the smooth roll-off of a normally-exposed negative into
    /// its own highlights.
    /// </summary>
    private const double MaxValleyDepth = 0.20;

    /// <summary>
    /// Least ABSOLUTE channel spread (max−min of the per-channel means, in linear units) before a
    /// dark cluster's relative cast is believed to be a real colour cast rather than noise.
    ///
    /// Set above the channel scatter of a black scanner border — measured ≈0.001 on the samples
    /// here — and far below a genuine C-41 highlight seen through the orange base, whose channels
    /// separate by an order of magnitude more.
    /// </summary>
    private const double MinCastSpread = 0.01;

    /// <summary>
    /// Least share of the frame a light board must occupy to be believed.
    ///
    /// A board is a physical object in shot — on the copy-stand samples it runs from a thin ring
    /// to a third of the frame. Below this the "board" is the bright tail of the picture, and
    /// cutting there removes highlights rather than hardware. 0.2% is low enough for a sliver of
    /// panel showing past one edge of a 135 frame.
    /// </summary>
    private const double MinBoardShare = 0.002;

    /// <summary>
    /// Least luma gap between the board's peak and the valley below it.
    ///
    /// Expresses "the board stands clear of the film" — the board↔base boundary is a step, not a
    /// dip inside one continuous distribution. Without it a scan whose histogram merely thins out
    /// toward the highlights yields a cut in the middle of the picture.
    /// </summary>
    private const double MinBoardSeparation = 0.10;

    /// <summary>
    /// Share of a cluster's pixels that must be connected to the frame's edge before it is
    /// accepted as hardware (light board, blocking card, unlit surround) rather than picture.
    ///
    /// Hardware reaches the edge by construction, so its true share is ≈1; picture content is
    /// ≈0. The margin below 1 absorbs the ordinary case where a little genuine picture happens
    /// to fall on the same side of the cut as the hardware — dark shadow touching a black
    /// border, a blown highlight meeting the panel — without letting that decide the answer.
    /// </summary>
    private const double MinEdgeConnected = 0.70;

    /// <summary>
    /// Least peak luma for a bright cluster to be a light board rather than bare film base.
    ///
    /// A board is unattenuated light source and is exposed at or near clipping; base is that same
    /// light seen THROUGH the orange mask, which costs it most of a stop and then some. Measured
    /// bases here peak around 0.27-0.28 and boards around 0.93-0.95, so the two populations are
    /// separated by a factor of three and the exact bar is not load-bearing. Set low enough to
    /// still admit a deliberately dim board (the estimator's remarks cite one at 0.9) and a copy
    /// stand exposed conservatively.
    /// </summary>
    private const double MinBoardLuma = 0.55;

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

        // 1. The board cluster, identified by ITS OWN properties rather than by pairing it with a
        //    film-base peak. See the class remarks: a normally-exposed negative HAS no base peak,
        //    so requiring one is what made this fire on pictures.
        int boardPk = TopCluster(smooth, out int boardFoot);
        if (boardPk < 0) return NoBoard;

        // 2. The gap between the FILM's bright edge and the board's foot.
        //
        // Searched from the film upward, not from bin 0. Below the film the histogram is empty,
        // and an empty bin is the global minimum — so a search starting at 0 puts the cut under
        // everything in frame, the "board" becomes the whole picture and the tests below then
        // reject the frame outright. The film's own top edge is where the gap actually begins.
        int filmTop = FilmTop(smooth, boardFoot);
        if (filmTop < 0) return NoBoard;
        int valleyIdx = ArgMin(smooth, filmTop, boardFoot + 1);
        double valleyLuma = Centre(valleyIdx);

        // 3. Rejection tests. Steps 1-2 find SOMETHING on any histogram — every distribution has a
        //    top and a dip — so what follows is what stops this firing on a photograph.
        double total = 0;
        foreach (double c in smooth) total += c;
        if (total <= 0) return NoBoard;
        double boardShare = 0;
        for (int i = boardFoot + 1; i < smooth.Length; i++) boardShare += smooth[i];
        // (a) The board occupies a real share of the frame — it is a physical object in shot.
        if (boardShare < MinBoardShare * total) return NoBoard;

        // (b) The board stands CLEAR of the film. Both halves are needed: the brightness gap says
        //     the cluster is somewhere else on the scale, and the valley DEPTH says the two are
        //     genuinely separate populations rather than one distribution with a dip in it. A
        //     normally-exposed negative rolls off smoothly into its highlights, so it fails the
        //     depth test even where a wide shoulder passes the separation one.
        if (Centre(boardPk) - valleyLuma < MinBoardSeparation) return NoBoard;
        if (smooth[valleyIdx] > MaxValleyDepth * smooth[boardPk]) return NoBoard;

        // (c) The board lies at the EDGE of the frame — the one test a bright picture cannot pass,
        //     because the board is the thing the film is lying ON and can only show around the
        //     film's edges. On FS-76221534.tif a dark subject at luma 0.21 against bright sky at
        //     0.59 satisfies every photometric rule above; what gives it away is that 643k of
        //     those pixels sit in the frame's interior against 63k in the border band.
        if (!BorderDominant(luma, image.Width, image.Height, valleyLuma)) return NoBoard;

        // (d) The board is BARE LIGHT SOURCE, so it is bright in absolute terms — not merely the
        //     brightest thing present.
        //
        // Without this, the bare film-base rebate at the edge of a scan is mistaken for a board:
        // it is a separate cluster, it is at the edge, and it is the brightest thing in frame, so
        // it passes (a) through (c). On 图像 003c the base sliver at luma 0.28 was read as a board
        // and the cut placed at 0.178, which then removed the base itself from every statistic —
        // and, because the roll pass applies one cut to all frames, took the whole roll's t_base
        // with it (0.163, 0.094, 0.048 against a correct 0.397, 0.273, 0.155).
        //
        // The two are far apart on an absolute scale and cannot be confused once it is consulted:
        // a copy-stand board is exposed to clip or near it (0.93-0.95 on the synthetic cases here,
        // and the estimator's own remarks cite a dim board at 0.9), while base seen through the
        // orange mask cannot exceed roughly a third of that. Anything dimmer than this bar is
        // film, whatever else it looks like.
        if (Centre(boardPk) < MinBoardLuma) return NoBoard;

        return Math.Clamp(valleyLuma, 0.1, 0.99);
    }

    /// <summary>
    /// The brightest bin still holding film: the last populated bin below
    /// <paramref name="boardFoot"/>. The gap the board cut goes in starts here.
    /// </summary>
    /// <returns>Bin index, or -1 when nothing lies below the board at all.</returns>
    private static int FilmTop(double[] smooth, int boardFoot)
    {
        double mx = 0;
        foreach (double v in smooth) if (v > mx) mx = v;
        if (mx <= 0) return -1;
        // "Populated" relative to the frame, so grain scatter in an empty region does not count
        // as film. The same floor the cluster search uses, for the same reason.
        double floor = ClusterFloor * mx;
        for (int i = Math.Min(boardFoot, smooth.Length - 1); i >= 0; i--)
            if (smooth[i] > floor) return i;
        return -1;
    }

    /// <summary>
    /// The topmost significant cluster of <paramref name="smooth"/>: its peak bin, and via
    /// <paramref name="foot"/> the bin where the cluster's lower flank bottoms out.
    ///
    /// Scans from the BRIGHT end for the first local maximum clearing a floor, rather than taking
    /// the tallest bin above a fixed luma. The fixed-luma form ("tallest bin above 0.55") failed
    /// in both directions: on a 120 frame with a large board, the board's own shoulder outranked
    /// the film base one bin below its summit; on a boardless scan it simply returned the
    /// picture's highlight mode and the caller cut the picture in half. Which cluster is topmost
    /// is a property of the histogram, not of a constant.
    /// </summary>
    /// <returns>Peak bin index, or -1 when no cluster clears the floor.</returns>
    private static int TopCluster(double[] smooth, out int foot)
    {
        foot = 0;
        double mx = 0;
        foreach (double v in smooth) if (v > mx) mx = v;
        if (mx <= 0) return -1;
        double floor = ClusterFloor * mx;

        int n = smooth.Length;
        int pk = -1;
        for (int i = n - 2; i >= 1; i--)
        {
            if (smooth[i] > floor && smooth[i] >= smooth[i - 1] && smooth[i] >= smooth[i + 1])
            {
                pk = i;
                break;
            }
        }
        if (pk < 0) return -1;

        // Down the cluster's lower flank to its foot: the bin where the histogram stops falling
        // and starts rising again into whatever lies below the board.
        int f = pk;
        while (f > 0 && smooth[f - 1] <= smooth[f]) f--;
        foot = f;
        return pk;
    }

    /// <summary>
    /// True when the pixels above <paramref name="cut"/> are concentrated in the frame's border
    /// band rather than spread through its interior — the signature of a light board as opposed
    /// to a bright subject.
    ///
    /// Measured as a DENSITY ratio, not a raw count: the border band is a small fraction of the
    /// frame, so a plain count comparison would be biased toward the interior simply because the
    /// interior is bigger. Comparing "share of the band that is lit" against "share of the
    /// interior that is lit" is scale-free and holds for a thin sliver of panel and a wide one
    /// alike.
    /// </summary>
    /// <param name="below">False → pixels ABOVE <paramref name="cut"/> (a light board).
    /// True → pixels at or BELOW it (a blocking card / unlit surround).</param>
    private static bool BorderDominant(float[] luma, int w, int h, double cut, bool below = false)
    {
        // Flood from the frame's border inward through matching pixels. Connectivity, not a
        // fixed band: hardware touches the edge of the scan but its shape is not known in
        // advance — a full-width strip along one side, a ring on all four, a corner wedge. A
        // band test only recognises the ring, and reports a one-sided strip as picture because
        // half the band is film. What every real case shares is that the region REACHES the
        // frame's edge, while a subject in the photograph does not.
        int n = w * h;
        var seen = new bool[n];
        var stack = new Stack<int>();

        void Seed(int p) { if (!seen[p] && Match(luma[p])) { seen[p] = true; stack.Push(p); } }
        bool Match(float v) => below ? v <= cut : v > cut;

        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }

        long connected = 0;
        while (stack.Count > 0)
        {
            int p = stack.Pop();
            connected++;
            int px = p % w, py = p / w;
            if (px > 0) Seed(p - 1);
            if (px < w - 1) Seed(p + 1);
            if (py > 0) Seed(p - w);
            if (py < h - 1) Seed(p + w);
        }
        if (connected == 0) return false;

        // How much of the matching population is edge-connected. Hardware is essentially all of
        // it; a bright sky or a dark subject sitting in the picture is essentially none, and the
        // few pixels of it that happen to touch a corner cannot carry the rest.
        long matching = 0;
        for (int p = 0; p < n; p++) if (Match(luma[p])) matching++;
        return matching > 0 && (double)connected / matching >= MinEdgeConnected;
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
        //
        // ArgMax already returns an ABSOLUTE index into `smooth`, so its result is used as-is.
        // Re-adding the `maskPk + 1` offset (as this line once did) pushed picPk past the end of
        // the array whenever the picture peak sat in the upper half, and ArgMin(maskPk, picPk)
        // then indexed out of bounds — an outright crash, reached by any frame whose brightest
        // cluster is bright enough, e.g. a 120 negative with the light panel showing.
        if (maskPk >= smooth.Length - 1) return NoMaskDark;
        int picPk = ArgMax(smooth, maskPk + 1, smooth.Length);
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
        // clipping it would throw away the very pixel auto-WB needs.
        //
        // Judged on the RELATIVE cast AND an ABSOLUTE channel spread, because the relative
        // measure alone divides by a near-zero mean and stops meaning anything exactly where the
        // surround is blackest. A scanner's unlit border measured (0.0066, 0.0070, 0.0076) on
        // FS-76221528.tif — visually pure black, and 0.001 apart — which the ratio reports as a
        // 15% "cast", rejecting it as picture. The surround then stayed in every statistic: it
        // set D-max to 4.6 and dragged t_base to a neutral 0.72 instead of an orange base.
        // Requiring the spread to be absolutely large as well keeps the test meaningful at both
        // ends of the scale, since a genuine C-41 highlight separates its channels by far more
        // than sensor noise does.
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
        double spread = Math.Max(Math.Max(m0, m1), m2) - Math.Min(Math.Min(m0, m1), m2);
        double cast = spread / Math.Max((m0 + m1 + m2) / 3.0, 1e-6);
        if (cast > 0.08 && spread > MinCastSpread) return NoMaskDark;

        // Spatial gate, the mirror of the one on the bright end: a blocking card / unlit surround
        // is at the frame's EDGE, whereas a dark SUBJECT is not.
        //
        // The neutrality test above is necessary but not sufficient. It rejects a dark cluster
        // that carries the orange base's cast, but a genuinely neutral dark subject — a shadow
        // under an overhang, a black object, a night sky — passes it, and clipping there throws
        // away the densest real pixels. Since those pixels are precisely the scene's brightest
        // highlights, losing them is what sends D-max to an extreme: the endpoint is then read
        // off whatever survives instead of off the true highlight. Requiring the dark cluster to
        // live at the border keeps the card and the surround while leaving picture shadows alone.
        if (!BorderDominant(luma, image.Width, image.Height, valleyLuma, below: true))
            return NoMaskDark;

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
