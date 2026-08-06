namespace OpenRevelare.Core;

/// <summary>
/// Film-base / D_max / white-balance sampling — port of negative/film_base.py.
///
/// These produce the SCALAR + (3,) parameters the inversion consumes
/// (<see cref="FrameParams.TBase"/>, <see cref="FrameParams.DMax"/>, wb_high/wb_offset):
/// the caller samples them once from user-selected rects (or a whole roll) and then
/// feeds them to every frame.
///
/// Sampling ORDER matters: the inversion applies D_corr[c] = D[c]*wb_high[c] + wb_offset[c],
/// so wb_offset (additive, shadow end) must be sampled BEFORE wb_high (multiplicative,
/// highlight end) — wb_high is then solved with the offset folded in. Doing it the other
/// way makes the two fight each other. See the Python docstrings for the derivation.
/// </summary>
public static class FilmBase
{
    private const double Truncate = 4.0;

    /// <summary>
    /// Film-base transmittance from an unexposed (D_min) rect. The returned T_base
    /// encodes D_min removal AND shadow-end WB (the orange mask) at once.
    /// rect = (x, y, w, h) normalised to [0,1]. Returns (3,).
    /// </summary>
    public static double[] SampleTBase(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                       double blurSigma = 3.0)
    {
        double[] patch = BlurredPatch(image, rect, blurSigma, "Sampling", out int pw, out int ph);
        double[] tBase = PatchMean(patch, pw * ph);
        Quantise(tBase);
        if (tBase.Any(v => v <= 0))
            throw new ArgumentException("Sampled T_base contains non-positive values — check selection area");
        return tBase;
    }

    /// <summary>
    /// Maximum optical density (most opaque region) as the 99.9th density percentile,
    /// which suppresses dust / hot-pixel outliers.
    /// Must be called on the T_norm image (T / T_base), NOT on raw T.
    /// </summary>
    public static double DetectDMax(ImageBuffer image)
    {
        float[] d = image.Data;
        var density = new double[d.Length];
        for (int i = 0; i < d.Length; i++)
            density[i] = -Math.Log10(Math.Max(d[i], 1e-10));
        return Percentile(density, 99.9);
    }

    /// <summary>
    /// Scalar D_max from a fully-exposed (shadow) rect: per-channel MEAN density, then the
    /// max across channels so no channel clips. The blur already suppresses dust, so the
    /// typical pixel of the selected "darkest area" inverts to T_pos = 1.0 (white) — which
    /// is what the user picking that area expects. Highlight-end channel balance is
    /// wb_high's job, not this one.
    /// </summary>
    public static double SampleDMaxFromRect(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                            double[] tBase, double blurSigma = 3.0)
    {
        double[] meanD = RectMeanDensity(image, rect, tBase, blurSigma, "D_max sampling");
        return meanD.Max();
    }

    /// <summary>
    /// Highlight WB from a neutral rect in the HIGHLIGHTS. Solves wb_high so every channel of
    /// (D[c]*wb_high[c] + wb_offset[c]) is equal, anchored to the densest post-offset channel:
    ///   target = max_c(D[c] + off[c]);  wb_high[c] = (target - off[c]) / D[c].
    /// With wb_offset = 0 (white-light rolls) this reduces to wb_high[c] = max_d / D[c].
    /// Pass the ALREADY-SET wb_offset (null = zeros) — see the class remarks on order.
    /// </summary>
    public static double[] SampleWbHighFromRect(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                                double[] tBase, double[]? wbOffset = null, double blurSigma = 3.0)
    {
        double[] meanD = RectMeanDensity(image, rect, tBase, blurSigma, "WB sampling");
        if (meanD.Max() <= 0)
            // Report the numbers: "choose a denser area" is useless when the region IS picture and
            // the real cause is t_base. All three channels being non-positive means the patch is
            // more transmissive than the film base everywhere — on Path A that points at a t_base
            // sampled in the wrong place (or one whose channels the decouple matrix pushed down),
            // not at the rectangle. mean T per channel = t_base · 10^(−D).
            throw new ArgumentException(
                "采样区比片基还透光（三通道密度全 ≤ 0） · " +
                $"D = {meanD[0]:F3}, {meanD[1]:F3}, {meanD[2]:F3} · " +
                $"t_base = {tBase[0]:F4}, {tBase[1]:F4}, {tBase[2]:F4} · " +
                "若框的已是画面内容，多半是 t_base 偏暗，请重采片基");

        double[] off = wbOffset ?? new double[3];
        double target = Math.Max(Math.Max(meanD[0] + off[0], meanD[1] + off[1]), meanD[2] + off[2]);
        var wbHigh = new double[3];
        for (int c = 0; c < 3; c++) wbHigh[c] = (target - off[c]) / Math.Max(meanD[c], 1e-10);
        Quantise(wbHigh);
        return wbHigh;
    }

