namespace OpenRevelare.Core;

/// <summary>
/// Geometry of the two 135 perforation rows in one image, as measured from the perforations
/// themselves. Film is punched to a standard, so this is a ruler: the row spacing is 28.17 mm and
/// the hole pitch along the strip is 4.75 mm, which together give both pixel scales and the tilt
/// of the strip without needing the exposure borders.
///
/// <para><see cref="CentrePx"/> is the across-axis position of the row-pair centre at the
/// ALONG-AXIS MIDPOINT of the analysed frame, in that frame's pixels. <see cref="Slope"/> carries
/// the tilt so a band can follow a strip that was photographed at an angle.</para>
/// </summary>
public readonly record struct PerforationRuler(
    bool Horizontal,
    double ScalePxPerMm,
    double CentrePx,
    double AcrossPxPerMm,
    double Angle)
{
    /// <summary>Across-axis pixels per along-axis pixel, i.e. the row's tilt. Derived from
    /// <see cref="Angle"/> with the same sign convention as <see cref="Geometry.ApplyRotation"/>'s
    /// correction; ±90° is outside the detector's domain and is not produced.</summary>
    public double Slope => Horizontal ? Math.Tan(Angle * Math.PI / 180.0)
                                      : -Math.Tan(Angle * Math.PI / 180.0);
}

/// <summary>One frame's two perforation references: the white end (light through the holes)
/// and the black span above it (the unexposed film between holes), in densities relative to
/// that white end. <see cref="BlackSpan"/> is null when the complementary band is not a
/// trustworthy unexposed black — the frame then contributes only its white end.</summary>
public readonly record struct PerforationSample(double[] White, double[]? BlackSpan);

/// <summary>
/// Finding the perforation band and sampling the two references it carries: the lamp through the
/// holes (the white end) and the unexposed film between them (the black end).
///
/// <para>WHY THE PERFORATIONS AND NOT THE PICTURE. On a reversal (positive) film the white end is
/// <c>D_min</c> — the clearest material on the strip. The picture itself is the wrong place to
/// look for it: its brightest patch is whatever the photographer pointed at (a white wall, fog, a
/// specular highlight), which varies frame to frame and can be coloured. The perforations are
/// punched clean through the film, so the light recorded there passed through no dye at all: every
/// frame of a roll saw the same lamp through the same holes. That makes them a roll-constant white
/// reference by construction.</para>
///
/// <para>THE SAME BAND IS THE BLACK REFERENCE. The film between and around the holes is outside
/// the camera gate, so on a reversal film it developed to the strip's D_max. Its dark tail is a
/// physical black end — but only when it is genuinely dark and neutral; <see cref="Measure"/>
/// returns a null black span for a clear (negative), fogged or stray-lit edge.</para>
///
/// <para>THE NUMBERS ARE CAMERA-NATIVE. A preview here is a UniWB RAW decode, so a neutral lamp
/// still reads UNEQUAL in R/G/B — the sensor's own channel response (on the sample Sony roll the
/// holes read about 0.32 / 0.70 / 0.41 against as-shot coefficients of 2.30 / 1.00 / 1.70, which
/// is the inverse relationship, to within the light table's own tint). That spread is the light's
/// colour in the numbers the inversion divides, and it is what makes the render neutral; it is
/// not a cast. Do not "neutralise" it here.</para>
/// </summary>
public static class FilmPerforations
{
    // SMPTE ST 93 / ISO 491: lateral displacement between the two perforation rows.
    public const double RowSpacingMm = 28.17;
    // ISO 491: perforation pitch along the strip.
    public const double HolePitchMm = 4.75;
    // The hole opening across the strip is ~1.98 mm on a 135 negative, but the light through a
    // hole blooms and the film edge is soft after copy-stand imaging. A 2.8 mm band is a superset
    // of the hole and still clears the 24 mm image area (its inner edge is 12.69 mm from the strip
    // centre), so the extra is dense unexposed film that the bright tail never selects.
    public const double HoleHeightMm = 2.8;

    /// <summary>Detection is done on at most this edge; a full-resolution RAW would only add cost,
    /// not row structure. Returned coordinates are scaled back to the source's pixels.</summary>
    private const int DetectorMaxEdge = 1200;

