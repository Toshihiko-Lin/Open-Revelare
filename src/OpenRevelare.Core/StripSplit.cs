namespace OpenRevelare.Core;

/// <summary>
/// Splits a scan holding several negatives into one rect per frame — the import-time
/// pre-pass for scanner TIFFs, where a single file is a whole strip rather than one photo.
///
/// Detection is scanner-agnostic: it reads the film's own structure and uses no holder
/// catalogue, no assumed scan extent and no DPI. That is deliberate. The X5-Crop route was
/// evaluated first and derives its scale from a known Hasselblad/Imacon holder; every real
/// sample scan tried against it is a strip trimmed in Photoshop whose aspect (≈2.6) is
/// nowhere near a full holder (≈7.2), and it detected nothing at all on any of them.
///
/// The signal used instead is that a gutter — the bare film base between two frames — is
/// FLAT AND BRIGHT. Both halves are load-bearing:
///
/// * Flat alone fails. An overcast sky is as smooth as the base (σ≈0.011 for both on one
///   sample), so flatness on its own reports five frames where there are two.
/// * Bright alone fails. Low-contrast scans put picture mid-tones near the base level.
///
/// Together they separate cleanly, because bare film base is unexposed — on a negative that
/// is the brightest thing on the strip — while carrying no image detail.
///
/// Measured 9 of 11 on real scans from two different scanners, median 20 ms on a
/// downsampled preview. The two misses are mild over-splits (one frame reported as two),
/// which the split dialog lets the user fix by deleting a divider. Callers are expected to
/// present the result for confirmation rather than apply it blind.
/// </summary>
public static class StripSplit
{
    /// <summary>One detected frame: normalised rect in [0,1], origin top-left, in the
    /// SOURCE image's own axes (already un-rotated if the strip ran horizontally).</summary>
    public readonly record struct Rect(double X, double Y, double W, double H);

    /// <summary>
    /// Locate the frames in <paramref name="image"/>.
    ///
    /// Returns an empty list when the image holds no recognisable strip; a single rect when
    /// the scan is one frame. Callers should treat a count of 0 or 1 as "nothing to split"
    /// rather than as an error.
    /// </summary>
    /// <param name="image">Decoded scan, any resolution. A downsampled preview is expected
    /// and sufficient — the rects are normalised, so they apply unchanged at full size.</param>
    /// <param name="minFrameFraction">Spans shorter than this fraction of the strip are
    /// discarded as sprocket gaps or leader rather than kept as frames.</param>
    public static IReadOnlyList<Rect> Detect(ImageBuffer image, double minFrameFraction = 0.05)
    {
        // Work with the strip running down the rows, whatever way round it was scanned;
        // the mapping back to source axes happens at the end.
        bool vertical = image.Height >= image.Width;
        int length = vertical ? image.Height : image.Width;   // along the strip
        int across = vertical ? image.Width : image.Height;
        if (length < 16 || across < 8) return Array.Empty<Rect>();

        float[] luma = Luma(image, vertical, length, across);

        var (c0, c1) = StripBounds(luma, length, across);
        if (c1 - c0 < 8) return Array.Empty<Rect>();

        // Central half of the strip only: sprocket holes and the ragged film edges are flat
        // and bright too, and would otherwise read as gutters running the whole length.
        int lo = (int)(c0 + 0.25 * (c1 - c0));
        int hi = (int)(c0 + 0.75 * (c1 - c0));
        if (hi - lo < 2) return Array.Empty<Rect>();

        // Per-row flatness and level across that band, smoothed along the strip so a single
        // grainy row cannot fragment one gutter into several short runs.
        var (sd, mean) = RowStats(luma, length, across, lo, hi);
        // Odd width so the box is symmetric about the row it replaces. An even width would
        // bias every smoothed value half a sample along the strip, which walks the detected
        // boundaries and, near a marginal gutter, changes how many frames come out.
        int window = Math.Max(3, length / 200) | 1;
        Smooth(sd, window);
        Smooth(mean, window);

        bool[] gutter = GutterMask(sd, mean, out bool[] blank);
        if (gutter is null) return Array.Empty<Rect>();

        var (f0, f1) = FilmExtent(sd, length);
        var spans = FrameSpans(gutter, blank, length, minFrameFraction, f0, f1);
        return ToRects(spans, vertical, c0, c1, length, across);
    }