    /// <summary>
    /// Shadow WB (the Negadoctor additive offset) from a rect that should reproduce neutral in
    /// the positive's SHADOWS. Sample this FIRST (darktable's order: offset before illuminant).
    /// With wb_high = identity it is purely additive: wb_offset[c] = max_d - D[c] ≥ 0, raising
    /// every channel to the densest so the region inverts neutral.
    /// </summary>
    public static double[] SampleWbOffsetFromRect(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                                  double[] tBase, double[]? wbHigh = null, double blurSigma = 3.0)
    {
        double[] meanD = RectMeanDensity(image, rect, tBase, blurSigma, "WB-offset sampling");

        var sl = new double[3];
        for (int c = 0; c < 3; c++) sl[c] = meanD[c] * (wbHigh?[c] ?? 1.0);
        double mx = Math.Max(Math.Max(sl[0], sl[1]), sl[2]);
        var wbOffset = new double[3];
        for (int c = 0; c < 3; c++) wbOffset[c] = mx - sl[c];
        Quantise(wbOffset);
        return wbOffset;
    }

    /// <summary>
    /// Estimate T_base (D_min) across a whole roll — exclude the light-board, then take the
    /// brightest survivor. The bare orange film base is the brightest thing in frame ONCE the
    /// board/sprockets are gone (the board is brighter still, which is exactly why it must be
    /// cut first or it gets mistaken for the base). D_min is near-constant along a roll, so the
    /// per-channel MEDIAN of the per-frame picks is the stable consensus — immune to a single
    /// anomalous frame (all-black scene, light leak) that a max() would let dominate.
    ///
    /// Frames are expected already downsampled (~640px) by the caller; that is plenty for
    /// percentile statistics and keeps 7k×18k scans from taking minutes each.
    ///
    /// ⚠ DELIBERATE DIVERGENCE FROM negative/film_base.py — the per-frame pick is CO-SITED.
    /// Python took three INDEPENDENT per-channel percentiles, which draws R, G and B from three
    /// different pixels. The film base is one physical material, so its three channels must be
    /// read off the same place; independent extremes bake a spurious cast straight into the
    /// fulcrum every later density divides by. This is the same failure
    /// <see cref="HighlightDensityFromRoll"/> already guards against at the highlight end — see
    /// the comment there — and it is worse here, because t_base contaminates the whole roll
    /// rather than one white point. <see cref="CoSitedFilmBase"/> holds the replacement and the
    /// level-matching argument; the CLI's <c>t_base_roll</c> parity dump no longer matches the
    /// Python reference by design.
    /// </summary>
    /// <param name="sprocketThreshold">
    /// Bright-end valley luma (board↔base cut) from sprocket threshold estimation. Given → board
    /// pixels (luma &gt; threshold) are dropped and the 99th percentile of the rest is the base
    /// (p99, not 99.97, avoids the thin transition shoulder just below the valley). Null → no
    /// board in frame; pure-brightness mode uses the 99.99th percentile (frame highlights act as
    /// a pseudo-base).
    /// </param>
    /// <param name="valueImages">
    /// Optional, index-aligned with <paramref name="images"/>. Given → <paramref name="images"/>
    /// supplies ONLY the luma for the board mask (the raw domain where the threshold was
    /// calibrated) while the sampled VALUES come from here. This is how a Path-A decoupled roll
    /// gets a T_base living in the same post-decouple domain the inversion later divides by;
    /// sampling the base raw but dividing a decoupled image would mismatch the two. White-light
    /// rolls pass null.
    /// </param>
    public static double[] EstimateTBaseFromRoll(IReadOnlyList<ImageBuffer> images,
                                                 double? sprocketThreshold = null,
                                                 IReadOnlyList<ImageBuffer>? valueImages = null)
    {
        if (images.Count == 0)
            throw new ArgumentException("EstimateTBaseFromRoll: empty image list");
        IReadOnlyList<ImageBuffer> valImages = valueImages ?? images;

        var perFrame = new List<double[]>();
        int frames = Math.Min(images.Count, valImages.Count);
        for (int f = 0; f < frames; f++)
        {
            ImageBuffer img = images[f], val = valImages[f];
            int total = img.PixelCount;
            float[] s = img.Data, v = val.Data;

            // The board mask keys off the RAW frame, where the threshold was calibrated, but
            // both the ranking luma and the sampled values come from the value domain — the
            // same split HighlightDensityFromRoll uses, and required on Path A where the
            // decouple matrix moves the channels out from under a raw-domain rank.
            var keptLuma = new double[total];
            var keptRgb = new float[total * 3];
            int kept = 0;
            double pct = sprocketThreshold is null ? 99.99 : 99.0;
            for (int p = 0; p < total; p++)
            {
                int i = p * 3;
                if (sprocketThreshold is double thr)
                {
                    double maskLuma = ((double)s[i] + s[i + 1] + s[i + 2]) / 3.0;
                    if (maskLuma > thr) continue;
                }
                int k = kept * 3;
                keptRgb[k] = v[i]; keptRgb[k + 1] = v[i + 1]; keptRgb[k + 2] = v[i + 2];
                keptLuma[kept] = ((double)v[i] + v[i + 1] + v[i + 2]) / 3.0;
                kept++;
            }
            if (kept == 0) continue;
            if (CoSitedFilmBase(keptLuma, keptRgb, kept, pct) is { } framePick)
                perFrame.Add(framePick);
        }

        if (perFrame.Count == 0)
            throw new ArgumentException("EstimateTBaseFromRoll: all pixels were masked out");

        var tBase = new double[3];
        for (int c = 0; c < 3; c++)
            tBase[c] = Median(perFrame.Select(x => x[c]).ToArray());
        Quantise(tBase);
        if (tBase.Any(x => x <= 0))
            throw new ArgumentException("EstimateTBaseFromRoll: estimated T_base contains non-positive values");
        return tBase;
    }