    private const double TailFraction = 0.02;

    // The black end comes from the COMPLEMENT of the holes: the unexposed film bridges between
    // them and the margins around each hole. The gate below is what keeps a clear (negative) or
    // stray-lit edge from being reported as an unexposed black; measured on the real 81-frame
    // folder, the positive segment reads 2.85/2.80/2.78 D and a clear-edge segment 1.19/1.22/1.30.
    private const double BlackTailFraction = 0.02;
    /// <summary>Pixels darker than this fraction of the hole light count as the non-hole band.</summary>
    private const double HoleLightFraction = 0.5;
    /// <summary>The 90th percentile of the non-hole band must stay below this fraction of the hole
    /// light. This is the mask-sanity check: if the hole pixels were let into the complement, the
    /// non-hole sample would be mostly lamp and this fails.</summary>
    private const double MaxNonHoleP90Fraction = 0.5;
    private const int MinBlackPixels = 128;
    /// <summary>A positive's unexposed edge is at least this many densities below its white end;
    /// a clear or fogged edge is around 1 D.</summary>
    public const double MinBlackSpanD = 2.0;
    /// <summary>The three channels of a neutral black must agree this closely; wider means the
    /// sample is not one unexposed material and is not used.</summary>
    public const double MaxBlackSpanSpread = 1.15;