    /// <summary>Mean luma, transposed if needed so index [row * across + col] walks along
    /// the strip in <c>row</c> and across it in <c>col</c>.</summary>
    private static float[] Luma(ImageBuffer image, bool vertical, int length, int across)
    {
        var luma = new float[length * across];
        float[] d = image.Data;
        int w = image.Width;
        Parallel.For(0, length, r =>
        {
            for (int c = 0; c < across; c++)
            {
                // vertical: (row, col) = (r, c); horizontal: the image is walked transposed.
                int px = vertical ? (r * w + c) : (c * w + r);
                int b = px * 3;
                luma[r * across + c] = (d[b] + d[b + 1] + d[b + 2]) / 3.0f;
            }
        });
        return luma;
    }

    /// <summary>
    /// The columns spanned by the film, discarding the black surround.
    ///
    /// The cut is placed low — a fraction of the way from the dark end to the median — and
    /// NOT midway between min and max. A sprocket hole is bare light source and far brighter
    /// than the film (0.75 against ≈0.36 on one sample); a min/max midpoint therefore lands
    /// above the film itself and selects only the sprockets, 30 columns out of 486.
    /// The widest run above the cut is the film, which also ignores specks in the surround.
    /// </summary>
    private static (int Lo, int Hi) StripBounds(float[] luma, int length, int across)
    {
        var col = new float[across];
        for (int c = 0; c < across; c++)
        {
            double acc = 0;
            for (int r = 0; r < length; r++) acc += luma[r * across + c];
            col[c] = (float)(acc / length);
        }

        double dark = NumpyStats.Percentile(col, 5.0);
        double mid = NumpyStats.Median(col);
        double cut = dark + 0.45 * (mid - dark);

        int bestLo = 0, bestHi = 0, curLo = -1;
        for (int c = 0; c < across; c++)
        {
            bool lit = col[c] > cut;
            if (lit && curLo < 0) curLo = c;
            else if (!lit && curLo >= 0)
            {
                if (c - curLo > bestHi - bestLo) { bestLo = curLo; bestHi = c; }
                curLo = -1;
            }
        }
        if (curLo >= 0 && across - curLo > bestHi - bestLo) { bestLo = curLo; bestHi = across; }
        return (bestLo, bestHi);
    }

    /// <summary>Per-row standard deviation and mean over the band [lo, hi).</summary>
    private static (double[] Sd, double[] Mean) RowStats(float[] luma, int length, int across,
                                                         int lo, int hi)
    {
        var sd = new double[length];
        var mean = new double[length];
        int n = hi - lo;
        Parallel.For(0, length, r =>
        {
            int b = r * across;
            double sum = 0, sumSq = 0;
            for (int c = lo; c < hi; c++)
            {
                double v = luma[b + c];
                sum += v;
                sumSq += v * v;
            }
            double m = sum / n;
            mean[r] = m;
            sd[r] = Math.Sqrt(Math.Max(sumSq / n - m * m, 0.0));
        });
        return (sd, mean);
    }

    /// <summary>Centred box mean of width <paramref name="window"/>, in place, edge-clamped.</summary>
    private static void Smooth(double[] v, int window)
    {
        var src = (double[])v.Clone();
        int half = window / 2;
        for (int i = 0; i < v.Length; i++)
        {
            double acc = 0;
            int count = 0;
            for (int t = -half; t <= half; t++)
            {
                int j = i + t;
                if (j < 0 || j >= src.Length) continue;
                acc += src[j];
                count++;
            }
            v[i] = acc / count;
        }
    }

    /// <summary>Rows that are gutter (or blank leader), by the flat-and-bright rule.</summary>
    private static bool[] GutterMask(double[] sd, double[] mean, out bool[] blank)
    {
        int n = sd.Length;

        // Blank scanner output — clipped white with no grain whatsoever — is not film and
        // must not set the scale for anything. Real film base always carries grain, so an
        // exactly-zero deviation identifies the blank region without touching real frames.
        // Left in, such a block lifts the brightness bar above the genuine gutters and every
        // one of them is missed.
        blank = new bool[n];
        var live = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            blank[i] = mean[i] > 0.99 && sd[i] < 1e-4;
            if (!blank[i]) live.Add(mean[i]);
        }
        if (live.Count == 0) return null!;