    /// <summary>
    /// One frame's film base: the per-channel mean of its brightest CO-SITED pixels — rank every
    /// kept pixel by luma, take the bright tail, average the three channels over exactly those
    /// pixels. All three components therefore come from the same physical patch of bare base.
    ///
    /// The tail is 2×(100−pct)% deep rather than the point value at pct. For a tail that is
    /// roughly linear — which the film base, being one near-uniform material, is — the mean of
    /// the top 2q% sits at the (100−q)th percentile. That doubling is what stops the switch
    /// from a point percentile to a tail MEAN from shifting the level on its own; without it
    /// the estimate drifts brighter and every downstream density shifts with it.
    ///
    /// Ranking by luma rather than per channel does still move the result, downward: the luma
    /// of a noisy base averages three independent channel noises, so the selected tail sits
    /// ~1/√3 as far above the true base as a per-channel tail would. That is a gain, not a
    /// residual — it is inflation the old estimator was baking in. On a synthetic base of
    /// (0.70, 0.42, 0.20) with σ=0.02 noise, a clipped board and a red object brighter in R
    /// than the base: independent percentiles returned (0.964, 0.458, 0.344), this returns
    /// (0.723, 0.443, 0.223) — the remaining lift is uniform across channels, so it costs a
    /// ~0.013 density offset and no cast.
    /// </summary>
    /// <returns>The (3,) base, or null when the guard rejected every sample.</returns>
    private static double[]? CoSitedFilmBase(double[] luma, float[] rgb, int count, double pct)
    {
        var keys = new double[count];
        Array.Copy(luma, keys, count);
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        Array.Sort(keys, order);          // ascending — the film base is the BRIGHT end

        double tailFraction = Math.Max(100.0 - pct, 0.0) / 100.0;
        int tailCount = Math.Clamp((int)Math.Ceiling(count * tailFraction * 2.0), 1, count);

        // Spike guard, ported from NexFilm's density_histogram_extremes. Its published
        // constants (skip a bin holding >10% of samples while under 20% accumulated) are
        // stated against a 1% tail, i.e. 10× and 20× the tail depth — expressed that way they
        // carry over to the 0.01% no-board branch, where a fixed 10% would never fire.
        double spike = count * tailFraction * 10.0;
        double guard = count * tailFraction * 20.0;

        // No-guard retry: if a frame is degenerate enough that the guard ate the whole tail,
        // a plateau-contaminated base still beats no base at all.
        return BrightTailMean(keys, order, rgb, count, tailCount, spike, guard)
            ?? BrightTailMean(keys, order, rgb, count, tailCount, double.PositiveInfinity, 0.0);
    }