    /// <summary>Rows, pitch and scales for <paramref name="source"/>, or null when no pair of
    /// perforation rows is present. Coordinates are returned in <paramref name="source"/>'s own
    /// pixels (the detector works on a boxed copy and scales the answer back).</summary>
    public static PerforationRuler? TryGetRuler(ImageBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width < 32 || source.Height < 32) return null;
        int factor = Resample.BoxFactor(source.Width, source.Height, DetectorMaxEdge);
        ImageBuffer working = Resample.Box(source, DetectorMaxEdge);
        if (Find(working) is not { } found) return null;
        // Box divides both axes by the same integer factor, so the answer is in source pixels
        // after multiplying by it exactly (using the width ratio would carry the truncation of
        // source.Width / factor into every coordinate).
        double toSource = factor;
        return new PerforationRuler(
            found.Horizontal,
            found.Scale * toSource,
            found.Centre * toSource,
            found.AcrossScale * toSource,
            found.Angle);
    }

    /// <summary>
    /// One frame's perforation sample: the white end (light through the holes) and, when the
    /// complementary band is trustworthy, the black span down to the unexposed edge.
    ///
    /// <para>WHITE. The bands are centred on each row and are <see cref="HoleHeightMm"/> tall;
    /// everything outside them is excluded. Within a band the holes are the bright material, so
    /// the brightest 2% by luma is lamp light. All three channels are averaged over the SAME
    /// pixels: the triple belongs to one patch of light, and its channel ratios are the light's
    /// colour rather than a per-channel splice.</para>
    ///
    /// <para>BLACK. The holes are excluded (anything at or above half the hole luma), and the
    /// darkest 2% of what remains is the unexposed film between and around the holes. Its density
    /// distance below the white end is returned per channel. This is exposure-invariant — both
    /// samples are scaled by the same exposure — so it can be pooled across a roll even when the
    /// copy-stand exposure drifts.</para>
    ///
    /// <para>WHEN BLACK IS REFUSED. A clear edge (negative film), a fogged edge, or a band lifted
    /// by stray light is not an unexposed black. The sample is only returned when the complement
    /// is genuinely dark (p90 below half the hole light), the span clears
    /// <see cref="MinBlackSpanD"/> in every channel, and the channels agree within
    /// <see cref="MaxBlackSpanSpread"/>. Otherwise <see cref="PerforationSample.BlackSpan"/> is
    /// null and the caller falls back to its scene estimator.</para>
    /// </summary>
    public static PerforationSample? Measure(ImageBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width < 32 || source.Height < 32) return null;
        ImageBuffer image = Resample.Box(source, DetectorMaxEdge);
        if (Find(image) is not { } ruler) return null;

        int w = image.Width, h = image.Height;
        double alongMid = (ruler.Horizontal ? w : h) / 2.0;
        double rowOffset = RowSpacingMm / 2 * ruler.AcrossScale;
        double halfBand = HoleHeightMm / 2 * ruler.AcrossScale;
        if (!(rowOffset > 1) || !(halfBand > 0.5)) return null;

        var luma = new double[w * h];
        var rgb = new float[w * h * 3];
        int kept = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double along = ruler.Horizontal ? x : y;
                double across = ruler.Horizontal ? y : x;
                // Rows follow the strip: the centre measured at the along midpoint has to be
                // carried along the tilt, or a strip photographed at a few degrees would drift
                // out of the band at its ends and let the picture in.
                double centre = ruler.Centre + ruler.Slope * (along - alongMid);
                double distance = Math.Abs(Math.Abs(across - centre) - rowOffset);
                if (distance > halfBand) continue;
                int p = y * w + x, i = p * 3;
                float r = image.Data[i], g = image.Data[i + 1], b = image.Data[i + 2];
                if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) continue;
                int k = kept * 3;
                rgb[k] = r; rgb[k + 1] = g; rgb[k + 2] = b;
                luma[kept] = ((double)r + g + b) / 3.0;
                kept++;
            }
        }
        if (kept < 64) return null;

        double[] MeanOf(int[] indices, int start, int count)
        {
            double r = 0, g = 0, b = 0;
            for (int i = start; i < start + count; i++)
            {
                int k = indices[i] * 3;
                r += rgb[k]; g += rgb[k + 1]; b += rgb[k + 2];
            }
            return new[] { r / count, g / count, b / count };
        }

        var all = new int[kept];
        for (int i = 0; i < kept; i++) all[i] = i;
        Array.Sort(all, (a, b) => luma[b].CompareTo(luma[a]));
        int tail = Math.Clamp((int)Math.Ceiling(kept * TailFraction), 3, kept);
        double[] white = MeanOf(all, 0, tail);
        for (int c = 0; c < 3; c++)
        {
            if (!double.IsFinite(white[c])) return null;
            white[c] = Math.Min(white[c], 1.0);
            if (!(white[c] > 0)) return null;
        }

        double holeLuma = (white[0] + white[1] + white[2]) / 3.0;
        var nonHole = new List<int>();
        for (int i = 0; i < kept; i++)
            if (luma[i] < holeLuma * HoleLightFraction) nonHole.Add(i);

        double[]? black = null;
        int n = nonHole.Count;
        if (n >= MinBlackPixels)
        {
            nonHole.Sort((a, b) => luma[a].CompareTo(luma[b]));
            int blackTail = Math.Clamp((int)Math.Ceiling(n * BlackTailFraction), 3, n);
            double[] blackT = MeanOf(nonHole.ToArray(), 0, blackTail);
            double p90 = luma[nonHole[Math.Min(n - 1, (int)(n * 0.90))]];
            if (p90 <= holeLuma * MaxNonHoleP90Fraction)
            {
                var span = new double[3];
                bool ok = true;
                for (int c = 0; c < 3; c++)
                {
                    if (!(blackT[c] > 0) || !double.IsFinite(blackT[c])) { ok = false; break; }
                    span[c] = Math.Log10(white[c] / blackT[c]);
                    if (!double.IsFinite(span[c])) { ok = false; break; }
                }
                if (ok && span.Min() >= MinBlackSpanD
                       && span.Max() / span.Min() <= MaxBlackSpanSpread)
                    black = span;
            }
        }
        return new PerforationSample(white, black);
    }

    /// <summary>Only the white end; null when no perforation pair is present.</summary>
    public static double[]? MeasureWhiteEnd(ImageBuffer source) => Measure(source)?.White;

    /// <summary>Only the black span (densities below the white end); null when the frame has no
    /// perforation pair or its complementary band is not a trustworthy unexposed black.</summary>
    public static double[]? MeasureBlackSpan(ImageBuffer source) => Measure(source)?.BlackSpan;

    private static (bool Horizontal, double Scale, double Centre, double Slope, double AcrossScale, double Angle)?
        Find(ImageBuffer image)
    {
        // A thin negative can be almost as bright as its holes; a fixed low threshold merges
        // them into the film base. Each threshold must independently recover both periodic rows.
        foreach (double fraction in new[] { .72, .84, .92, .97, .60 })
            if (FindAtThreshold(image, fraction) is { } ruler) return ruler;
        return null;
    }

    private static (bool Horizontal, double Scale, double Centre, double Slope, double AcrossScale, double Angle)?
        FindAtThreshold(ImageBuffer image, double fraction)
    {
        int w = image.Width, h = image.Height;
        var light = new double[w * h];
        for (int i = 0; i < light.Length; i++)
            light[i] = (image.Data[3 * i] + image.Data[3 * i + 1] + image.Data[3 * i + 2]) / 3;
        var sorted = light.Order().ToArray();
        double cut = sorted[(int)(sorted.Length * 0.99)] * fraction;
        var seen = new bool[light.Length];
        var points = new List<(double X, double Y)>();
        var queue = new Queue<int>();
        for (int i = 0; i < light.Length; i++)
        {
            if (seen[i] || light[i] < cut) continue;
            seen[i] = true; queue.Enqueue(i);
            int x0 = w, x1 = 0, y0 = h, y1 = 0, count = 0;
            while (queue.Count > 0)
            {
                int p = queue.Dequeue(), x = p % w, y = p / w;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); count++;
                void Visit(int q) { if (!seen[q] && light[q] >= cut) { seen[q] = true; queue.Enqueue(q); } }
                if (x > 0) Visit(p - 1); if (x + 1 < w) Visit(p + 1);
                if (y > 0) Visit(p - w); if (y + 1 < h) Visit(p + w);
            }
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            if (x0 == 0 || y0 == 0 || x1 == w - 1 || y1 == h - 1 || cw < 3 || ch < 3
                || cw > w * .08 || ch > h * .12 || count < cw * ch * .55) continue;
            double ratio = (double)cw / ch;
            if (ratio < .45 || ratio > 2.2) continue;
            points.Add(((x0 + x1) / 2.0, (y0 + y1) / 2.0));
        }
        foreach (bool horizontal in new[] { true, false })
        {
            double A((double X, double Y) p) => horizontal ? p.X : p.Y;
            double C((double X, double Y) p) => horizontal ? p.Y : p.X;
            int across = horizontal ? h : w;
            var rows = new List<(double Centre, double Pitch, int Count, double Slope)>();
            foreach (var seed in points)
            {
                var row = points.Where(p => Math.Abs(C(p) - C(seed)) < across * .045).OrderBy(A).ToArray();
                if (row.Length < 4) continue;
                double centre = row.Average(C);
                if (rows.Any(r => Math.Abs(r.Centre - centre) < across * .045)) continue;
                var gaps = row.Zip(row.Skip(1), (a, b) => A(b) - A(a)).Order().ToArray();
                double pitch = gaps[gaps.Length / 2];
                if (pitch < 5 || gaps.Count(g => Math.Abs(g / pitch - 1) < .15) < gaps.Length * .7) continue;
                double meanA = row.Average(A);
                double variance = row.Sum(p => Math.Pow(A(p) - meanA, 2));
                if (!(variance > 0)) continue;
                double slope = row.Sum(p => (A(p) - meanA) * (C(p) - centre)) / variance;
                double residual = Math.Sqrt(row.Average(p => Math.Pow(C(p) - centre - slope * (A(p) - meanA), 2)));
                if (residual > pitch * .12) continue;
                centre += slope * ((horizontal ? w : h) / 2.0 - meanA);
                rows.Add((centre, pitch, row.Length, slope));
            }
            foreach (var a in rows.OrderByDescending(r => r.Count))
                foreach (var b in rows.OrderByDescending(r => r.Count))
                {
                    double distance = b.Centre - a.Centre, scale = (a.Pitch + b.Pitch) / 2 / HolePitchMm;
                    if (distance < across * .25
                        || Math.Abs(distance / (scale * RowSpacingMm) - 1) > .12
                        || Math.Abs(a.Pitch / b.Pitch - 1) > .10
                        || Math.Abs(a.Slope - b.Slope) > .025) continue;
                    double angle = Math.Atan((a.Slope + b.Slope) / 2) * 180 / Math.PI * (horizontal ? 1 : -1);
                    return (horizontal, scale, (a.Centre + b.Centre) / 2, (a.Slope + b.Slope) / 2,
                            distance / RowSpacingMm, angle);
                }
        }
        return null;
    }
}