        double p5 = NumpyStats.Percentile(sd, 5.0);
        double p95 = NumpyStats.Percentile(sd, 95.0);
        double flatCut = p5 + 0.30 * (p95 - p5);

        // Two independent readings of the base level, combined by taking the lower. p98 of
        // live rows is the simple one, but where the picture fills most of the strip it lands
        // in bright picture content and sets the bar too high, merging two frames into one.
        // The level of the flattest live rows targets the base directly, but alone it sits
        // too low elsewhere and splits frames. The lower of the two is the safer bar.
        double[] liveArr = live.ToArray();
        double med = NumpyStats.Median(liveArr);
        double top = NumpyStats.Percentile(liveArr, 98.0);

        double sdQuiet = NumpyStats.Percentile(sd, 20.0);
        var calm = new List<double>();
        for (int i = 0; i < n; i++)
            if (!blank[i] && sd[i] <= sdQuiet) calm.Add(mean[i]);
        if (calm.Count > 0)
            top = Math.Min(top, NumpyStats.Percentile(calm.ToArray(), 90.0));

        double brightBar = (med + Math.Max(top, med)) / 2.0;

        var mask = new bool[n];
        for (int i = 0; i < n; i++)
            mask[i] = blank[i] || (sd[i] < flatCut && mean[i] > brightBar);
        return mask;
    }

    /// <summary>
    /// How far the FILM runs along the strip: the dark surround before it and the blank scanner
    /// output after it are not part of any photograph.
    ///
    /// This is <see cref="StripBounds"/> turned ninety degrees, and it exists because without it
    /// the outermost frames are declared to run to the file edge whatever is there. Measured over
    /// the sample scans that was the detector's one systematic error — interior dividers land on
    /// real film base almost every time, while the two ends were pinned to row 0 and row length-1:
    /// eight of fifteen scans began the first frame in the black surround and four ended the last
    /// one in blank white, on one file leaving 25% of the strip outside the box.
    ///
    /// Judged on VARIANCE alone, deliberately. Both non-film regions are machine-flat — the
    /// surround at level ≈0.05 and the blank at 1.00 both measure σ≈0.0000 — while real film never
    /// is: even bare base carries grain at σ≈0.002, an order of magnitude up. A brightness rule
    /// cannot express that (the two regions sit at opposite ends of the scale, with the film
    /// between them), and a brightness PERCENTILE is worse still: picture rows dominate the
    /// distribution, so the cut lands mid-picture and truncates the film. That was tried first and
    /// collapsed 3.tif from six frames to two.
    /// </summary>
    private static (int Lo, int Hi) FilmExtent(double[] sd, int length)
    {
        // Absolute, not a percentile: this separates "a machine wrote this" from "light passed
        // through film", and that boundary does not move with the content of the scan.
        const double dead = 5e-4;

        int bestLo = 0, bestHi = 0, curLo = -1;
        for (int i = 0; i <= length; i++)
        {
            bool isFilm = i < length && sd[i] > dead;
            if (isFilm) { if (curLo < 0) curLo = i; }
            else if (curLo >= 0)
            {
                if (i - curLo > bestHi - bestLo) { bestLo = curLo; bestHi = i; }
                curLo = -1;
            }
        }

        // Widest run, not the first: a speck in the surround or a sliver of leader must not claim
        // the strip. Nothing separable (a scan trimmed flush to the film, which is common) leaves
        // the whole image, which is the old behaviour and correct for that case.
        return bestHi - bestLo < length / 20 ? (0, length) : (bestLo, bestHi);
    }

    /// <summary>Frames are what lies between gutters, after pitch-based repair.</summary>
    /// <param name="filmLo">First row of film — where the first frame starts, rather than row 0.</param>
    /// <param name="filmHi">One past the last row of film — where the last frame ends.</param>
    private static List<(int Lo, int Hi)> FrameSpans(bool[] gutter, bool[] blank, int length,
                                                     double minFrameFraction, int filmLo, int filmHi)
    {
        int minGutter = Math.Max(3, length / 250);
        var cuts = new List<int> { filmLo };
        int runLo = -1;
        // Confined to the film: a flat, bright run out in the blank tail is not a boundary between
        // two photographs, and treating it as one is what produced a final "frame" of pure white.
        for (int i = filmLo; i < filmHi; i++)
        {
            if (gutter[i]) { if (runLo < 0) runLo = i; }
            else if (runLo >= 0)
            {
                if (i - runLo >= minGutter) cuts.Add((runLo + i) / 2);
                runLo = -1;
            }
        }
        if (runLo >= 0 && filmHi - runLo >= minGutter) cuts.Add((runLo + filmHi) / 2);
        cuts.Add(filmHi);

        int minLen = (int)(minFrameFraction * length);
        var spans = new List<(int Lo, int Hi)>();
        for (int i = 0; i < cuts.Count - 1; i++)
        {
            int s = cuts[i], e = cuts[i + 1];
            if (e - s < minLen) continue;
            // A span that is mostly blank holds no photograph — it is the unexposed tail
            // past the last frame. Without this it is reported as one more frame.
            int blankCount = 0;
            for (int r = s; r < e; r++) if (blank[r]) blankCount++;
            if (blankCount * 2 >= e - s) continue;
            spans.Add((s, e));
        }
        return MergeToPitch(spans);
    }

    /// <summary>
    /// Glue fragments back together so every span is about one frame pitch long.
    ///
    /// Frames on a strip are evenly pitched, so the true frame length is the median span
    /// length — fragments cannot outvote it as long as most frames survive intact. Anything
    /// much shorter is a piece of a frame split by a false gutter (a blown highlight band
    /// inside the picture), so it is absorbed into its neighbour.
    /// </summary>
    private static List<(int Lo, int Hi)> MergeToPitch(List<(int Lo, int Hi)> spans)
    {
        if (spans.Count < 3) return spans;

        var lengths = spans.Select(s => s.Hi - s.Lo).OrderBy(v => v).ToArray();
        int pitch = lengths[lengths.Length / 2];
        if (pitch <= 0) return spans;

        var merged = new List<(int Lo, int Hi)>();
        foreach (var (s, e) in spans)
        {
            if (merged.Count > 0 && merged[^1].Hi - merged[^1].Lo < 0.6 * pitch)
                merged[^1] = (merged[^1].Lo, e);
            else
                merged.Add((s, e));
        }
        // A trailing fragment has no successor to absorb it; fold it backwards.
        if (merged.Count > 1 && merged[^1].Hi - merged[^1].Lo < 0.6 * pitch)
        {
            merged[^2] = (merged[^2].Lo, merged[^1].Hi);
            merged.RemoveAt(merged.Count - 1);
        }

        // A second pass against the merged pitch. The first measures pitch on fragmented
        // spans, so where one frame was cut into three the median is itself a fragment and
        // the pieces survive. Re-measuring after the merge gives the true frame length.
        if (merged.Count >= 2)
        {
            var merged2 = merged.Select(s => s.Hi - s.Lo).OrderBy(v => v).ToArray();
            int pitch2 = merged2[merged2.Length / 2];
            var outSpans = new List<(int Lo, int Hi)>();
            foreach (var (s, e) in merged)
            {
                if (outSpans.Count > 0 && e - s < 0.65 * pitch2)
                    outSpans[^1] = (outSpans[^1].Lo, e);
                else
                    outSpans.Add((s, e));
            }
            if (outSpans.Count > 1 && outSpans[^1].Hi - outSpans[^1].Lo < 0.65 * pitch2)
            {
                outSpans[^2] = (outSpans[^2].Lo, outSpans[^1].Hi);
                outSpans.RemoveAt(outSpans.Count - 1);
            }
            merged = outSpans;
        }
        return merged;
    }

    /// <summary>Map spans back to normalised rects in the SOURCE image's axes.</summary>
    private static IReadOnlyList<Rect> ToRects(List<(int Lo, int Hi)> spans, bool vertical,
                                               int c0, int c1, int length, int across)
    {
        var rects = new List<Rect>(spans.Count);
        foreach (var (s, e) in spans)
        {
            double alongPos = (double)s / length, alongSize = (double)(e - s) / length;
            double crossPos = (double)c0 / across, crossSize = (double)(c1 - c0) / across;
            rects.Add(vertical
                ? new Rect(crossPos, alongPos, crossSize, alongSize)
                : new Rect(alongPos, crossPos, alongSize, crossSize));
        }
        return rects;
    }
}