    /// <summary>
    /// Per-channel mean of the <paramref name="tailCount"/> brightest pixels, walking the
    /// luma-sorted order downward and SKIPPING any plateau of identical quantised luma that
    /// alone supplies more than <paramref name="spike"/> samples while fewer than
    /// <paramref name="guard"/> have been passed. A clipped light-board, a blown specular or a
    /// flat-filled border occupies one level and would otherwise BE the entire tail; real base,
    /// carrying grain and an illumination gradient, spreads across levels and survives.
    /// Skipped plateaus contribute to neither the mean nor the passed count.
    /// </summary>
    private static double[]? BrightTailMean(double[] keys, int[] order, float[] rgb, int count,
                                            int tailCount, double spike, double guard)
    {
        var sum = new double[3];
        int taken = 0;
        double passed = 0;
        int i = count - 1;
        while (i >= 0 && taken < tailCount)
        {
            long level = QuantiseLuma(keys[i]);
            int j = i;
            while (j >= 0 && QuantiseLuma(keys[j]) == level) j--;
            int run = i - j;                              // the plateau occupies (j, i]

            if (run > spike && passed < guard) { i = j; continue; }

            for (int k = i; k > j && taken < tailCount; k--)
            {
                int b = order[k] * 3;
                sum[0] += rgb[b]; sum[1] += rgb[b + 1]; sum[2] += rgb[b + 2];
                taken++;
            }
            passed += run;
            i = j;
        }
        if (taken == 0) return null;
        return new[] { sum[0] / taken, sum[1] / taken, sum[2] / taken };
    }

    /// <summary>
    /// 16-bit levels — the granularity the scans actually carry, and the analogue of NexFilm's
    /// 65536-bin histogram. Everything at or above 1.0 (clipping, and any post-decouple
    /// overshoot) collapses into the top level, which is precisely the plateau the guard exists
    /// to drop.
    /// </summary>
    private static long QuantiseLuma(double luma) => (long)(Math.Clamp(luma, 0.0, 1.0) * 65535.0);

