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
    /// Film base as the brightest DENSE MODE of the frame's luma, rather than as a bright-tail
    /// percentile. Co-sited: the window is chosen on luma, and all three channels are averaged
    /// over exactly the pixels inside it.
    ///
    /// Why a mode and not a tail. Every percentile estimator here — including NexFilm's
    /// <c>compute_auto_base</c>, which this originally followed — assumes the brightest pixels
    /// ARE the base. On a copy-stand negative that assumption fails, because the light board's
    /// transition shoulder outlives the board cut: the sprocket threshold removes the board's
    /// core, but its penumbra bleeds into the frame edges and is still far brighter than the
    /// base. On the measured sample the non-board luma histogram held a dense base peak of
    /// 126637 pixels at luma ≈0.14 that fell off a cliff to ~168 per bin above 0.158, and every
    /// tail estimator landed in that thin scatter instead of on the peak:
    ///
    ///   p99.99 → 0.364, 0.637, 0.371   (R/G 0.571 — green-dominant, physically impossible)
    ///   p99    → 0.232, 0.308, 0.165   (R/G 0.752)
    ///   p95    → 0.207, 0.203, 0.082   (R/G 1.024)
    ///   mode   → 0.198, 0.174, 0.060   (R/G 1.142)
    ///   manual → 0.200, 0.175, 0.060   (R/G 1.143)
    ///
    /// Lowering the percentile only walks toward the mode asymptotically and never reaches it,
    /// because the contamination is a gradient, not an outlier count — no percentile is both
    /// low enough to clear the shoulder and high enough to still mean "base".
    ///
    /// The mode is also what makes this robust: bare base is one near-uniform material covering
    /// a large, contiguous area, so it is the single densest thing in the histogram by a wide
    /// margin. Picture content spreads; the base piles up. Widening the averaging window 3×
    /// moved R/G by 0.002 on the sample, so the result is not tuned to a window choice.
    ///
    /// Requires a board cut, and returns null without one. The mode is only the base on a frame
    /// where the base is bounded from above by something brighter that has just been removed —
    /// take the board away and the brightest dense mode is picture content, not base. Measured on
    /// a synthetic no-board negative with a true base of (0.700, 0.420, 0.200), the mode returned
    /// (0.493, 0.296, 0.141): a uniform ~30% underestimate, i.e. a base sitting inside the
    /// picture's own tone distribution. The bright-tail estimator is right for that case and
    /// <see cref="EstimateTBaseFromRoll"/>'s no-board branch already handles it, so callers fall
    /// back to it rather than this.
    /// </summary>
    /// <param name="image">Frame to measure — the luma domain the board cut was calibrated in.</param>
    /// <param name="sprocketThreshold">Board cut; pixels above it are dropped before the
    /// histogram is built. Null → returns null (see above).</param>
    /// <param name="valueImage">Optional post-decouple buffer supplying the averaged VALUES while
    /// <paramref name="image"/> still supplies the luma. Same split as
    /// <see cref="EstimateTBaseFromRoll"/>'s valueImages, and required on Path A.</param>
    /// <returns>The (3,) base, or null when no mode cleared the density floor.</returns>
    /// <summary>
    /// Film base from a thin BARE-BASE SLIVER at the frame's edge, on a scan with no light board
    /// and no blocking card — the case both other estimators miss.
    ///
    /// A scan trimmed close to the picture can still include a millimetre of unexposed rebate
    /// along one or two edges. That sliver is the real film base and it is the brightest thing on
    /// the negative, but it is far too small for a percentile to find: measured on 图像 001a it is
    /// 0.74% of the frame, so the 99.99th percentile used by <see cref="EstimateTBaseFromRoll"/>'s
    /// no-board branch lands inside it only by accident and the 99th lands in picture highlights.
    /// The returned base then comes back roughly 2.5× too dense (0.43, 0.29, 0.17 against a true
    /// 0.20, 0.12, 0.06), which propagates into every density downstream.
    ///
    /// <see cref="EstimateTBaseByMode"/> cannot help either: it needs a board cut to bound the
    /// base from above, and refuses without one for the reason given in its own remarks.
    ///
    /// What identifies the sliver is that it is a SEPARATE CLUSTER — a second peak above the
    /// picture's distribution with a genuine valley between them — that also sits at the frame's
    /// EDGE and is ORANGE. All three tests are required:
    ///
    ///  * separate cluster, or a bright picture region qualifies;
    ///  * at the edge, because bare rebate is at the film's margin while a blown highlight is not;
    ///  * orange (R &gt; G &gt; B by a clear margin), because that is what a C-41 mask IS, and it is
    ///    the test a specular white highlight at the frame edge fails.
    ///
    /// Returns null unless all three hold, so a frame with no visible base falls through to the
    /// existing estimators unchanged.
    /// </summary>
    /// <param name="image">Frame to measure, in the raw luma domain.</param>
    /// <param name="valueImage">Optional post-decouple buffer supplying the averaged VALUES while
    /// <paramref name="image"/> supplies the luma. Same split as the other estimators.</param>
    /// <returns>The (3,) base, or null when no qualifying sliver was found.</returns>
    public static double[]? EstimateTBaseFromEdgeSliver(ImageBuffer image,
                                                        ImageBuffer? valueImage = null)
    {
        const int Bins = 256;
        // The sliver is small by definition; anything larger is a region of the picture.
        const double MaxShare = 0.08;
        // …but it must be more than dust, or a hot pixel cluster would qualify.
        const double MinShare = 0.0005;
        // Share of the cluster that has to lie in the border band.
        const double MinEdgeShare = 0.70;
        // Width of that band, per side.
        const double EdgeBand = 0.10;
        // Least R:B ratio for the cluster to be a C-41 mask rather than a neutral highlight.
        const double MinOrangeRatio = 1.35;

        int w = image.Width, h = image.Height, n = w * h;
        if (n < 400) return null;

        ImageBuffer values = valueImage ?? image;
        if (values.PixelCount != n) values = image;
        float[] s = image.Data, v = values.Data;

        var luma = new double[n];
        var hist = new int[Bins];
        for (int p = 0; p < n; p++)
        {
            int i = p * 3;
            double l = ((double)s[i] + s[i + 1] + s[i + 2]) / 3.0;
            luma[p] = l;
            hist[Math.Clamp((int)(l * Bins), 0, Bins - 1)]++;
        }

        // Walk down from the top for the first populated bin, then keep walking while the
        // histogram is still falling away from that cluster. Where it turns back up, the cluster
        // has ended and the picture below has begun — that turning point is the cut.
        int top = Bins - 1;
        while (top > 0 && hist[top] == 0) top--;
        if (top <= 1) return null;

        int cut = top;
        while (cut > 1 && hist[cut - 1] >= hist[cut]) cut--;   // down the cluster's near flank
        while (cut > 1 && hist[cut - 1] <= hist[cut]) cut--;   // across the valley floor
        if (cut <= 1) return null;

        double cutLuma = (double)cut / Bins;

        long count = 0, edge = 0;
        int bx = Math.Max(1, (int)(w * EdgeBand)), by = Math.Max(1, (int)(h * EdgeBand));
        double a0 = 0, a1 = 0, a2 = 0;
        for (int y = 0; y < h; y++)
        {
            bool edgeRow = y < by || y >= h - by;
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (luma[p] <= cutLuma) continue;
                count++;
                if (edgeRow || x < bx || x >= w - bx) edge++;
                int i = p * 3;
                a0 += v[i]; a1 += v[i + 1]; a2 += v[i + 2];
            }
        }
        if (count == 0) return null;

        double share = (double)count / n;
        if (share < MinShare || share > MaxShare) return null;
        if ((double)edge / count < MinEdgeShare) return null;

        double m0 = a0 / count, m1 = a1 / count, m2 = a2 / count;
        if (!(m0 > m1 && m1 > m2)) return null;
        if (m0 / Math.Max(m2, 1e-6) < MinOrangeRatio) return null;

        var tb = new[] { m0, m1, m2 };
        Quantise(tb);
        return tb.Any(x => x <= 0) ? null : tb;
    }

    public static double[]? EstimateTBaseByMode(ImageBuffer image,
                                                double? sprocketThreshold = null,
                                                ImageBuffer? valueImage = null)
    {
        const int Bins = 512;
        // A bin must hold this share of the surviving pixels to count as the base mode. Set well
        // below the base peak's real share (the sample's was ~8% of non-board pixels in one bin)
        // and well above the shoulder scatter (~0.01%), so the gap between them is ~2 orders of
        // magnitude wide and the exact value is not load-bearing.
        const double ModeFloor = 0.0015;
        // Averaging window as a fraction of the peak luma. Narrow enough to exclude the
        // neighbouring picture tones, wide enough that the mean is taken over a large sample.
        const double HalfWindow = 0.06;

        if (sprocketThreshold is not double cut || cut <= 0.0) return null;

        ImageBuffer values = valueImage ?? image;
        float[] s = image.Data, v = values.Data;
        int total = Math.Min(image.PixelCount, values.PixelCount);

        var histogram = new int[Bins];
        double scale = cut;
        long kept = 0;
        for (int p = 0; p < total; p++)
        {
            int i = p * 3;
            double luma = ((double)s[i] + s[i + 1] + s[i + 2]) / 3.0;
            if (luma > cut) continue;
            histogram[(int)Math.Clamp(luma / scale * (Bins - 1), 0, Bins - 1)]++;
            kept++;
        }
        if (kept == 0) return null;

        // Brightest bin that is dense enough to be a material rather than scatter. Walking down
        // from the bright end (not taking the global mode) is what keeps a large dark subject
        // from winning: the base is the brightest such mode, not the most populous one overall.
        int peak = -1;
        for (int b = Bins - 1; b >= 0; b--)
            if (histogram[b] > kept * ModeFloor) { peak = b; break; }
        if (peak < 0) return null;

        double peakLuma = (double)peak / (Bins - 1) * scale;
        double low = peakLuma * (1.0 - HalfWindow), high = peakLuma * (1.0 + HalfWindow);

        var sum = new double[3];
        long count = 0;
        for (int p = 0; p < total; p++)
        {
            int i = p * 3;
            double luma = ((double)s[i] + s[i + 1] + s[i + 2]) / 3.0;
            if (luma < low || luma > high) continue;
            sum[0] += v[i]; sum[1] += v[i + 1]; sum[2] += v[i + 2];
            count++;
        }
        if (count == 0) return null;

        var tBase = new[] { sum[0] / count, sum[1] / count, sum[2] / count };
        Quantise(tBase);
        return tBase.Any(x => x <= 0) ? null : tBase;
    }

    /// <summary>
    /// Roll-wide film base by mode: <see cref="EstimateTBaseByMode"/> per frame, then the
    /// per-channel MEDIAN of the frames that produced one.
    ///
    /// The median is the whole point of doing this across a roll. The base is one physical
    /// material with a near-constant D_min along the strip, so the frames are repeated
    /// measurements of a single quantity — and a median of repeated measurements discards the
    /// frame whose mode landed on something else (an all-black scene with no bare base showing,
    /// a light leak, a frame where the board cut sat wrong) instead of letting it move the
    /// result. A mean would not: one bad frame drags it.
    /// </summary>
    /// <param name="images">Mask-domain frames (raw luma, where the board cut is calibrated).</param>
    /// <param name="sprocketThreshold">Board cut, required — see <see cref="EstimateTBaseByMode"/>.</param>
    /// <param name="valueImages">Optional post-decouple value buffers, index-aligned with
    /// <paramref name="images"/>.</param>
    /// <returns>The (3,) roll base, or null when no frame yielded a mode.</returns>
    public static double[]? EstimateTBaseByModeFromRoll(IReadOnlyList<ImageBuffer> images,
                                                        double? sprocketThreshold = null,
                                                        IReadOnlyList<ImageBuffer>? valueImages = null)
    {
        var perFrame = new List<double[]>();
        int frames = valueImages is null ? images.Count : Math.Min(images.Count, valueImages.Count);
        for (int f = 0; f < frames; f++)
            if (EstimateTBaseByMode(images[f], sprocketThreshold, valueImages?[f]) is { } pick)
                perFrame.Add(pick);

        if (perFrame.Count == 0) return null;
        var tBase = new double[3];
        for (int c = 0; c < 3; c++) tBase[c] = Median(perFrame.Select(x => x[c]).ToArray());
        Quantise(tBase);
        return tBase.Any(x => x <= 0) ? null : tBase;
    }

    /// <summary>
    /// <see cref="EstimateTBaseFromEdgeSliver"/> pooled across the roll, by MEDIAN.
    ///
    /// Median for the same reason <see cref="EstimateTBaseByModeFromRoll"/> uses it: the base is
    /// one physical material, so the frames are repeated measurements of a single quantity and
    /// the middle one is the best estimate of it. It also carries the roll through frames whose
    /// sliver is hidden — those simply do not vote, and one frame that still shows rebate is
    /// enough to base the whole roll correctly.
    /// </summary>
    public static double[]? EstimateTBaseFromEdgeSliverFromRoll(
        IReadOnlyList<ImageBuffer> images, IReadOnlyList<ImageBuffer>? valueImages = null)
    {
        var perFrame = new List<double[]>();
        int frames = valueImages is null ? images.Count : Math.Min(images.Count, valueImages.Count);
        for (int f = 0; f < frames; f++)
            if (EstimateTBaseFromEdgeSliver(images[f], valueImages?[f]) is { } pick)
                perFrame.Add(pick);

        if (perFrame.Count == 0) return null;
        var tBase = new double[3];
        for (int c = 0; c < 3; c++) tBase[c] = Median(perFrame.Select(x => x[c]).ToArray());
        Quantise(tBase);
        return tBase.Any(x => x <= 0) ? null : tBase;
    }

    /// <summary>
    /// Roll-wide D_max: the per-frame 99.9th density percentile of T / t_base, reduced across
    /// frames by an UPPER percentile rather than by a median or a max.
    ///
    /// D_max is a property of the film and its development — the densest the emulsion goes — not
    /// of any one scene, which is why one value for the roll is the right model. But the frames
    /// are not repeated measurements of it the way they are for t_base: a frame only reaches the
    /// film's true D_max if it actually contains a bright highlight. An underexposed or flat
    /// frame reads low, so a median would be dragged below the film's real ceiling and every
    /// frame that DOES contain a highlight would then clip to white.
    ///
    /// So the reduction is asymmetric on purpose: take a high percentile across frames
    /// (<paramref name="rollPercentile"/>, default 90) — high enough that the well-exposed frames
    /// define the ceiling, but not <c>max</c>, which would hand the whole roll to a single frame
    /// with a dust speck or a specular blowout.
    /// </summary>
    /// <param name="images">Frames in the same domain the inversion divides, already normalised
    /// by t_base is NOT assumed — this divides internally.</param>
    /// <param name="tBase">The roll's film base.</param>
    /// <param name="rollPercentile">Cross-frame percentile, 0-100.</param>
    /// <returns>The roll D_max, or null when no frame could be measured.</returns>
    /// <param name="masks">Raw-domain frames the luma cuts key off, index-aligned with
    /// <paramref name="images"/>. Null → each image masks itself, which is right for a white-light
    /// roll where the two domains coincide.</param>
    /// <param name="sprocketThreshold">Board cut, or null to auto-estimate per frame.</param>
    public static double? DetectDMaxFromRoll(IReadOnlyList<ImageBuffer> images, double[] tBase,
                                             double rollPercentile = 90.0,
                                             IReadOnlyList<ImageBuffer>? masks = null,
                                             double? sprocketThreshold = null)
    {
        var perFrame = new List<double>();
        for (int f = 0; f < images.Count; f++)
        {
            ImageBuffer img = images[f];
            var norm = new ImageBuffer(img.Width, img.Height);
            for (int p = 0; p < img.PixelCount; p++)
                for (int c = 0; c < 3; c++)
                    norm.Data[p * 3 + c] = (float)(img.Data[p * 3 + c] / Math.Max(tBase[c], 1e-10));
            ImageBuffer maskFrame = masks is not null && f < masks.Count ? masks[f] : img;
            double d = DetectDMax(norm, maskFrame, sprocketThreshold);
            if (double.IsFinite(d) && d > 0) perFrame.Add(d);
        }
        return perFrame.Count == 0 ? null : Percentile(perFrame.ToArray(), rollPercentile);
    }

    /// <summary>
    /// <see cref="DetectDMaxFromRoll"/> resolved per channel — the roll's highlight endpoints.
    ///
    /// The cross-frame percentile is taken per channel independently, exactly as the scalar
    /// version takes it over frames: a roll-wide value so a single flat-lit frame is not
    /// stretched on its own, which is what makes "roll-uniform" mean the same thing here as it
    /// does for the scalar d_max.
    /// </summary>
    /// <param name="masks">Raw-domain frames the luma masks key off, where the sprocket
    /// threshold is calibrated. Null = use <paramref name="images"/> for both.</param>
    /// <param name="sprocketThreshold">Bright cut for light board / sprockets; null = no cut.</param>
    /// <param name="edgeInset">
    /// Fraction of each border cropped away before measuring, exactly as
    /// <see cref="AutoWbHighFromRoll"/> does and for the same reason: the film-edge line is a
    /// hard, opaque boundary whose density sits above anything in the picture, and it is not
    /// removed by either luma cut — the bright cut is for the board and the dark valley needs a
    /// cleanly bimodal histogram.
    ///
    /// Without it the endpoints are measured off that line rather than off the scene, and because
    /// they are per-channel divisors the result is a cast. Measured on 图像 001a the uninset
    /// endpoints put the red channel 18.3% away from the balance wb_high's own solve implies for
    /// the same highlight; at any inset from 2% upward the two agree to within 0.5%. The default
    /// matches AutoWbHighFromRoll's so the two ends of the model see the same region.
    /// </param>
    /// <summary>
    /// Where the roll's no-clip headroom is read off the per-frame channel maxima.
    ///
    /// This is a ROBUST pooling of a quantity that used to be pooled by plain maximum. The lift
    /// it feeds is applied to every frame of the roll, so a statistic decided by a single frame
    /// lets that frame set the roll's exposure — and the frame a maximum selects is always the
    /// deepest one, which on real film is systematically the most off-colour (depth and cast are
    /// correlated: an off-neutral highlight reads as denser). That is the same failure
    /// <see cref="PickRepresentativeFrame"/> already rejects at the triple, arriving through the
    /// rescale instead.
    ///
    /// 95 rather than 100: high enough that ordinary frame-to-frame spread still lifts the
    /// endpoint and the roll's real highlights stay unclipped, low enough that one anomalous
    /// frame in twenty cannot carry the roll. On a 32-frame roll it admits the 30th deepest
    /// frame — every frame at or below it keeps full headroom, and only a lone extreme above it
    /// gives up some highlight detail, recoverable with 单张 on that frame.
    /// </summary>
    private const double HeadroomPercentile = 95.0;

    /// <summary>
    /// How many frames a roll needs before <see cref="HeadroomPercentile"/> replaces the plain
    /// maximum — below this the pool stays a maximum.
    ///
    /// A percentile earns its keep by having a population to judge an outlier against. On a short
    /// roll there is none: at eight frames the 95th percentile sits between the top two, so it
    /// clips the deepest frame while rejecting nothing. That is the worst of both — the guard
    /// gives up highlight detail and buys no robustness for it.
    ///
    /// Twelve is a real roll's shortest ordinary length (a 120 roll at 6×9), which is also about
    /// where one frame stops being a sixth of the evidence and starts being a twelfth.
    /// </summary>
    private const int MinFramesForHeadroomPercentile = 12;

    /// <summary>
    /// How many neighbours a frame's highlight colour is judged against in
    /// <see cref="PickRepresentativeFrame"/>.
    ///
    /// Small on purpose. The frames that measured the FILM's highlight agree with each other
    /// closely, and there are usually several of them; the frames that measured a subject
    /// disagree with everyone. Three neighbours is enough to tell a cluster from a lone frame
    /// and few enough that a roll with only four or five usable highlights still has one.
    /// </summary>
    private const int ConsensusNeighbours = 3;

    public static double[]? DetectDMaxPerChannelFromRoll(
        IReadOnlyList<ImageBuffer> images, double[] tBase, double rollPercentile = 90.0,
        IReadOnlyList<ImageBuffer>? masks = null, double? sprocketThreshold = null,
        double edgeInset = 0.05)
    {
        var perFrame = new List<double[]>();
        // The per-channel MAXIMUM over the same kept pixels, pooled across frames. The co-sited
        // triplet below is measured on pixels ranked by TOTAL density, which is what keeps the
        // three endpoints on one physical highlight and therefore keeps the colour balance
        // honest — but it says nothing about any single channel. A strongly tinted highlight (a
        // sodium lamp, a sunset, red neon) can be dense in ONE channel while its total ranks
        // below the tail, so that channel's own density exceeds the endpoint it will be divided
        // by, and it clips. These maxima are what the uniform rescale after the reduction uses to
        // guarantee it cannot.
        //
        // PER FRAME, paired with that frame's own triplet — NOT pooled across the roll. The
        // reduction below picks ONE frame's endpoint, so the no-clip lift has to be that same
        // frame's maximum or the two describe different negatives. Pooling made the roll's answer
        // depend on frames it did not choose: a single dense frame elsewhere on the strip lifted
        // the winner's triplet, and 单张 on the winning frame — which sees only its own maximum —
        // then disagreed with 整卷 even though both had picked the same highlight. That broke the
        // invariant the two buttons are supposed to share, and it is why every frame came back
        // different.
        var perFrameMax = new List<double[]>();
        for (int i = 0; i < images.Count; i++)
        {
            ImageBuffer img = images[i];
            ImageBuffer mask = masks is not null && i < masks.Count ? masks[i] : img;

            // Inset both buffers together so the mask still lines up with the pixels it admits.
            if (edgeInset > 0)
            {
                int h0 = img.Height, w0 = img.Width;
                int yi = RoundHalfEven(h0 * edgeInset), xi = RoundHalfEven(w0 * edgeInset);
                if (w0 - 2 * xi >= 4 && h0 - 2 * yi >= 4)
                {
                    bool shared = ReferenceEquals(mask, img);
                    img = Crop(img, xi, yi, w0 - 2 * xi, h0 - 2 * yi);
                    mask = shared ? img : Crop(mask, xi, yi, w0 - 2 * xi, h0 - 2 * yi);
                }
            }

            bool[] keep = HighDensityKeepMask(mask, sprocketThreshold);

            int n = img.PixelCount;

            // Density ceiling, same constant and same reason as AutoWbHighFromRoll: an opaque
            // sprocket / film-frame edge is fully light-blocking, so it lands on
            // FrameParams.DensityCeiling (4.0), above any real picture tone (~1–1.5). The dark
            // valley misses it whenever the histogram is not cleanly bimodal — which is exactly
            // the case on rolls that kept the sprockets in frame — so the ceiling is what
            // actually rejects it.
            // Applied on TOTAL density so a pixel is judged as one physical sample, not per
            // channel; dropping channels independently would bias the endpoints against each
            // other, which is the very thing they are supposed to measure. A channel PINNED AT
            // THE CLAMP is the separate case IsEndpointSample also covers — see there.
            // One quantisation step, in each channel's t_base-normalised units — the scale
            // IsResolved compares its uncertainty against.
            double step = SourceStep(img);
            var stepN = new double[3];
            for (int c = 0; c < 3; c++) stepN[c] = step / Math.Max(tBase[c], 1e-10);

            var dens = new double[3][];
            for (int c = 0; c < 3; c++) dens[c] = new double[n];
            int k = 0;
            for (int p = 0; p < n; p++)
            {
                if (!keep[p]) continue;
                double t0 = img.Data[p * 3] / Math.Max(tBase[0], 1e-10);
                double t1 = img.Data[p * 3 + 1] / Math.Max(tBase[1], 1e-10);
                double t2 = img.Data[p * 3 + 2] / Math.Max(tBase[2], 1e-10);
                // Unresolved in ANY channel disqualifies the whole pixel: the endpoints are read
                // co-sited off one physical sample, so a pixel is either a usable sample or it is
                // not — dropping channels independently would bias them against each other.
                if (step > 0 && !(IsResolved(t0, stepN[0]) && IsResolved(t1, stepN[1])
                                  && IsResolved(t2, stepN[2]))) continue;
                double d0 = FrameParams.DensityOf(t0);
                double d1 = FrameParams.DensityOf(t1);
                double d2 = FrameParams.DensityOf(t2);
                if (!IsEndpointSample(d0, d1, d2)) continue;
                dens[0][k] = d0; dens[1][k] = d1; dens[2][k] = d2;
                k++;
            }
            if (k == 0) continue;

            // CO-SITED: rank the kept pixels by TOTAL density, then average the top tail's three
            // channels. All three endpoints therefore come from the SAME physical highlight.
            //
            // Three independent per-channel percentiles — what this did before — draw R, G and B
            // from three DIFFERENT pixels, and the endpoints are per-channel DIVISORS, so any
            // difference between those pixels becomes a colour cast baked into the inversion.
            // <see cref="HighlightDensityFromRoll"/> already documents this exact failure at the
            // same end of the scale ("white clouds look yellow") and solves it the same way; this
            // routine simply had not been brought into line. Measured on 图像 001a the independent
            // form put the red endpoint 15% away from what wb_high's own co-sited solve implied
            // for the same highlight, which is a cast no Stage-2 control can remove because it
            // happens inside the inversion.
            //
            // The tail is averaged rather than a single extremum taken, so grain and dust cannot
            // define the white point — the same reasoning behind the percentile it replaces.
            //
            // 0.1% of the kept pixels, matching the 99.9th percentile this replaces, floored so a
            // small frame still averages something and clamped so it cannot swallow the frame.
            const double tailFraction = 0.001;
            int tail = Math.Clamp((int)Math.Ceiling(k * tailFraction), 1, Math.Max(1, k / 2));
            var (spike, guard) = SpikeThresholds(k, tailFraction);

            var order = new int[k];
            var total = new double[k];
            for (int q = 0; q < k; q++)
            {
                order[q] = q;
                total[q] = (dens[0][q] + dens[1][q] + dens[2][q]) / 3.0;
            }
            Array.Sort(total, order);   // ascending by total density; densest at the end

            // Unguarded retry on total rejection: a plateau-contaminated endpoint still beats
            // dropping the frame.
            int[] tailIdx = DenseTailIndices(total, order, k, tail, spike, guard)
                         ?? DenseTailIndices(total, order, k, tail, double.PositiveInfinity, 0.0)!;

            var res = new double[3];
            foreach (int src in tailIdx)
                for (int c = 0; c < 3; c++) res[c] += dens[c][src];
            for (int c = 0; c < 3; c++) res[c] /= tailIdx.Length;

            // The no-clip maximum is guarded BY THE SAME RULE as the tail above. Without that the
            // guard achieves nothing: a plateau kept out of the tail still sets chanMax, and
            // RescaleToClearChannelMax then lifts the endpoint back onto it — measured, a
            // 200-pixel flat block at 2.80 was correctly excluded from the tail and the rescale
            // pulled the endpoint from the picture's 2.60 up to 2.80 regardless. The two
            // populations must agree; see MaxChannelDensityFromRoll, which mirrors this exactly.
            var frameMax = new double[3];
            for (int c = 0; c < 3; c++)
            {
                var mkeys = new double[k];
                Array.Copy(dens[c], mkeys, k);
                var morder = new int[k];
                for (int q = 0; q < k; q++) morder[q] = q;
                Array.Sort(mkeys, morder);

                int[] midx = DenseTailIndices(mkeys, morder, k, 1, spike, guard)
                          ?? DenseTailIndices(mkeys, morder, k, 1, double.PositiveInfinity, 0.0)!;
                frameMax[c] = dens[c][midx[0]];
            }

            bool ok = true;
            for (int c = 0; c < 3; c++)
                if (!double.IsFinite(res[c]) || res[c] <= 0) { ok = false; break; }
            if (ok) { perFrame.Add(res); perFrameMax.Add(frameMax); }
        }
        if (perFrame.Count == 0) return null;

        // Reduce across frames by picking ONE frame's triplet — the densest — rather than taking
        // a per-channel percentile over frames.
        //
        // A per-channel percentile re-introduces exactly the error the co-siting above removes,
        // one level up: each channel's 90th percentile can land on a DIFFERENT frame, so the
        // result is a triplet no single negative ever produced, and the balance between its
        // channels is an artefact of which frames happened to rank where. Measured on 图像 001a it
        // also skews the whole triplet high — every frame's red endpoint sat between 0.98 and
        // 1.09, and the pooled answer came out 1.077 — because a percentile of maxima is not a
        // maximum. The frames' own endpoints were consistent to within 10%; the pooled one was
        // further from any of them than they were from each other.
        //
        // Taking ONE frame keeps the triplet co-sited all the way through: it is one physical
        // highlight, on one piece of film, measured under one exposure. That is the same choice
        // <see cref="AutoWbHighFromRoll"/> makes for the same quantity, and it is what makes the
        // two agree. <paramref name="rollPercentile"/> is retained for API compatibility but no
        // longer selects between frames.
        //
        // WHICH frame is a second question, and "the densest" is the wrong answer to it on its
        // own. Density decides how DEEP the endpoint sits; the ratios between the three channels
        // decide the roll's HIGHLIGHT COLOUR — and those ratios are then applied to every frame,
        // because the endpoints are per-channel divisors. A single frame whose brightest subject
        // is strongly coloured (a sunset, a neon sign, a red wall) can top the roll on total
        // density while its ratios describe that subject rather than the film. Picking it hands
        // its cast to the whole roll, and no Stage-2 control can take it back out.
        //
        // So the pick is by CONSENSUS between frames — see PickRepresentativeFrame for why
        // neither depth nor a fitted trend can be allowed to decide.
        //
        // NOTE these triples are PRE-RESCALE: the no-clip lift below is applied to the winner
        // only. The lift is one factor on all three channels, so it cannot change any frame's
        // RATIOS — the colour comparison is unaffected — but it does change totals, so a frame's
        // depth here is not the same number a single-frame solve reports for it.
        //
        // AND THEY ARE RELATIVE TO <paramref name="tBase"/>. Every ratio argument in this method
        // — the co-siting, the pick, the lift preserving colour — is only true of densities
        // measured against the FILM BASE, because the inversion consumes the SPAN
        // dMax_c − dMin_c (DensityEndpoints.FromMeasured), and the colour balance is the ratio
        // between the three spans. Called with a neutral 1,1,1 the triples are absolute, each
        // channel carries its own dMin, and two things go wrong at once: the absolute ratio
        // (dMin_R + s_R)/(dMin_G + s_G) drifts toward the base's own colour as the highlight
        // gets shallower, which is a trend that is NOT in the spans; and lifting the winner by k
        // turns its span into k·s + (k−1)·dMin — a per-channel offset in the base's colour, so
        // the "colour-preserving" lift is a cast in the domain that matters. Measured on 诺日士1089
        // (12 frames, no board): the absolute pick landed on 003a and a 1.549× lift moved its span
        // R/G from 0.866 to 0.767 against 0.905 on a confirmed-neutral frame, and the whole roll
        // came out red. The same numbers measured against the base pick a frame in the neutral
        // cluster and the lift leaves its span ratios untouched. Callers therefore pass the roll's
        // t_base here and add dMin back to the result.
        int bestIdx = PickRepresentativeFrame(perFrame);
        double[] best = perFrame[bestIdx];

        // ROLL-WIDE headroom, deliberately — not the winner's own.
        //
        // The winner is now chosen for its COLOUR, so it can be a frame of ordinary depth while
        // other frames on the roll go deeper. Its own maximum only clears its own pixels; every
        // deeper frame would then exceed the endpoint it is divided by and clip its highlight,
        // which is an unrecoverable loss of picture. Pooling across frames is what lifts the
        // chosen triple clear of the WHOLE roll, and because the lift is one factor on all three
        // channels it cannot disturb the colour the frame was chosen for.
        //
        // POOLED BY AN UPPER PERCENTILE, NOT BY THE MAXIMUM. This is the same reduction the
        // per-frame tail above already uses, applied one level up, and for the same reason.
        // A plain max is decided by exactly ONE frame — necessarily the deepest on the strip —
        // and depth is correlated with cast, so that frame is systematically the least
        // representative one. PickRepresentativeFrame exists precisely to keep such a frame from
        // speaking for the roll; taking a raw max here handed it the roll back through the lift
        // instead of through the triple, which is the same failure wearing a different hat.
        //
        // Measured on the 除碳5219 roll (32 frames, Fuji): DSCF5657 set all three channels of the
        // pool on its own and lifted the chosen triple 1.197×, from DSCF5654's measured
        // [1.323, 1.622, 2.274] to [1.584, 1.942, 2.722]. Every ordinary frame on that roll then
        // rendered against an endpoint ~20% above its own highlight — frame DSCF5635's brightest
        // tone reached only 0.82/0.81/0.77 of the divisor it was applied to, so the whole strip
        // came out flat and dark with 17.6% of its highlight range unreachable. Dropping that one
        // frame moved the roll to [1.344, 1.648, 2.310] and the lift to 1.016. The percentile
        // reaches that answer WITHOUT having to know which frame to drop.
        //
        // The percentile is high enough that ordinary frame-to-frame spread still lifts the
        // endpoint — clipping the roll's genuine highlights is the failure this rescale exists to
        // prevent, and it stays prevented for every frame at or below the percentile. What it
        // gives up is absolute protection for the extreme tail: a lone frame above it can clip.
        // That is the correct trade at this end. Clipping is bounded and local — it costs that
        // one frame some highlight detail, and 单张 on it recovers the full range. An inflated
        // endpoint is neither: it is a per-channel divisor applied to all 32 frames at once.
        //
        // THE COST, STATED PLAINLY: 整卷 no longer equals 单张 on any one frame. Those two now
        // answer different questions — 单张 measures one negative, 整卷 measures a roll and must
        // fit the frames in it — so a roll whose frames differ in depth will not reproduce its
        // answer from any single frame. That is a deliberate trade of a checkable invariant for a
        // correct colour, taken because the invariant was costing the roll its highlight balance:
        // ranking on depth handed an expired Superia 200 roll to its most off-colour frame.
        //
        // BELOW <see cref="MinFramesForHeadroomPercentile"/> FRAMES THE POOL IS STILL A MAXIMUM,
        // the same guard and the same reasoning as PickRepresentativeFrame's own three-frame
        // floor. A percentile rejects an outlier by having a population to judge it against; on a
        // handful of frames there is none, and the percentile stops being a robust statistic and
        // becomes merely a slightly shrunken maximum — it would clip the deepest frame without
        // rejecting anything, which is a pure loss. A short roll's deepest frame is not yet
        // distinguishable from ordinary spread, so it gets the full protection.
        var chanMax = new double[3];
        bool pooled = perFrameMax.Count >= MinFramesForHeadroomPercentile;
        for (int c = 0; c < 3; c++)
        {
            if (pooled)
            {
                var col = new double[perFrameMax.Count];
                for (int i = 0; i < perFrameMax.Count; i++) col[i] = perFrameMax[i][c];
                chanMax[c] = Percentile(col, HeadroomPercentile);
            }
            else
            {
                foreach (double[] m in perFrameMax) if (m[c] > chanMax[c]) chanMax[c] = m[c];
            }
        }

        // UNIFORM RESCALE SO NO CHANNEL CLIPS.
        //
        // The triplet above is co-sited and therefore correctly BALANCED, but it is the density of
        // one highlight — nothing guarantees that every other pixel's red, green and blue each sit
        // below their own channel's entry. A tinted highlight whose total density ranked below the
        // tail can still be denser in one channel than that channel's endpoint, and since the
        // endpoints are per-channel divisors, that channel clips.
        //
        // The fix is one ratio applied to all three, not three independent per-channel maxima.
        // Taking the maxima directly would let R, G and B come from three different pixels, which
        // is exactly the error the co-siting above exists to prevent — a triplet no negative ever
        // produced, whose channel ratios are an artefact of which pixel ranked where, baked into
        // the inversion as a cast no Stage-2 control can remove. Scaling all three by the SAME
        // factor leaves every ratio between them untouched, so the balance survives intact and
        // only the placement moves. RescaleToClearChannelMax is that one ratio, kept as its own
        // public method so the lift can be read and tested apart from the measurement around it.
        //
        // It returns a fresh array rather than scaling in place — `best` still aliases an entry in
        // perFrame, and mutating it would leave that list holding a value it never measured.
        return RescaleToClearChannelMax(best, chanMax);
    }

    /// <summary>
    /// Which frame's highlight triple should stand for the roll: the one whose highlight colour
    /// the most OTHER frames agree with.
    ///
    /// Two properties of a triple matter and they are independent. Its ratios say what COLOUR the
    /// highlight is — and because the endpoints are per-channel divisors applied to every frame,
    /// that colour becomes the whole roll's. Its total density says how DEEP it is — but depth is
    /// repairable (the no-clip rescale lifts the winner) and colour is not, so colour decides.
    ///
    /// WHY CONSENSUS. The frames of a roll are repeated measurements of one film's highlight only
    /// where the brightest subject was actually neutral — a white wall, a cloud, a blown sky. Those
    /// frames all report the same ratios, to within grain, and there are typically several of
    /// them. A frame whose brightest subject was coloured (a sunset, a lamp, a red wall) reports
    /// that subject, and no two such frames report the same thing. So the film's colour is where
    /// the triples PILE UP, and a subject's colour is wherever one triple sits on its own. Ranking
    /// each frame by its distance to its nearest few neighbours finds the pile without knowing
    /// any frame's content. Measured on 诺日士1089 (12 frames): five well-exposed frames sit within
    /// ±1.5% of each other in R/G and ±2.5% in B/G, the seven shallow ones scatter over 17% and
    /// 40%, and the three tightest frames are the ones a person had picked as reference.
    ///
    /// WHY NOT DEPTH. A frame whose highlight is off-neutral reads as denser, since its strong
    /// channel inflates the total; whichever frame depth favours is systematically the one least
    /// representative in colour. Measured on an expired Superia 200 roll, the deepest frame was
    /// also the worst colour match and took the roll with it.
    ///
    /// WHY NOT THE MEDIAN. The median of the ratios assumes the frames scatter about the film's
    /// colour, so an asymmetric spread of coloured subjects pulls it off the cluster; and ranking
    /// by distance to a point a roll never produced can prefer a lone frame that happens to sit
    /// near that point over any member of the cluster. Consensus asks each frame how many others
    /// back it, which is the question actually being asked.
    ///
    /// WHY NOT A DEPTH TREND. An earlier revision fitted the roll's ratio-versus-depth line and
    /// read the colour off its shallow end, on the theory that a shallow highlight carries least
    /// of its subject. The trend it was fitting was an artefact of measuring ABSOLUTE densities:
    /// R/G = (dMin_R + s_R)/(dMin_G + s_G) tends to the base's own ratio as the span s shrinks,
    /// so on any orange-masked film R/G rises and B/G falls with depth whether or not the spans
    /// change at all — exactly the anti-correlated pair recorded on 除碳5219. Measured against the
    /// base, that roll-wide correlation on 诺日士1089 went from +0.76 to −0.14. Extrapolating the
    /// artefact to the shallow end pushed the endpoint toward the base's colour, the frame it
    /// picked was one of the scattered shallow ones, and the three best-ranked candidates were
    /// within 0.002 of each other, so the answer changed with the crop. The triples this sees are
    /// base-relative now (see the caller), and there is no trend left to fit.
    ///
    /// The result is still ONE frame's co-sited measurement — nothing about the triple is
    /// synthetic, which is the property the whole reduction exists to preserve.
    ///
    /// Under three frames there are no neighbours to consult: one odd frame out of two is
    /// indistinguishable from one out of one, and depth is the honest answer.
    /// </summary>
    private static int PickRepresentativeFrame(List<double[]> perFrame)
    {
        static double Total(double[] t) => t[0] + t[1] + t[2];

        int densest = 0;
        for (int i = 1; i < perFrame.Count; i++)
            if (Total(perFrame[i]) > Total(perFrame[densest])) densest = i;

        int n = perFrame.Count;
        if (n < 3) return densest;

        // Ratios against green, the channel a negative's highlight varies least in.
        var rg = new double[n];
        var bg = new double[n];
        for (int i = 0; i < n; i++)
        {
            double g = Math.Max(perFrame[i][1], 1e-9);
            rg[i] = perFrame[i][0] / g;
            bg[i] = perFrame[i][2] / g;
        }

        int k = Math.Min(ConsensusNeighbours, n - 1);
        // Tie-break on depth, toward the roll's MIDDLE. Frames of one cluster can be backed
        // equally well; the one nearest the roll's typical depth is the one least likely to be an
        // outlier in the other property, and the choice does not depend on frame order. Not the
        // deepest: on a roll of frames that agree exactly, that would hand the endpoint to a
        // lone deep frame — the case the headroom percentile below exists to keep out.
        var depths = new double[n];
        for (int i = 0; i < n; i++) depths[i] = Total(perFrame[i]);
        double midDepth = Median((double[])depths.Clone());

        int best = 0;
        double bestSupport = double.PositiveInfinity;
        var dist = new double[n - 1];
        for (int i = 0; i < n; i++)
        {
            int m = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                // Relative offsets, summed: the two axes have different scales, so absolute
                // differences would silently weight B/G more heavily than R/G. Measured against
                // the frame being judged, so the distance is not symmetric — that is fine, each
                // frame is asked the same question of its own neighbours.
                dist[m++] = Math.Abs(rg[j] - rg[i]) / Math.Max(Math.Abs(rg[i]), 1e-9)
                          + Math.Abs(bg[j] - bg[i]) / Math.Max(Math.Abs(bg[i]), 1e-9);
            }
            Array.Sort(dist);
            double support = 0;
            for (int q = 0; q < k; q++) support += dist[q];
            if (support < bestSupport
                || (support == bestSupport
                    && Math.Abs(depths[i] - midDepth) < Math.Abs(depths[best] - midDepth)))
            {
                bestSupport = support;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// The uniform no-clip rescale, on its own: lift a co-sited highlight triple until every
    /// channel's own densest kept pixel sits at or below its endpoint, moving all three by ONE
    /// factor so the ratios between them — the colour balance — survive untouched.
    ///
    /// "Untouched" holds for densities measured AGAINST THE FILM BASE, which is what the
    /// inversion's spans are. On absolute densities the same factor adds (k−1)·dMin to each
    /// channel's span, a cast in the base's colour — see the caller's note on tBase.
    ///
    /// Split out of <see cref="DetectDMaxPerChannelFromRoll"/>, which is currently its only
    /// caller. THIS IS A CALIBRATION STEP, not a general safety net: it belongs where the
    /// endpoints are being chosen from scratch and an endpoint under a channel's own maximum would
    /// bake a clipped channel into the whole roll. Do not reach for it to "protect" a triple some
    /// later stage has already placed — the lift is >= 1 by construction, so it can only push the
    /// endpoints up, and on an already-placed triple that reads as an unasked-for exposure change.
    /// 智能白平衡 used to call it for exactly that reason and had to stop; see AutoWbAiAsync step 3.
    ///
    /// The factor is the largest overshoot across the three channels, so the worst offender lands
    /// exactly on its endpoint and the other two stay below theirs. It is never less than 1: a
    /// triple that already clears every channel is returned unchanged.
    /// </summary>
    /// <param name="endpoint">Co-sited per-channel highlight densities. Not mutated.</param>
    /// <param name="channelMax">Per-channel maximum density over the same kept pixels the
    /// endpoint was measured on — see <see cref="MaxChannelDensityFromRoll"/>.</param>
    public static double[] RescaleToClearChannelMax(double[] endpoint, double[] channelMax)
    {
        double scale = 1.0;
        for (int c = 0; c < 3; c++)
        {
            if (endpoint[c] <= 0 || !double.IsFinite(channelMax[c])) continue;
            double need = channelMax[c] / endpoint[c];
            if (need > scale) scale = need;
        }
        return new[] { endpoint[0] * scale, endpoint[1] * scale, endpoint[2] * scale };
    }

    /// <summary>
    /// Per-channel MAXIMUM density over the pixels <see cref="DetectDMaxPerChannelFromRoll"/>
    /// would keep — same edge inset, same keep mask, same real-density ceiling, same t_base.
    ///
    /// This is the second half of what the detector's no-clip rescale needs, and it exists as its
    /// own entry point so "does this triple clear the frame?" can be asked — and tested — apart
    /// from the detector's own measurement pass. Deliberately NOT co-sited: the whole question is whether any
    /// single channel anywhere overshoots, so each channel's own extreme is the right statistic —
    /// which is safe here precisely because the result is only ever used as a uniform scale factor
    /// (see <see cref="RescaleToClearChannelMax"/>), never as an endpoint triple in its own right.
    /// </summary>
    /// <returns>Three maxima, or all-zero when no pixel survived the masks.</returns>
    public static double[] MaxChannelDensityFromRoll(
        IReadOnlyList<ImageBuffer> images, double[] tBase,
        IReadOnlyList<ImageBuffer>? masks = null, double? sprocketThreshold = null,
        double edgeInset = 0.05)
    {
        var chanMax = new double[3];
        for (int i = 0; i < images.Count; i++)
        {
            ImageBuffer img = images[i];
            ImageBuffer mask = masks is not null && i < masks.Count ? masks[i] : img;

            if (edgeInset > 0)
            {
                int h0 = img.Height, w0 = img.Width;
                int yi = RoundHalfEven(h0 * edgeInset), xi = RoundHalfEven(w0 * edgeInset);
                if (w0 - 2 * xi >= 4 && h0 - 2 * yi >= 4)
                {
                    bool shared = ReferenceEquals(mask, img);
                    img = Crop(img, xi, yi, w0 - 2 * xi, h0 - 2 * yi);
                    mask = shared ? img : Crop(mask, xi, yi, w0 - 2 * xi, h0 - 2 * yi);
                }
            }

            bool[] keep = HighDensityKeepMask(mask, sprocketThreshold);
            int n = img.PixelCount;

            // The plateau guard has to run HERE TOO, on the same population and by the same rule.
            // The rescale lifts the detector's endpoint until it clears this maximum, so a flat
            // block excluded from the endpoint's tail but still counted here is re-admitted
            // through the back door: measured, a 200-pixel block at 2.80 was correctly kept out
            // of the tail, and the rescale then pulled the endpoint from the picture's 2.60 up to
            // 2.80 anyway — the guard bought exactly nothing. The two populations must agree,
            // which is the invariant the IsEndpointSample comments below already state for the
            // per-sample tests; the plateau rule is now part of it.
            var chanDens = new double[3][];
            for (int c = 0; c < 3; c++) chanDens[c] = new double[n];
            int kept = 0;
            // Same resolution test as the detector, and it MUST stay the same — see the ceiling
            // note below for why this maximum and that endpoint have to be drawn from one and the
            // same population.
            double step = SourceStep(img);
            var stepN = new double[3];
            for (int c = 0; c < 3; c++) stepN[c] = step / Math.Max(tBase[c], 1e-10);
            for (int p = 0; p < n; p++)
            {
                if (!keep[p]) continue;
                double t0 = img.Data[p * 3] / Math.Max(tBase[0], 1e-10);
                double t1 = img.Data[p * 3 + 1] / Math.Max(tBase[1], 1e-10);
                double t2 = img.Data[p * 3 + 2] / Math.Max(tBase[2], 1e-10);
                if (step > 0 && !(IsResolved(t0, stepN[0]) && IsResolved(t1, stepN[1])
                                  && IsResolved(t2, stepN[2]))) continue;
                double d0 = FrameParams.DensityOf(t0);
                double d1 = FrameParams.DensityOf(t1);
                double d2 = FrameParams.DensityOf(t2);
                // Same test as the detector, and it MUST stay the same: this maximum is what the
                // no-clip rescale compares the detector's endpoint against, so a pixel counted
                // here but rejected there would demand headroom for a sample the endpoint was
                // never allowed to see — which is precisely how a clamped blue channel dragged the
                // whole triple up by 1.75x.
                if (!IsEndpointSample(d0, d1, d2)) continue;
                chanDens[0][kept] = d0; chanDens[1][kept] = d1; chanDens[2][kept] = d2;
                kept++;
            }
            if (kept == 0) continue;

            // The guarded maximum is the densest sample that is not part of a skipped plateau —
            // a tail of one, walked by the same rule as the endpoint's tail.
            var (spike, guard) = SpikeThresholds(kept, MaxChannelTailFraction);
            for (int c = 0; c < 3; c++)
            {
                var keys = new double[kept];
                Array.Copy(chanDens[c], keys, kept);
                var order = new int[kept];
                for (int q = 0; q < kept; q++) order[q] = q;
                Array.Sort(keys, order);

                int[] idx = DenseTailIndices(keys, order, kept, 1, spike, guard)
                         ?? DenseTailIndices(keys, order, kept, 1, double.PositiveInfinity, 0.0)!;
                double d = chanDens[c][idx[0]];
                if (d > chanMax[c]) chanMax[c] = d;
            }
        }
        return chanMax;
    }

    /// <summary>
    /// The tail depth the no-clip maximum's plateau guard is scaled against.
    ///
    /// It matches <see cref="DetectDMaxPerChannelFromRoll"/>'s 0.1%, deliberately: the two
    /// measurements are compared against each other by the rescale, so they must call the same
    /// thing a plateau. The maximum itself is still a single sample — only the threshold is
    /// borrowed.
    /// </summary>
    private const double MaxChannelTailFraction = 0.001;

    /// <summary>
    /// Pixels admissible when measuring the HIGH-density end: everything except the light board /
    /// sprockets (bright cut, dilated) and the opaque mask card / film-edge line (dark valley).
    ///
    /// The D_max detectors historically took no mask at all — they percentiled every pixel. That
    /// was survivable while d_max was a scalar SUBTRACTED from the density (an inflated value
    /// shifts the whole frame, and exposure pulls it back), but the endpoint model DIVIDES each
    /// channel by its own value. An opaque edge line inflates the channels unequally, so it turns
    /// into a colour cast that no exposure control can undo. The 99.9th percentile dodges dust; a
    /// film-edge line is a whole column, not dust.
    ///
    /// Same two cuts as <see cref="AutoWbHighFromRoll"/>, on the raw-domain luma where the
    /// sprocket threshold is calibrated.
    ///
    /// Public because it is the ONE definition of "which pixels are film" that every automatic
    /// measurement has to agree on. The bright cut alone is not enough and neither is the dark
    /// one: a copy stand shows the board ABOVE the film and the blocking card BELOW it, so a
    /// single-ended rule always leaves one of the two inside the statistics.
    /// </summary>
    /// <param name="maskFrame">Raw-domain frame — the luma domain the cuts are calibrated in.</param>
    /// <param name="sprocketThreshold">Bright board cut, or null to auto-estimate it from the
    /// frame. Passing null is what lets a caller with no dialog-supplied threshold still get the
    /// board excluded; <see cref="Sprocket.NoBoard"/> maps back to "no bright cut".</param>
    /// <summary>
    /// Is this pixel usable for measuring an ENDPOINT?
    ///
    /// Two independent rejections, because there are two different ways a sample can carry no
    /// endpoint information and each is invisible to the other's test:
    ///
    /// TOTAL density at or above <see cref="FrameParams.RealDensityCeiling"/> — an opaque sprocket
    /// hole, a mask card, a film-frame edge. The whole pixel is light-blocking, so it is rejected
    /// as ONE physical sample rather than per channel; dropping channels independently here would
    /// bias the endpoints against each other, which is the very thing they are supposed to measure.
    ///
    /// ANY SINGLE CHANNEL pinned at <see cref="FrameParams.DensityCeiling"/> — that channel
    /// underflowed to zero transmittance and <see cref="FrameParams.DensityOf"/> clamped it. The
    /// clamp value is a FLOOR ARTEFACT, not a measurement: the real density is unknown and merely
    /// at-least-this. Averaging it into an endpoint states a density the film never had.
    ///
    /// WHY THE TOTAL TEST DOES NOT CATCH THAT. A pixel whose blue underflowed while red and green
    /// are ordinary picture tones scores (1.9 + 1.7 + 4.0)/3 = 2.5, comfortably under the 3.0
    /// ceiling, and is kept — carrying a fabricated 4.0 into the blue endpoint. On a scan whose
    /// blue channel was crushed to code 0 over 11.66% of the frame, the co-sited top tail is made
    /// almost entirely of such pixels, and the blue endpoint came out at 6.8 — a transmittance of
    /// 1/6,800,000, where the file's own bit depth cannot express more than about 2.4. The
    /// no-clip rescale then lifted all three endpoints ~1.75x to clear it, crushing the blue slope
    /// to two thirds of its correct value and throwing the frame yellow.
    ///
    /// The underflow itself is a property of the FILE and cannot be undone here — this only stops
    /// it from being read as a measurement.
    /// </summary>
    private static bool IsEndpointSample(double d0, double d1, double d2)
    {
        if ((d0 + d1 + d2) / 3.0 >= FrameParams.RealDensityCeiling) return false;
        return d0 < FrameParams.DensityCeiling
            && d1 < FrameParams.DensityCeiling
            && d2 < FrameParams.DensityCeiling;
    }

    /// <summary>
    /// The largest density uncertainty an endpoint sample may carry, in density units.
    ///
    /// A sample's density is only as trustworthy as the quantisation step it sits on, and near
    /// black that step is enormous: on 8-bit, code 1 to 2 is 0.301 D and code 2 to 3 is 0.176.
    /// Samples whose neighbouring code is further away than this in density are not measurements,
    /// they are quantisation — and they always err toward TOO DENSE, because the lowest code maps
    /// to the highest density.
    ///
    /// AN EMPIRICAL VALUE, and worth reading as one. It was swept against a hand-graded reference:
    /// on the scan this was written for (a blue channel crushed to code 0 over 11.6% of the frame)
    /// the detector's blue endpoint lands at B/G slope 0.511 against the user's by-eye 0.493,
    /// where the untreated estimate was 0.333. Tightening from here keeps improving that one
    /// number, so the value is chosen on the conservative side of the best fit rather than at it:
    /// over-rejecting costs a little endpoint accuracy, under-rejecting lets fabricated densities
    /// into the answer.
    ///
    /// IT IS A SAFETY NET, NOT A GENERAL IMPROVEMENT, and the distinction matters for anyone
    /// tuning it. On synthetic negatives at ordinary densities (D_max ~1.9) the darkest sample
    /// still sits around code 3, clear of the cliff, and the threshold changes nothing at any
    /// setting — it only engages once the picture runs into the file's own bit depth. Nor can it
    /// repair one that has: with the blue channel crushed eight codes deep the error is identical
    /// with and without it. What it prevents is quantisation being READ as measurement.
    ///
    /// The R and G endpoints on the reference scan are unchanged across the whole swept range, so
    /// tightening this is safe for healthy channels; it is the crushed one that moves.
    /// </summary>
    private const double MaxDensityUncertainty = 0.10;

    /// <summary>
    /// Is this sample's density RESOLVED, or is it mostly quantisation?
    ///
    /// The uncertainty of a density read off a quantised file is the gap to the adjacent code:
    ///
    ///   ΔD = -log10(L/t_base) + log10((L+step)/t_base) = log10((L+step)/L)
    ///
    /// T_BASE CANCELS. The uncertainty is a property of the CODE ALONE — not of the channel, not
    /// of the film base, not of the exposure — which is what makes one shared threshold the right
    /// shape for this test. The same reasoning says a per-channel density CAP would be the wrong
    /// shape: it states one physical fact (quantisation) as three different numbers derived from
    /// three film-base values, and the three would then have to be kept in step forever.
    ///
    /// WHY THIS MATTERS AT THE ENDPOINT. On 8-bit the step near black is brutal — code 1 to 2 is
    /// 0.301 D, code 2 to 3 is 0.176 — so a channel crushed toward zero reports densities that are
    /// pure quantisation and systematically TOO HIGH (the lowest code always maps to the highest
    /// density). The endpoint is the top tail of exactly that region, so those samples dominate it:
    /// on the scan this was written for, the blue channel's endpoint came out at 5.2 where the
    /// file's own bit depth can express at most 3.0, and the no-clip rescale then dragged all three
    /// endpoints up to clear it, crushing blue's slope and throwing the frame yellow.
    ///
    /// SIXTEEN-BIT IS UNAFFECTED, and that is the point of testing the step rather than the code.
    /// A 16-bit file's step is 1/65535, so even its lowest codes resolve density far finer than
    /// this threshold and every sample passes — the test disappears exactly where it should.
    /// </summary>
    /// <param name="tNorm">Transmittance already normalised by t_base (what DensityOf takes).</param>
    /// <param name="stepNorm">One quantisation step in the same normalised units.</param>
    private static bool IsResolved(double tNorm, double stepNorm)
        => tNorm > 0 && Math.Log10((tNorm + stepNorm) / tNorm) <= MaxDensityUncertainty;

    /// <summary>
    /// The source file's quantisation step for this buffer, in its own linear units.
    ///
    /// READ, NOT MEASURED. It is stamped at decode by <see cref="TiffIO"/> and carried through
    /// every geometric transform — see <see cref="ImageBuffer.SourceQuantisationStep"/> for why
    /// measuring it off the pixels is wrong (a box-downsampled preview lands on a finer lattice
    /// and reports float noise, silently disabling every test built on it).
    ///
    /// 0 means unknown or continuous, which makes <see cref="IsResolved"/> admit everything — the
    /// correct no-op for data whose coarseness cannot be established.
    /// </summary>
    private static double SourceStep(ImageBuffer img) => img.SourceQuantisationStep;

    public static bool[] HighDensityKeepMask(ImageBuffer maskFrame, double? sprocketThreshold)
    {
        int w = maskFrame.Width, h = maskFrame.Height, n = w * h;
        var keep = new bool[n];
        Array.Fill(keep, true);

        float[] d = maskFrame.Data;
        var luma = new double[n];
        for (int p = 0; p < n; p++)
            luma[p] = ((double)d[p * 3] + d[p * 3 + 1] + d[p * 3 + 2]) / 3.0;

        // No caller-supplied cut: measure one. The board is physically there whether or not a
        // dialog asked about it, and leaving it in is what puts a blown-white block at the top of
        // every percentile this mask feeds. NoBoard means the histogram showed no board/base
        // two-peak structure, which is the genuine "nothing to cut" case.
        double? bright = sprocketThreshold;
        if (bright is null)
        {
            double est = Sprocket.EstimateSprocketThreshold(maskFrame);
            if (est < Sprocket.NoBoard) bright = est;
        }

        // Bright end: light board / sprockets, dilated outward to swallow the soft transition
        // ring between the transmissive core and the opaque frame edge. The RING IS MEASURED,
        // not assumed — see BoardShoulderRadius.
        if (bright is double thr)
        {
            var board = new bool[n];
            bool any = false;
            for (int p = 0; p < n; p++) if (luma[p] > thr) { board[p] = true; any = true; }
            if (any)
            {
                board = Dilate(board, w, h, BoardShoulderRadius(maskFrame, board, w, h));
                for (int p = 0; p < n; p++) if (board[p]) keep[p] = false;
            }
        }

        // Dark end: the opaque mask card / edge line — exactly the thing that would otherwise
        // set the endpoint. <= 0 is the "no mask present" sentinel.
        double darkValley = Sprocket.EstimateDarkValley(maskFrame);
        if (darkValley > 0.0)
            for (int p = 0; p < n; p++) if (!(luma[p] > darkValley)) keep[p] = false;

        // Never hand back an empty selection — an all-masked frame should fall back to measuring
        // everything rather than silently dropping out of the roll statistics.
        for (int p = 0; p < n; p++) if (keep[p]) return keep;
        Array.Fill(keep, true);
        return keep;
    }

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
    public static double DetectDMax(ImageBuffer image) => DetectDMax(image, null, null);

    /// <summary>
    /// <see cref="DetectDMax(ImageBuffer)"/> restricted to the pixels that are actually film.
    ///
    /// D_max is the DENSEST point, so it is precisely the measurement an opaque blocking card
    /// steals: the card is darker than any exposed area, so it wins the 99.9th density percentile
    /// outright and the roll's white end is set by a piece of cardboard. The bright board matters
    /// less here (it is the low-density end) but is excluded for consistency — one definition of
    /// "film" across every automatic measurement.
    /// </summary>
    /// <param name="maskFrame">RAW-domain frame the cuts key off, since the valleys are calibrated
    /// on raw luma while <paramref name="image"/> is already T_norm. Null → no masking.</param>
    /// <param name="sprocketThreshold">Board cut, or null to auto-estimate.</param>
    public static double DetectDMax(ImageBuffer image, ImageBuffer? maskFrame,
                                    double? sprocketThreshold)
    {
        float[] d = image.Data;
        bool[]? keep = KeepMaskFor(image, maskFrame, sprocketThreshold);

        var density = new double[keep is null ? d.Length : image.PixelCount * 3];
        int n = 0;
        for (int p = 0; p < image.PixelCount; p++)
        {
            if (keep is not null && !keep[p]) continue;
            for (int c = 0; c < 3; c++)
                density[n++] = FrameParams.DensityOf(d[p * 3 + c]);
        }
        if (n == 0) return 0.0;
        var used = new double[n];
        Array.Copy(density, used, n);
        return GuardedHighPercentile(used, 99.9);
    }

    /// <summary>
    /// The keep-mask for a measurement, or null when there is nothing to mask.
    ///
    /// Guards the size match itself: the mask is built on the raw frame, and a caller that hands
    /// in a differently-sized buffer would otherwise index past the end. Mismatched sizes mean the
    /// two are not the same view of the frame, so masking is skipped rather than misapplied.
    /// </summary>
    private static bool[]? KeepMaskFor(ImageBuffer image, ImageBuffer? maskFrame,
                                       double? sprocketThreshold)
    {
        if (maskFrame is null) return null;
        if (maskFrame.Width != image.Width || maskFrame.Height != image.Height) return null;
        return HighDensityKeepMask(maskFrame, sprocketThreshold);
    }

    /// <summary>
    /// <see cref="DetectDMax"/> resolved PER CHANNEL — each channel's own 99.9th density
    /// percentile rather than one percentile over all three pooled together.
    ///
    /// Pooling answers "how deep does this frame go", which is the right question for a scalar
    /// d_max. It is the wrong question for endpoints: the three channels reach different
    /// densities in the darkest area (that difference IS the highlight colour balance), and
    /// pooling averages it away before it can be measured.
    /// Must be called on the T_norm image (T / T_base), NOT on raw T.
    /// </summary>
    public static double[] DetectDMaxPerChannel(ImageBuffer image)
        => DetectDMaxPerChannel(image, null, null);

    /// <summary>
    /// <see cref="DetectDMaxPerChannel(ImageBuffer)"/> restricted to film pixels — see
    /// <see cref="DetectDMax(ImageBuffer, ImageBuffer?, double?)"/> for why the cut matters.
    ///
    /// It matters MORE per channel than for the scalar. A neutral blocking card sets all three
    /// endpoints to the same value, so the inversion — which divides each channel by its own
    /// endpoint — reads the roll's highlight balance off the card instead of off the film, and
    /// the highlight cast the per-channel model exists to capture is flattened away.
    /// </summary>
    public static double[] DetectDMaxPerChannel(ImageBuffer image, ImageBuffer? maskFrame,
                                                double? sprocketThreshold)
    {
        float[] d = image.Data;
        int n = image.PixelCount;
        bool[]? keep = KeepMaskFor(image, maskFrame, sprocketThreshold);

        var res = new double[3];
        var col = new double[n];
        for (int c = 0; c < 3; c++)
        {
            int k = 0;
            for (int p = 0; p < n; p++)
            {
                if (keep is not null && !keep[p]) continue;
                col[k++] = FrameParams.DensityOf(d[p * 3 + c]);
            }
            if (k == 0) return DetectDMaxPerChannel(image);   // all masked out → measure everything
            var used = new double[k];
            Array.Copy(col, used, k);
            res[c] = GuardedHighPercentile(used, 99.9);
        }
        return res;
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
        => SampleDMaxPerChannelFromRect(image, rect, tBase, blurSigma).Max();

    /// <summary>
    /// The same shadow-rect measurement, WITHOUT the collapse to a scalar — the highlight
    /// endpoint of each channel.
    ///
    /// This is not a new measurement. <see cref="SampleDMaxFromRect"/> already computes exactly
    /// this and then discards two of the three numbers with <c>.Max()</c>; the colour information
    /// it throws away is precisely what <c>wb_high</c> re-measures afterwards, from a different
    /// rect, using this same <c>RectMeanDensity</c> helper. One physical quantity, measured
    /// twice, is why the two ends compete and why calibration order changes the answer
    /// (THEORY.md step 5: residual 0.7 versus 0.04 depending on which is solved first).
    ///
    /// Keeping the three values instead is what lets the highlight endpoint and its colour cast
    /// be one fact rather than two — see <see cref="DensityEndpoints"/> for the algebra.
    /// </summary>
    public static double[] SampleDMaxPerChannelFromRect(
        ImageBuffer image, (double X, double Y, double W, double H) rect,
        double[] tBase, double blurSigma = 3.0)
        => RectMeanDensity(image, rect, tBase, blurSigma, "D_max sampling");

    /// <summary>
    /// The per-channel HIGHLIGHT ENDPOINT from a neutral rect in the highlights — three measured
    /// densities, which is what the inversion's white end is (see <see cref="DensityEndpoints"/>).
    ///
    /// It is the same measurement <see cref="SampleDMaxPerChannelFromRect"/> takes, and that is
    /// the point: highlight white balance and highlight endpoint are ONE quantity. This used to
    /// solve a multiplier <c>wb_high[c] = (max_d - off[c]) / D[c]</c> instead, which normalised
    /// away the very levels it was measuring — the three densities went in, a ratio came out, and
    /// the absolute endpoint had to be re-measured separately. Returning the densities keeps the
    /// endpoint and its colour cast a single fact and lets the caller write one field.
    ///
    /// The neutrality assertion has not gone anywhere; it has moved to where it belongs. Feeding
    /// these three unequal densities to <c>FromMeasured</c> gives each channel a slope that lands
    /// its own highlight on white, which is exactly what "this patch is neutral" means.
    /// </summary>
    public static double[] SampleWbHighFromRect(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                                double[] tBase, double blurSigma = 3.0)
    {
        double[] meanD = RectMeanDensity(image, rect, tBase, blurSigma, "WB sampling");
        if (meanD.Max() <= 0)
            // Report the numbers: "choose a denser area" is useless when the region IS picture and
            // the real cause is t_base. All three channels being non-positive means the patch is
            // more transmissive than the film base everywhere — on Path A that points at a t_base
            // sampled in the wrong place (or one whose channels the decouple matrix pushed down),
            // not at the rectangle. mean T per channel = t_base · 10^(−D).
            throw new ArgumentException(CoreText.F(
                $"采样区比片基还透光（三通道密度全 ≤ 0） · D = {meanD[0]:F3}, {meanD[1]:F3}, {meanD[2]:F3} · t_base = {tBase[0]:F4}, {tBase[1]:F4}, {tBase[2]:F4} · 若框的已是画面内容，多半是 t_base 偏暗，请重采片基"));

        Quantise(meanD);
        return meanD;
    }

    /// <summary>
    /// The per-channel SHADOW ENDPOINT from a rect that should reproduce neutral in the positive's
    /// SHADOWS — three measured densities, the black-end partner of
    /// <see cref="SampleWbHighFromRect"/>.
    ///
    /// Absolute densities, like the highlight end, rather than the additive nudge
    /// <c>max_d - D[c]</c> this used to return. And ORDER NO LONGER MATTERS: the old form had to
    /// be sampled before the highlight (darktable's rule) because the two were solved against each
    /// other; two independently measured endpoints cannot compete, so either may be sampled first
    /// or re-sampled alone.
    /// </summary>
    public static double[] SampleWbOffsetFromRect(ImageBuffer image, (double X, double Y, double W, double H) rect,
                                                  double[] tBase, double blurSigma = 3.0)
    {
        double[] meanD = RectMeanDensity(image, rect, tBase, blurSigma, "WB-offset sampling");
        Quantise(meanD);
        return meanD;
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
    /// The DENSE end's plateau guard — <see cref="BrightTailMean"/>'s rule pointed the other way.
    ///
    /// Walks a density-sorted order downward from the densest sample and returns the indices of
    /// the tail, SKIPPING any plateau of identical quantised density that alone supplies more
    /// than <paramref name="spike"/> samples while fewer than <paramref name="guard"/> have been
    /// passed. Skipped plateaus contribute to neither the tail nor the passed count.
    ///
    /// WHY THE HIGHLIGHT ENDPOINT NEEDS THIS. The film base end is bounded from above by the
    /// light board, so <see cref="BrightTailMean"/>'s guard is what stops the board's clipped
    /// plateau from BEING the tail. The highlight end has the mirror problem and no equivalent
    /// bound: an opaque sprocket edge, a blocking card, or any region crushed to code 0 lands on
    /// one density level — <see cref="FrameParams.DensityCeiling"/> or near it — and a single
    /// such plateau clears a 0.1% tail on its own. The existing defences each miss a case the
    /// other covers: <see cref="IsEndpointSample"/>'s ceiling test rejects only what reaches
    /// <see cref="FrameParams.RealDensityCeiling"/>, and the luma cuts need a cleanly bimodal
    /// histogram. A flat region sitting BELOW the ceiling but above the picture — an
    /// under-illuminated corner, a partially transmissive card, a scanner surround — passes both
    /// and then defines the roll's white point.
    ///
    /// Real picture tone carries grain and an illumination gradient, so it spreads across levels
    /// and survives; a synthetic flat fill does not, which is the discrimination being made.
    ///
    /// Returns null when the guard consumed everything — callers retry unguarded rather than lose
    /// the frame, matching <see cref="CoSitedFilmBase"/>.
    /// </summary>
    /// <param name="sortedKeys">Ranking values, ASCENDING (densest last).</param>
    /// <param name="order">Sample indices permuted alongside <paramref name="sortedKeys"/>.</param>
    /// <param name="count">Number of valid entries in both arrays.</param>
    /// <param name="tailCount">How many samples the tail should hold.</param>
    /// <param name="spike">Plateau size above which a level is skipped.</param>
    /// <param name="guard">Passed-sample count beyond which skipping stops.</param>
    private static int[]? DenseTailIndices(double[] sortedKeys, int[] order, int count,
                                           int tailCount, double spike, double guard)
    {
        var taken = new int[tailCount];
        int n = 0;
        double passed = 0;
        int i = count - 1;
        while (i >= 0 && n < tailCount)
        {
            long level = QuantiseDensity(sortedKeys[i]);
            int j = i;
            while (j >= 0 && QuantiseDensity(sortedKeys[j]) == level) j--;
            int run = i - j;                              // the plateau occupies (j, i]

            if (run > spike && passed < guard) { i = j; continue; }

            for (int k = i; k > j && n < tailCount; k--) taken[n++] = order[k];
            passed += run;
            i = j;
        }
        if (n == 0) return null;
        if (n < tailCount) Array.Resize(ref taken, n);
        return taken;
    }

    /// <summary>
    /// Density levels at the granularity a 16-bit scan can actually distinguish, so "identical
    /// value" means the same thing here as <see cref="QuantiseLuma"/> means at the other end.
    ///
    /// Density is unbounded above where luma is not, so the scale is fixed to
    /// <see cref="FrameParams.DensityCeiling"/> rather than to 1.0: everything at or above the
    /// ceiling — every fully light-blocking pixel — collapses into the top level, which is
    /// precisely the plateau the guard exists to drop.
    /// </summary>
    private static long QuantiseDensity(double density)
        => (long)(Math.Clamp(density / FrameParams.DensityCeiling, 0.0, 1.0) * 65535.0);

    /// <summary>
    /// The plateau-size and release thresholds for a tail of <paramref name="tailFraction"/> of
    /// <paramref name="count"/> samples.
    ///
    /// THE THRESHOLD IS THE TAIL ITSELF, not a fixed share of the frame. What makes a plateau
    /// dangerous is that it can fill the tail ALONE — at which point it is the entire measurement
    /// and the picture never gets a vote. That condition is "bigger than the tail", so that is
    /// what is tested, with a 2× margin so an ordinary tone that merely happens to be tail-sized
    /// is not mistaken for a flat fill.
    ///
    /// NexFilm's published constants (skip a bin holding &gt;10% of samples while under 20%
    /// accumulated) are stated against their 1% tail, where 10% of the frame IS ten tails. Read
    /// as a share of the FRAME the rule does not transfer: our highlight tail is 0.1%, so a fixed
    /// 10% would ignore any block up to a hundred times the tail's size — measured, a 300-pixel
    /// flat block against a 40-pixel tail sailed through and took the endpoint (2.80 against the
    /// picture's true 2.60). Read as a share of the TAIL it transfers exactly, which is the form
    /// used here and the one <see cref="CoSitedFilmBase"/> already relies on for its 0.01%
    /// no-board branch.
    ///
    /// The release threshold stays 2× the skip threshold, preserving the ratio NexFilm's 10/20
    /// pair encodes: once enough samples have been passed that the tail is no longer at the
    /// mercy of one level, stop skipping — otherwise a genuinely flat subject could be skipped
    /// indefinitely and the walk would run off the end of the picture.
    /// </summary>
    private static (double Spike, double Guard) SpikeThresholds(int count, double tailFraction)
    {
        double tail = count * tailFraction;
        return (tail * 2.0, tail * 4.0);
    }

    /// <summary>
    /// A high percentile of <paramref name="vals"/> with <see cref="DenseTailIndices"/>' plateau
    /// guard applied — the single-array form of the guarded tail, for the callers that measure
    /// one pooled population rather than co-sited triples.
    ///
    /// Equivalent to <c>Percentile(vals, q)</c> once the guard has nothing to skip, so a frame
    /// with no flat block reads exactly as it did before. The returned value is the guarded
    /// tail's LEAST-dense member, which is where the percentile itself sits: the tail holds the
    /// top (100−q)% and the percentile is its lower boundary.
    /// </summary>
    /// <param name="vals">Densities. Not mutated.</param>
    /// <param name="q">Percentile in [0,100], intended for the dense end.</param>
    private static double GuardedHighPercentile(double[] vals, double q)
    {
        int n = vals.Length;
        if (n == 0) return 0.0;

        double tailFraction = Math.Max(100.0 - q, 0.0) / 100.0;
        int tail = Math.Clamp((int)Math.Ceiling(n * tailFraction), 1, Math.Max(1, n / 2));

        var keys = (double[])vals.Clone();
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(keys, order);

        var (spike, guard) = SpikeThresholds(n, tailFraction);
        int[]? idx = DenseTailIndices(keys, order, n, tail, spike, guard);
        // Guard ate everything (a frame that is one flat level end to end): fall back to the
        // plain percentile rather than report a density nothing in the frame has.
        if (idx is null || idx.Length == 0) return Percentile(vals, q);

        double least = double.PositiveInfinity;
        foreach (int i in idx) if (vals[i] < least) least = vals[i];
        return least;
    }

    /// <summary>
    /// Auto-estimate the per-channel HIGHLIGHT ENDPOINT by finding the roll's brightest
    /// highlight — the negative's DENSEST (darkest) real picture pixel — and taking that point's
    /// three densities as the white end. Physics: on a negative the densest pixel is the scene's
    /// brightest highlight (most light → most dye → most opaque); assuming it should reproduce as
    /// neutral white, the inversion maps each channel's own density there onto white, which
    /// balances the channels at that point. This is the auto counterpart to box-selecting a
    /// neutral highlight for <see cref="SampleWbHighFromRect"/> — only the region is found
    /// automatically, and both return the same quantity so auto and manual agree.
    ///
    /// Returns absolute densities, not a multiplier: the endpoint and its colour cast are one
    /// fact (see <see cref="DensityEndpoints"/>), so this writes
    /// <see cref="FrameParams.DMaxPerChannel"/> directly.
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
    /// <param name="valueImages">Optional, index-aligned with <paramref name="images"/>. Given →
    /// masks key off <paramref name="images"/> (raw luma) but the sampled VALUES come from here
    /// (the post-decouple domain on Path A) — the same convention
    /// <see cref="EstimateTBaseFromRoll"/> uses. Null → values come from the frames themselves.</param>
    public static double[] AutoWbHighFromRoll(IReadOnlyList<ImageBuffer> images,
                                              double[] tBase,
                                              double? sprocketThreshold = null,
                                              IReadOnlyList<ImageBuffer>? valueImages = null,
                                              double edgeInset = 0.05,
                                              double highlightPct = 99.5)
    {
        double[] bestDensity = HighlightDensityFromRoll(images, tBase, sprocketThreshold,
                                                        valueImages, edgeInset, highlightPct);
        Quantise(bestDensity);
        return bestDensity;
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

        // Every frame's highlight triple; the choice between them happens after the loop.
        var candidates = new List<double[]>();

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
            // One quantisation step in each channel's normalised units, as in the detector.
            double step = SourceStep(insetVal);
            var stepN = new double[3];
            for (int c = 0; c < 3; c++) stepN[c] = step / tb[c];

            for (int p = 0; p < n; p++)
            {
                if (!keep[p]) continue;
                double sum = 0;
                bool clamped = false;
                for (int c = 0; c < 3; c++)
                {
                    double tc = vd[p * 3 + c] / tb[c];
                    double dc = FrameParams.DensityOf(tc);
                    // A channel pinned at the clamp underflowed to zero transmittance: the value
                    // is a floor artefact, not a density, and this method's whole output is a
                    // per-channel MEAN over the top tail, so one fabricated 4.0 lands directly in
                    // the answer. Marked here rather than tested after the fact because the
                    // rejection below ranks on TOTAL density, which a single clamped channel does
                    // not lift past the ceiling — see IsEndpointSample.
                    if (dc >= FrameParams.DensityCeiling) clamped = true;
                    // And a channel too coarsely quantised to resolve its own density is the same
                    // kind of non-measurement one step short of the clamp — see IsResolved.
                    if (step > 0 && !IsResolved(tc, stepN[c])) clamped = true;
                    dens[k * 3 + c] = dc;
                    sum += dc;
                }
                // Route it into the existing rejection rather than adding a second pass: that
                // filter already drops everything at or above RealDensityCeiling, so pushing the
                // total to the clamp marks this sample without duplicating the compaction below.
                totalD[k] = clamped ? FrameParams.DensityCeiling : sum / 3.0;
                k++;
            }

            // Reject opaque sprocket / film-frame BLACK edges before picking the highlight.
            // These are fully light-blocking (t_norm → 0), so their density lands on
            // FrameParams.DensityCeiling (4.0), above any real picture tone (~1–1.5). Both the
            // bright cut and the dark valley miss them on rolls where the user kept the sprockets
            // in frame and the valley returned its no-op sentinel — and "pick max density" then
            // locks onto dead black instead of the highlight.
            int realCount = 0;
            for (int i = 0; i < keptCount; i++) if (totalD[i] < FrameParams.RealDensityCeiling) realCount++;
            if (realCount > 0 && realCount < keptCount)
            {
                var d2 = new double[realCount * 3];
                var t2 = new double[realCount];
                int j = 0;
                for (int i = 0; i < keptCount; i++)
                {
                    if (!(totalD[i] < FrameParams.RealDensityCeiling)) continue;
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

            candidates.Add(hiD);
        }

        if (candidates.Count == 0)
            throw new ArgumentException("AutoWbHighFromRoll: all pixels were masked out");

        // Same choice, same reasoning, same helper as DetectDMaxPerChannelFromRoll: the densest
        // frame whose highlight COLOUR agrees with the roll. Picking on density alone let one
        // frame with a strongly coloured highlight set the whole roll's channel ratios. These two
        // estimators answer the same question and feed the same field, so they must not disagree
        // about which frame speaks for the roll.
        return candidates[PickRepresentativeFrame(candidates)];
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
        return outImg.InheritSourceFrom(src);
    }

    /// <summary>
    /// scipy.ndimage.binary_dilation(mask, iterations=radius) with the default structure —
    /// the 4-neighbour cross, so N iterations reach every pixel within MANHATTAN distance N.
    /// Computed as an exact city-block distance transform (two chamfer passes, O(n)) rather
    /// than N dilation passes, which would be O(n·N).
    /// </summary>
    /// <summary>
    /// How far the light board's contaminated band actually reaches, in pixels, MEASURED off this
    /// frame rather than assumed.
    ///
    /// WHY THIS REPLACED A CONSTANT. The dilation used to be a flat 5% of the short edge — a
    /// guess about an optical property, tied to a quantity (the frame's pixel dimensions) that the
    /// property does not depend on. The same physical scan therefore dilated differently at
    /// preview and full resolution. Measuring makes it resolution-correct for free.
    ///
    /// WHAT IS BEING MEASURED, AND WHY IT IS NOT BRIGHTNESS. The first version of this walked
    /// outward watching the median LUMA settle, on the reasoning that the penumbra is a
    /// brightness gradient. That reads the wrong quantity and stops far too early. Measured on
    /// 图像 001a at 873×1120, per distance ring:
    ///
    /// <code>
    ///   ring   median luma   max density
    ///      1        0.095        1.78
    ///      2        0.038        2.61      ← luma already looks like film
    ///      3        0.039        2.91
    ///      4        0.043        2.68
    ///      5        0.045        2.97
    ///      6        0.047        1.44      ← density finally drops to the film's own level
    ///     48        0.046        1.48
    /// </code>
    ///
    /// Rings 2–5 have the luma of film and the density of the opaque frame edge. A median cannot
    /// see that: the contaminating pixels are a small minority of each ring, so they never move
    /// its centre, and the luma test declared the shoulder over at 2 px. The endpoint solve then
    /// took its highlight from those rings and the frame's white end moved from 1.63 to 2.97 —
    /// set by the edge of the film holder rather than by any photograph.
    ///
    /// So the ring is judged by its DENSITY EXTREME, which is exactly what the endpoint consumes:
    /// walk outward until a ring's peak density comes back down to what the far field reaches.
    /// The transition is sharp — 2.97 to 1.44 between rings 5 and 6 — so the stopping point is
    /// not sensitive to the tolerance.
    ///
    /// A minimum of 1 keeps the immediate boundary pixel out regardless, and the cap keeps a
    /// pathological frame from dilating half the picture away.
    /// </summary>
    /// <param name="frame">The frame the densities are read from.</param>
    /// <param name="board">Board mask — true where luma cleared the bright cut.</param>
    private static int BoardShoulderRadius(ImageBuffer frame, bool[] board, int w, int h)
    {
        int n = w * h;
        int[] dist = DistanceFrom(board, w, h);

        // Bucket every pixel's DENSITY EXTREME by its ring, in ONE pass. Scanning the frame once
        // per candidate radius is O(n·maxR) — on a 24 MP frame with a 5% cap that is billions of
        // comparisons, and this runs inside the interactive auto-calibrate.
        //
        // Density, not luma, and the ring's MAXIMUM, not its median — see the remarks.
        int maxR = Math.Max(1, (int)(Math.Min(w, h) * MaxShoulderFraction));
        var peak = new double[maxR + 1];
        var seen = new bool[maxR + 1];
        float[] d = frame.Data;
        for (int p = 0; p < n; p++)
        {
            int r = dist[p];
            if (r < 1 || r > maxR) continue;
            seen[r] = true;
            for (int c = 0; c < 3; c++)
            {
                double density = FrameParams.DensityOf(d[p * 3 + c]);
                if (density > peak[r]) peak[r] = density;
            }
        }

        // The far field's own peak: what an uncontaminated ring of this frame looks like. Taken
        // from the outermost rings the cap allows, which are past any plausible penumbra.
        double reference = 0;
        int refFrom = Math.Max(1, maxR - maxR / 4);
        for (int r = refFrom; r <= maxR; r++) if (seen[r] && peak[r] > reference) reference = peak[r];
        if (reference <= 0) return 1;

        // Walk outward and stop at the first ring whose peak has come down to what the far field
        // reaches. Everything before it is still carrying the opaque edge.
        for (int r = 1; r <= maxR; r++)
        {
            if (!seen[r]) break;                          // ran out of frame
            if (peak[r] <= reference * (1.0 + ShoulderSettleTolerance))
                return Math.Max(1, r - 1);
        }
        return maxR;
    }

    /// <summary>
    /// How far above the far field's own density peak a ring may still reach and count as clean,
    /// as a fraction of that peak. The contaminated rings on 图像 001a peak at 2.6–3.0 against a
    /// far field of ~1.48 — 75% to 100% above it — while clean rings sit within a couple of
    /// percent of each other all the way out. 15% separates them with a wide margin on both
    /// sides.
    /// </summary>
    private const double ShoulderSettleTolerance = 0.15;

    /// <summary>Ceiling on the measured shoulder, as a fraction of the short edge — the old fixed
    /// rule, kept only as a backstop for a frame whose luma never settles.</summary>
    private const double MaxShoulderFraction = 0.05;

    /// <summary>Chebyshev distance to the nearest true pixel — the two-pass transform
    /// <see cref="Dilate"/> already used, split out so both can share it.</summary>
    private static int[] DistanceFrom(bool[] mask, int w, int h)
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
        return dist;
    }

    private static bool[] Dilate(bool[] mask, int w, int h, int radius)
    {
        int[] dist = DistanceFrom(mask, w, h);
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
                sum[c] += FrameParams.DensityOf(patch[p * 3 + c] / tb[c]);
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