    /// <summary>
    /// Auto-estimate highlight WB by finding the roll's brightest highlight — the negative's
    /// DENSEST (darkest) real picture pixel — and solving wb_high so that point inverts to
    /// neutral white. Physics: on a negative the densest pixel is the scene's brightest
    /// highlight (most light → most dye → most opaque); assuming it should reproduce as
    /// neutral white, we balance the channels there. This is the auto counterpart to
    /// box-selecting a neutral highlight for <see cref="SampleWbHighFromRect"/> — only the
    /// region is found automatically, and the final solve is the same flat-channel rule so
    /// auto and manual agree.
    ///
    /// Per frame the pick is guarded against three contaminants that are denser/brighter
    /// than any real picture tone: the light-board / sprockets (bright end, cut by
    /// <paramref name="sprocketThreshold"/>), the opaque mask card / film-edge line (dark
    /// end, cut by <see cref="Sprocket.EstimateDarkValley"/>), and the film-edge line at the
    /// border (cut by <paramref name="edgeInset"/>).
    ///
    /// Aggregation: each frame contributes its highlight; the roll's highlight is the frame
    /// whose highlight is DENSEST — the truest "brightest scene point across the whole roll".
    /// </summary>
    /// <param name="images">Raw-domain (pre-decouple) frames — used for the luma masks, where
    /// sprocketThreshold and the dark valley are calibrated.</param>
    /// <param name="tBase">The film-base fulcrum (img / t_base → density), the same array the
    /// inversion divides by.</param>
    /// <param name="wbOffset">Already-set additive shadow WB, folded into the solve so both
    /// ends stay neutral together (mirrors <see cref="SampleWbHighFromRect"/>).</param>
    /// <param name="valueImages">Optional, index-aligned with <paramref name="images"/>. Given →
    /// masks key off <paramref name="images"/> (raw luma) but the sampled VALUES come from here
    /// (the post-decouple domain on Path A) — the same convention
    /// <see cref="EstimateTBaseFromRoll"/> uses. Null → values come from the frames themselves.</param>
    public static double[] AutoWbHighFromRoll(IReadOnlyList<ImageBuffer> images,
                                              double[] tBase,
                                              double[]? wbOffset = null,
                                              double? sprocketThreshold = null,
                                              IReadOnlyList<ImageBuffer>? valueImages = null,
                                              double edgeInset = 0.05,
                                              double highlightPct = 99.5)
    {
        double[] bestDensity = HighlightDensityFromRoll(images, tBase, sprocketThreshold,
                                                        valueImages, edgeInset, highlightPct);

        // Same flat-channel rule as SampleWbHighFromRect, so auto and manual agree.
        double[] off = wbOffset ?? new double[3];
        double target = Math.Max(Math.Max(bestDensity[0] + off[0], bestDensity[1] + off[1]), bestDensity[2] + off[2]);
        var wbHigh = new double[3];
        for (int c = 0; c < 3; c++) wbHigh[c] = (target - off[c]) / Math.Max(bestDensity[c], 1e-10);
        Quantise(wbHigh);
        return wbHigh;
    }

    /// <summary>
    /// The roll's highlight-end density vector: the per-channel density of the ONE physical
    /// highlight <see cref="AutoWbHighFromRoll"/> balances on, with the same masking (light-board
    /// dilation, dark valley, edge inset, opaque-edge rejection) and the same same-source pick.
    ///
    /// Split out because the Deep-WB solve needs exactly this vector as its anchor — both for its
    /// geometric starting wb_high and as the divisor that turns the net's log-gains into a
    /// density-slope delta. Sharing it is what keeps 智能白平衡 and 自动亮部 WB from starting in
    /// two different places; a private per-channel percentile is precisely the failure this
    /// method's same-source pick exists to avoid (see the comment inside).
    /// </summary>
    public static double[] HighlightDensityFromRoll(IReadOnlyList<ImageBuffer> images,
                                                    double[] tBase,
                                                    double? sprocketThreshold = null,
                                                    IReadOnlyList<ImageBuffer>? valueImages = null,
                                                    double edgeInset = 0.05,
                                                    double highlightPct = 99.5)
    {
        if (images.Count == 0)
            throw new ArgumentException("AutoWbHighFromRoll: empty image list");
        IReadOnlyList<ImageBuffer> valImages = valueImages ?? images;

        var tb = new double[3];
        for (int c = 0; c < 3; c++) tb[c] = Math.Max(tBase[c], 1e-10);

        double[]? bestDensity = null;
        double bestMeanD = double.NegativeInfinity;

        int frames = Math.Min(images.Count, valImages.Count);
        for (int f = 0; f < frames; f++)
        {
            ImageBuffer img = images[f], val = valImages[f];
            int h = img.Height, w = img.Width;

            // Edge inset: crop each border inward to drop the film-edge line.
            int yi = RoundHalfEven(h * edgeInset), xi = RoundHalfEven(w * edgeInset);
            if (h - 2 * yi < 4 || w - 2 * xi < 4) { yi = 0; xi = 0; }   // too small to inset
            int cw = w - 2 * xi, ch = h - 2 * yi;

            ImageBuffer inset = Crop(img, xi, yi, cw, ch);
            ImageBuffer insetVal = ReferenceEquals(img, val) ? inset : Crop(val, xi, yi, cw, ch);

            // NOTE: float64 luma here, unlike Sprocket's float32 one — Python does
            // .astype(np.float64).mean(axis=2) for the masks but hands the float32 frame to
            // estimate_dark_valley, which computes its own float32 luma internally. Two
            // different precisions on purpose; keep both.
            int n = cw * ch;
            var luma = new double[n];
            float[] id = inset.Data;
            for (int p = 0; p < n; p++)
                luma[p] = ((double)id[p * 3] + id[p * 3 + 1] + id[p * 3 + 2]) / 3.0;

            var keep = new bool[n];
            Array.Fill(keep, true);

            // 1. Bright end: drop light-board / sprockets, DILATED outward ~5%. The bare cut
            //    catches the sprocket's transmissive core, but the soft TRANSITION ring
            //    between that core and the opaque black frame edge sits below the cut — and
            //    those pixels are what slam the density into the -log10 clamp and poison the
            //    white point. Dilating by ~5% of the short edge swallows the ring.
            if (sprocketThreshold is double thr)
            {
                var board = new bool[n];
                bool any = false;
                for (int p = 0; p < n; p++) if (luma[p] > thr) { board[p] = true; any = true; }
                if (any)
                {
                    int radius = Math.Max(1, RoundHalfEven(Math.Min(ch, cw) * 0.05));
                    board = Dilate(board, cw, ch, radius);
                }
                for (int p = 0; p < n; p++) if (board[p]) keep[p] = false;
            }
            // 2. Dark end: drop the opaque mask / edge line. The valley is computed on the
            //    RAW inset frame, where it is calibrated; <= 0 means "no mask" → keep all.
            double darkValley = Sprocket.EstimateDarkValley(inset);
            if (darkValley > 0.0)
                for (int p = 0; p < n; p++) if (!(luma[p] > darkValley)) keep[p] = false;

            int keptCount = 0;
            for (int p = 0; p < n; p++) if (keep[p]) keptCount++;
            if (keptCount == 0) continue;

            // Density of every kept pixel relative to t_base; the highlight is the densest
            // end. The white point must come from ONE physical highlight, so pick pixels by
            // LUMA (total density = brightest scene point) and take their per-channel MEAN —
            // a same-source white point. Taking a per-channel percentile independently would
            // draw R, G and B from three DIFFERENT pixels; on an RGB-decouple roll the matrix
            // systematically lifts one channel's density, so that channel's independent
            // extreme is inflated and gets locked as the wb_high base, leaving the positive's
            // highlights cast ("white clouds look yellow"). Python hit exactly this.
            var dens = new double[keptCount * 3];
            var totalD = new double[keptCount];
            float[] vd = insetVal.Data;
            int k = 0;
            for (int p = 0; p < n; p++)
            {
                if (!keep[p]) continue;
                double sum = 0;
                for (int c = 0; c < 3; c++)
                {
                    double dc = -Math.Log10(Math.Max(vd[p * 3 + c] / tb[c], 1e-10));
                    dens[k * 3 + c] = dc;
                    sum += dc;
                }
                totalD[k] = sum / 3.0;
                k++;
            }

            // Reject opaque sprocket / film-frame BLACK edges before picking the highlight.
            // These are fully light-blocking (t_norm → 0), so their density slams into the
            // -log10 clamp far above any real picture tone (~6–10 vs a true highlight
            // ~1–1.5). Both the bright cut and the dark valley miss them on rolls where the
            // user kept the sprockets in frame and the valley returned its no-op sentinel —
            // and "pick max density" then locks onto dead black instead of the highlight.
            const double MaxRealDensity = 3.0;
            int realCount = 0;
            for (int i = 0; i < keptCount; i++) if (totalD[i] < MaxRealDensity) realCount++;
            if (realCount > 0 && realCount < keptCount)
            {
                var d2 = new double[realCount * 3];
                var t2 = new double[realCount];
                int j = 0;
                for (int i = 0; i < keptCount; i++)
                {
                    if (!(totalD[i] < MaxRealDensity)) continue;
                    d2[j * 3] = dens[i * 3]; d2[j * 3 + 1] = dens[i * 3 + 1]; d2[j * 3 + 2] = dens[i * 3 + 2];
                    t2[j] = totalD[i];
                    j++;
                }
                dens = d2; totalD = t2; keptCount = realCount;
            }

            double thresh = Percentile(totalD, highlightPct);
            double[]? hiD = MeanOfRowsAtOrAbove(dens, totalD, keptCount, thresh)
                         ?? MeanOfRowsAtOrAbove(dens, totalD, keptCount, Percentile(totalD, highlightPct - 1.0));
            if (hiD is null) continue;

            double meanD = (hiD[0] + hiD[1] + hiD[2]) / 3.0;
            if (meanD > bestMeanD) { bestMeanD = meanD; bestDensity = hiD; }
        }

        if (bestDensity is null)
            throw new ArgumentException("AutoWbHighFromRoll: all pixels were masked out");
        return bestDensity;
    }

    /// <summary>Per-channel mean of the rows whose total density is &gt;= thresh; null if none.</summary>
    private static double[]? MeanOfRowsAtOrAbove(double[] dens, double[] totalD, int count, double thresh)
    {
        var sum = new double[3];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (totalD[i] < thresh) continue;
            sum[0] += dens[i * 3]; sum[1] += dens[i * 3 + 1]; sum[2] += dens[i * 3 + 2];
            n++;
        }
        if (n == 0) return null;
        return new[] { sum[0] / n, sum[1] / n, sum[2] / n };
    }

    private static ImageBuffer Crop(ImageBuffer src, int x0, int y0, int cw, int ch)
    {
        var outImg = new ImageBuffer(cw, ch);
        float[] s = src.Data, o = outImg.Data;
        for (int y = 0; y < ch; y++)
            Array.Copy(s, ((y0 + y) * src.Width + x0) * 3, o, y * cw * 3, cw * 3);
        return outImg;
    }

    /// <summary>
    /// scipy.ndimage.binary_dilation(mask, iterations=radius) with the default structure —
    /// the 4-neighbour cross, so N iterations reach every pixel within MANHATTAN distance N.
    /// Computed as an exact city-block distance transform (two chamfer passes, O(n)) rather
    /// than N dilation passes, which would be O(n·N).
    /// </summary>
    private static bool[] Dilate(bool[] mask, int w, int h, int radius)
    {
        const int Inf = int.MaxValue / 4;
        var dist = new int[w * h];
        for (int i = 0; i < dist.Length; i++) dist[i] = mask[i] ? 0 : Inf;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (y > 0) dist[i] = Math.Min(dist[i], dist[i - w] + 1);
                if (x > 0) dist[i] = Math.Min(dist[i], dist[i - 1] + 1);
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (y < h - 1) dist[i] = Math.Min(dist[i], dist[i + w] + 1);
                if (x < w - 1) dist[i] = Math.Min(dist[i], dist[i + 1] + 1);
            }

        var outMask = new bool[w * h];
        for (int i = 0; i < outMask.Length; i++) outMask[i] = dist[i] <= radius;
        return outMask;
    }

    // ── shared rect → blurred patch → density pipeline ────────────────────────────

    /// <summary>Per-channel mean density of a blurred rect, relative to t_base. Shape (3,).</summary>
    private static double[] RectMeanDensity(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                            double[] tBase, double blurSigma, string what)
    {
        double[] patch = BlurredPatch(image, rect, blurSigma, what, out int pw, out int ph);
        int n = pw * ph;
        var tb = new double[3];
        for (int c = 0; c < 3; c++) tb[c] = Math.Max(tBase[c], 1e-10);

        var sum = new double[3];
        for (int p = 0; p < n; p++)
            for (int c = 0; c < 3; c++)
                sum[c] += -Math.Log10(Math.Max(patch[p * 3 + c] / tb[c], 1e-10));
        return new[] { sum[0] / n, sum[1] / n, sum[2] / n };
    }

    /// <summary>
    /// Crop the normalised rect (numpy semantics: banker's-rounded bounds clipped to the image)
    /// and Gaussian-blur each channel in double precision, matching the Python float64 path.
    /// </summary>
    private static double[] BlurredPatch(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                         double blurSigma, string what, out int pw, out int ph)
    {
        int h = image.Height, w = image.Width;
        int x0 = Math.Max(0, RoundHalfEven(rect.X * w));
        int y0 = Math.Max(0, RoundHalfEven(rect.Y * h));
        int x1 = Math.Min(w, RoundHalfEven((rect.X + rect.W) * w));
        int y1 = Math.Min(h, RoundHalfEven((rect.Y + rect.H) * h));
        if (x1 <= x0 || y1 <= y0)
            throw new ArgumentException(
                $"{what} rect ({rect.X},{rect.Y},{rect.W},{rect.H}) yields empty region for image {w}×{h}");

        pw = x1 - x0; ph = y1 - y0;
        var patch = new double[pw * ph * 3];
        float[] d = image.Data;
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                for (int c = 0; c < 3; c++)
                    patch[(y * pw + x) * 3 + c] = d[((y0 + y) * w + (x0 + x)) * 3 + c];

        BlurEachChannel(patch, pw, ph, blurSigma);
        return patch;
    }

    private static double[] PatchMean(double[] patch, int n)
    {
        var sum = new double[3];
        for (int p = 0; p < n; p++) { sum[0] += patch[p * 3]; sum[1] += patch[p * 3 + 1]; sum[2] += patch[p * 3 + 2]; }
        return new[] { sum[0] / n, sum[1] / n, sum[2] / n };
    }

    // ── scipy-compatible separable Gaussian (mode='reflect', truncate=4.0), float64 ──
    private static void BlurEachChannel(double[] data, int w, int h, double sigma)
    {
        double[] kernel = GaussianKernel1D(sigma);
        int r = kernel.Length / 2;
        var plane = new double[w * h];
        var tmp = new double[w * h];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < plane.Length; i++) plane[i] = data[i * 3 + c];
            Parallel.For(0, h, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double acc = 0;
                    for (int t = -r; t <= r; t++)
                        acc += plane[row + Reflect(x + t, w)] * kernel[t + r];
                    tmp[row + x] = acc;
                }
            });
            Parallel.For(0, w, x =>
            {
                for (int y = 0; y < h; y++)
                {
                    double acc = 0;
                    for (int t = -r; t <= r; t++)
                        acc += tmp[Reflect(y + t, h) * w + x] * kernel[t + r];
                    plane[y * w + x] = acc;
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

    // ── numpy helpers ─────────────────────────────────────────────────────────────

    // Python's round() is half-to-even, and so is Math.Round's default — keep both.
    private static int RoundHalfEven(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    // numpy order statistics live in NumpyStats (float32/float64 split documented there).
    private static double Percentile(double[] vals, double q) => NumpyStats.Percentile(vals, q);

    private static double Median(double[] vals) => NumpyStats.Median(vals);

    // Python returns these as float32; round-trip so the caller sees the same value it would.
    private static void Quantise(double[] v)
    {
        for (int i = 0; i < v.Length; i++) v[i] = (float)v[i];
    }
}
