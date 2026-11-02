namespace OpenRevelare.Core;

public enum FilmCropMode { ImageArea, WithSprockets }
public enum FilmPolarity { Negative, Positive }

/// <summary>135 frame detection in uncropped, oriented, linear pixels. No render or mask is used.</summary>
public static class FilmAutoCrop
{
    // SMPTE ST 93 / ISO 491: lateral displacement between the two perforation rows.
    // 29.5 mm was not the row spacing and biased the transverse ruler and base samples.
    private const double PerforationRowSpacing = 28.17;
    public const double ImageAspect = 3.0 / 2;
    public const double SprocketAspect = 37.0 / 35;
    public readonly record struct Rect(double X, double Y, double W, double H);
    public sealed record Detection(Rect Image, double Confidence, int Width, int Height)
    {
        public double RotationCorrection { get; init; }
        public FilmPolarity Polarity { get; init; }
        public bool HasPerforationRuler { get; init; }
    }

    /// <summary>Use the same geometric domain as the displayed/exported crop. White rotation
    /// fill is a display convention, not sensor light: it must not become the hole threshold.</summary>
    public static ImageBuffer Prepare(ImageBuffer source, FrameParams parameters)
    {
        var image = parameters.DistortionK1 == 0 ? source : LensCorrections.ApplyDistortion(source, parameters.DistortionK1);
        image = Geometry.ApplyOrientation(image, parameters.QuarterTurns, parameters.FlipH, parameters.FlipV);
        return Geometry.ApplyRotation(image, parameters.Rotation, fill: 0);
    }

    public static Detection? Detect(ImageBuffer source) => DetectCore(source, straighten: true);

    private static Detection? DetectCore(ImageBuffer source, bool straighten,
        (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale)? knownRuler = null)
    {
        if (source.Width < 64 || source.Height < 64) return null;
        var image = Resample.Box(source, 640);
        int w = image.Width, h = image.Height;
        var log = new double[w * h];
        for (int i = 0; i < log.Length; i++)
        {
            double r = image.Data[i * 3], g = image.Data[i * 3 + 1], b = image.Data[i * 3 + 2];
            if (!double.IsFinite(r + g + b)) return null;
            log[i] = Math.Log(Math.Max(1e-6, (Math.Max(0, r) + Math.Max(0, g) + Math.Max(0, b)) / 3));
        }
        int step = Math.Max(2, Math.Min(w, h) / 100);
        double Edge(bool vertical, int at, int lo, int hi, int sign)
        {
            double sum = 0;
            var contrasts = new double[hi - lo];
            for (int p = lo; p < hi; p++)
            {
                int a = vertical ? p * w + at - step : (at - step) * w + p;
                int b = vertical ? p * w + at + step : (at + step) * w + p;
                // Requiring one consistent polarity on all four sides supports both negative
                // (bright base) and positive (dark base) without trusting a scene's strongest edge.
                double contrast = Math.Clamp(sign * (log[a] - log[b]), -1.5, 1.5);
                contrasts[p - lo] = contrast;
                sum += contrast;
            }
            Array.Sort(contrasts);
            return 0.35 * sum / (hi - lo) + 0.65 * contrasts[contrasts.Length / 3];
        }
        int[] Candidates(bool vertical, int sign)
        {
            int n = vertical ? w : h, across = vertical ? h : w;
            var values = new List<(int At, double Score)>();
            for (int i = step; i < n - step; i++)
                values.Add((i, Edge(vertical, i, across / 4, across * 3 / 4, sign)));
            var kept = new List<int>();
            foreach (var v in values.OrderByDescending(v => v.Score))
            {
                if (v.Score < 0.025) break;
                if (kept.All(p => Math.Abs(p - v.At) > step * 2)) kept.Add(v.At);
                if (kept.Count == 14) break;
            }
            return kept.ToArray();
        }
        // Perforations supply a ruler independent of scene density. In particular, a dark
        // building edge can be stronger than a frame border, but cannot fake two periodic rows.
        if ((knownRuler ?? PerforationRuler(image)) is { } ruler)
        {
            if (straighten && Math.Abs(ruler.Angle) > 0.15 && Math.Abs(ruler.Angle) <= 5)
            {
                double correction = -ruler.Angle;
                // Rotation can join a clipped hole to the board or split an edge component.
                // The measured ruler is already known; do not rediscover it after resampling.
                var aligned = DetectCore(Geometry.ApplyRotation(source, correction, fill: 0), straighten: false,
                    (ruler.Horizontal, ruler.Scale, ruler.Centre, 0, ruler.AcrossScale));
                return aligned is null ? null : aligned with { RotationCorrection = correction };
            }
            if (FindFrameGutters(image, ruler.Horizontal, ruler.Scale, ruler.AcrossScale, ruler.Centre, out var stableBase, out var shoulderTone, out bool darkBase) is { } gutterFrame)
                return gutterFrame with { Width = source.Width, Height = source.Height };
            bool horizontal = ruler.Horizontal;
            int along = horizontal ? w : h, across = horizontal ? h : w;
            int length = (int)Math.Round(36 * ruler.Scale), height = (int)Math.Round(24 * ruler.AcrossScale);
            double bestHoleScore = 0;
            Rect holeFrame = default;
            FilmPolarity holePolarity = FilmPolarity.Negative;
            int margin = Math.Max(2, (int)(height * 0.04));
            var toneVotes = new List<double>();
            int nominalLow = (int)Math.Round(ruler.Centre - height / 2.0);
            int nominalHigh = nominalLow + height;
            if (nominalLow <= step || nominalHigh >= across - step) return null;
            for (int at = along / 10; at < along * 9 / 10; at++)
            {
                double Value(int q) => log[horizontal ? q * w + at : at * w + q];
                double interior = 0;
                int count = 0;
                for (int q = nominalLow + height / 4; q < nominalHigh - height / 4; q++)
                { interior += Value(q); count++; }
                double difference = shoulderTone[at] - interior / count;
                if (Math.Abs(difference) > .04) toneVotes.Add(difference);
            }
            if (toneVotes.Count < along / 10) return null;
            toneVotes.Sort();
            // Decide from the exposed area's relation to its base across many columns, not
            // the winning rectangle: a night scene can otherwise win with the wrong polarity.
            // A Dmax-black reversal base cannot be brighter than a real negative exposure;
            // positive votes there are perforation halation against unexposed black sky.
            int sign = toneVotes[toneVotes.Count / 2] > 0 ? 1 : -1;
            if (darkBase && sign > 0) return null;
            for (int cy = (int)ruler.Centre - margin; cy <= ruler.Centre + margin; cy++)
            {
                int low = cy - height / 2, high = low + height;
                if (low < step || high >= across - step) continue;
                for (int start = step; start + length < along - step; start++)
                {
                    int end = start + length, m = height / 8, lm = length / 8;
                    if (!stableBase(start,end)) continue;
                    double a = Edge(horizontal, start, low + m, high - m, sign);
                    double b = Edge(horizontal, end, low + m, high - m, -sign);
                    if (Math.Min(a, b) < 0.025) continue;
                    double c = Edge(!horizontal, low, start + lm, end - lm, sign);
                    double d = Edge(!horizontal, high, start + lm, end - lm, -sign);
                    if (Math.Min(c, d) < 0.025) continue;
                    double weakest = Math.Min(Math.Min(a, b), Math.Min(c, d));
                    if (weakest < (a + b + c + d) * .04) continue;
                    // A leader or two partial neighbours can fit a 36 mm window too. A
                    // full-height base-coloured gutter INSIDE that window exposes the mistake.
                    bool gutter = false;
                    double MeanLine(int at)
                    {
                        double sum = 0;
                        for (int q = low + m; q < high - m; q++)
                            sum += log[horizontal ? q * w + at : at * w + q];
                        return sum / (high - low - 2 * m);
                    }
                    double baseLevel = (MeanLine(start - step) + MeanLine(end + step)) / 2;
                    for (int at = start + length / 5; at < end - length / 5; at++)
                    {
                        double mean = MeanLine(at), variance = 0;
                        for (int q = low + m; q < high - m; q++)
                        {
                            double v = log[horizontal ? q * w + at : at * w + q] - mean;
                            variance += v * v;
                        }
                        if (sign * (baseLevel - mean) < .12 && variance / (high - low - 2 * m) < .025)
                        { gutter = true; break; }
                    }
                    if (gutter) continue;
                    double score = Math.Min(a, b) + Math.Min(c, d) + (a + b + c + d) / 4;
                    if (score <= bestHoleScore) continue;
                    bestHoleScore = score;
                    holePolarity = sign == 1 ? FilmPolarity.Negative : FilmPolarity.Positive;
                    holeFrame = horizontal ? new Rect((double)start / w, (double)low / h, (double)length / w, (double)height / h)
                        : new Rect((double)low / w, (double)start / h, (double)height / w, (double)length / h);
                }
            }
            return bestHoleScore < 0.12 ? null : new Detection(holeFrame, bestHoleScore, source.Width, source.Height) { Polarity = holePolarity, HasPerforationRuler = true };
        }
        double best = 0;
        Rect found = default;
        FilmPolarity polarity = FilmPolarity.Negative;
        var fallback = new List<Detection>();
        foreach (int sign in new[] { 1, -1 })
        {
            var left = Candidates(true, sign); var right = Candidates(true, -sign);
            var top = Candidates(false, sign); var bottom = Candidates(false, -sign);
            foreach (int x0 in left) foreach (int x1 in right)
            foreach (int y0 in top) foreach (int y1 in bottom)
            {
                int cw = x1 - x0, ch = y1 - y0;
                if (cw < w * 0.22 || ch < h * 0.22 || cw * ch < w * h * 0.12) continue;
                double ratio = (double)cw / ch;
                double error = Math.Min(Math.Abs(Math.Log(ratio / ImageAspect)), Math.Abs(Math.Log(ratio * ImageAspect)));
                if (error > 0.10) continue;
                int mx = cw / 8, my = ch / 8;
                double[] edges = { Edge(true, x0, y0 + my, y1 - my, sign), Edge(true, x1, y0 + my, y1 - my, -sign),
                    Edge(false, y0, x0 + mx, x1 - mx, sign), Edge(false, y1, x0 + mx, x1 - mx, -sign) };
                if (edges.Min() < 0.035) continue;
                double score = edges.Average() + edges.Min() - error * 2;
                var candidate = new Rect((double)x0 / w, (double)y0 / h, (double)cw / w, (double)ch / h);
                fallback.Add(new Detection(candidate, score, source.Width, source.Height)
                    { Polarity = sign == 1 ? FilmPolarity.Negative : FilmPolarity.Positive });
                if (score <= best) continue;
                best = score;
                polarity = sign == 1 ? FilmPolarity.Negative : FilmPolarity.Positive;
                found = new Rect((double)x0 / w, (double)y0 / h, (double)cw / w, (double)ch / h);
            }
        }
        if (best < .12) return null;
        var nested = fallback.Where(d => d.Confidence >= .12 && d.Polarity != polarity
            && d.Image.X > found.X && d.Image.Y > found.Y
            && d.Image.X + d.Image.W < found.X + found.W && d.Image.Y + d.Image.H < found.Y + found.H)
            .OrderByDescending(d => d.Confidence).FirstOrDefault();
        return nested ?? new Detection(found, best, source.Width, source.Height) { Polarity = polarity };
    }

    private static Detection? FindFrameGutters(ImageBuffer image, bool horizontal, double scale, double acrossScale, double centre,
        out Func<int, int, bool> stableBase, out double[] shoulderTone, out bool darkBase)
    {
        stableBase = (_, _) => true;
        shoulderTone = Array.Empty<double>();
        darkBase = false;
        int w = image.Width, h = image.Height, along = horizontal ? w : h, across = horizontal ? h : w;
        int low = (int)Math.Round(centre - 12 * acrossScale), high = (int)Math.Round(centre + 12 * acrossScale);
        if (low < 2 || high >= across - 2) return null;
        var baseColour = new double[along * 3];
        shoulderTone = new double[along];
        double LightAt(int a, int q)
        {
            int p = (horizontal ? q * w + a : a * w + q) * 3;
            return (image.Data[p] + image.Data[p + 1] + image.Data[p + 2]) / 3d;
        }
        double LogAt(int a, int q, int c) => Math.Log(Math.Max(1e-6,
            image.Data[(horizontal ? q * w + a : a * w + q) * 3 + c]));
        for (int a = 0; a < along; a++)
        {
            for (int c = 0; c < 3; c++)
            {
                var values = new double[6];
                // The unexposed shoulder is only ~0.69 mm wide: outside the 24 mm picture,
                // inside the nearest perforation edge at ~12.69 mm from the strip centre.
                for (int j = 0; j < 3; j++)
                {
                    int offset = (int)Math.Round((12.20 + .15 * j) * acrossScale);
                    values[j] = LogAt(a, Math.Clamp((int)Math.Round(centre) - offset, 0, across - 1), c);
                    values[j + 3] = LogAt(a, Math.Clamp((int)Math.Round(centre) + offset, 0, across - 1), c);
                }
                Array.Sort(values); baseColour[a * 3 + c] = (values[2] + values[3]) / 2;
            }
            shoulderTone[a] = (baseColour[a * 3] + baseColour[a * 3 + 1] + baseColour[a * 3 + 2]) / 3;
        }
        double Distance(int a, int q)
        {
            double value = 0;
            for (int c = 0; c < 3; c++) value += Math.Min(2.5, Math.Abs(LogAt(a, q, c) - baseColour[a * 3 + c]));
            return value / 3;
        }
        var profile = new double[along]; var tone = new double[along];
        var upperProfile = new double[along]; var lowerProfile = new double[along];
        int sampleStep = Math.Max(1, (high - low) / 48), samples = 0, upperSamples = 0, lowerSamples = 0, midQ = (low + high) / 2;
        for (int q = low + 1; q < high - 1; q += sampleStep)
        {
            samples++;
            if (q < midQ) upperSamples++; else lowerSamples++;
        }
        for (int a = 0; a < along; a++)
        {
            for (int q = low + 1; q < high - 1; q += sampleStep)
            {
                double dist = Distance(a, q);
                profile[a] += dist;
                if (q < midQ) upperProfile[a] += dist; else lowerProfile[a] += dist;
                for (int c = 0; c < 3; c++) tone[a] += (baseColour[a * 3 + c] - LogAt(a, q, c)) / 3;
            }
            profile[a] /= samples; tone[a] /= samples;
            upperProfile[a] /= Math.Max(1, upperSamples);
            lowerProfile[a] /= Math.Max(1, lowerSamples);
        }
        static void Smooth3(double[] data)
        {
            var copy = (double[])data.Clone();
            for (int i = 1; i < data.Length - 1; i++) data[i] = (copy[i - 1] + 2 * copy[i] + copy[i + 1]) / 4;
        }
        Smooth3(profile); Smooth3(upperProfile); Smooth3(lowerProfile);
        var prefix = new double[along + 1];
        for (int a = 0; a < along; a++) prefix[a + 1] = prefix[a] + profile[a];
        double Mean(int a, int b) => (prefix[b] - prefix[a]) / Math.Max(1, b - a);
        int edge = Math.Max(2, (int)Math.Round(scale * .35));
        var sampledLight = new List<double>();
        for (int i = 0; i < image.Data.Length; i += 12) sampledLight.Add((image.Data[i] + image.Data[i + 1] + image.Data[i + 2]) / 3d);
        sampledLight.Sort(); double referenceWhite = Math.Max(1e-6, sampledLight[(int)(sampledLight.Count * .99)]);
        var stripBaseLinear = new List<double>();
        for (int a = along / 10; a < along * 9 / 10; a++)
            stripBaseLinear.Add((Math.Exp(baseColour[a * 3]) + Math.Exp(baseColour[a * 3 + 1]) + Math.Exp(baseColour[a * 3 + 2])) / 3);
        if (stripBaseLinear.Count > 0) { stripBaseLinear.Sort(); darkBase = stripBaseLinear[stripBaseLinear.Count / 2] < referenceWhite * .06; }

        // A black film-holder mask occluding a bright neighbour looks dark in the image channel,
        // but it also hides both perforation rows. Real strip borders keep perforations or backlight.
        int rowLow = Math.Clamp((int)Math.Round(centre - PerforationRowSpacing / 2 * acrossScale), 0, across - 1);
        int rowHigh = Math.Clamp((int)Math.Round(centre + PerforationRowSpacing / 2 * acrossScale), 0, across - 1);
        int pitch = Math.Max(4, (int)Math.Round(4.75 * scale));
        var leftVisible = new bool[along]; var rightVisible = new bool[along];
        bool RowsLit(int x0, int x1)
        {
            double maxLow = 0, maxHigh = 0;
            for (int x = Math.Max(0, x0); x <= Math.Min(along - 1, x1); x++)
            {
                maxLow = Math.Max(maxLow, LightAt(x, rowLow));
                maxHigh = Math.Max(maxHigh, LightAt(x, rowHigh));
            }
            return Math.Min(maxLow, maxHigh) >= referenceWhite * .35;
        }
        for (int a = 0; a < along; a++)
        {
            leftVisible[a] = RowsLit(a - pitch, a - edge);
            rightVisible[a] = RowsLit(a + edge, a + pitch);
        }

        var basePrefix = new double[(along + 1) * 3];
        for (int a = 0; a < along; a++) for (int c = 0; c < 3; c++) basePrefix[(a + 1) * 3 + c] = basePrefix[a * 3 + c] + baseColour[a * 3 + c];
        // Burned leaders can combine clear and dark bases inside a nominal frame. Check both
        // local steps and quarter-frame averages so gradual optical burn transitions cannot slip by.
        bool HasStableBase(int a, int b)
        {
            if (!leftVisible[a] || !rightVisible[b]) return false;
            for (int x = a + 2 * edge; x < b - 2 * edge; x += edge)
            {
                double jump = 0, linearShift = 0;
                for (int c = 0; c < 3; c++)
                {
                    double left = (basePrefix[x * 3 + c] - basePrefix[(x - edge) * 3 + c]) / edge;
                    double right = (basePrefix[(x + edge) * 3 + c] - basePrefix[x * 3 + c]) / edge;
                    jump += Math.Abs(right - left); linearShift += Math.Abs(Math.Exp(right) - Math.Exp(left));
                }
                // Black slide-base read noise can be huge in log units but negligible light.
                // A burned leader requires a substantial change in both domains.
                if (jump / 3 > .45 && linearShift / 3 > referenceWhite * .18) return false;
            }
            int span0 = a + edge, span1 = b - edge;
            if (span1 - span0 >= 8)
            {
                Span<double> quarters = stackalloc double[12];
                for (int q = 0; q < 4; q++)
                {
                    int q0 = span0 + (span1 - span0) * q / 4, q1 = span0 + (span1 - span0) * (q + 1) / 4;
                    for (int c = 0; c < 3; c++)
                        quarters[q * 3 + c] = (basePrefix[q1 * 3 + c] - basePrefix[q0 * 3 + c]) / Math.Max(1, q1 - q0);
                }
                for (int q0 = 0; q0 < 4; q0++) for (int q1 = q0 + 1; q1 < 4; q1++)
                {
                    double jump = 0, linearShift = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        jump += Math.Abs(quarters[q1 * 3 + c] - quarters[q0 * 3 + c]);
                        linearShift += Math.Abs(Math.Exp(quarters[q1 * 3 + c]) - Math.Exp(quarters[q0 * 3 + c]));
                    }
                    if (jump / 3 > 1.5 && linearShift / 3 > referenceWhite * .18) return false;
                }
            }
            return true;
        }
        stableBase = HasStableBase;
        int minLength = (int)Math.Round(34.5 * scale), maxLength = (int)Math.Round(36.8 * scale);
        double best = 0; int start = 0, end = 0;
        for (int a = edge * 2; a + minLength < along - edge * 2; a++)
        {
            double rise = profile[a + edge] - profile[a - edge];
            if (rise < .015 || !leftVisible[a]) continue;
            for (int b = a + minLength; b <= a + maxLength && b < along - edge * 2; b++)
            {
                double fall = profile[b - edge] - profile[b + edge];
                if (fall < .015 || !rightVisible[b]) continue;
                if (!HasStableBase(a, b)) continue;
                double outside = Math.Max(Mean(a - 2 * edge, a - edge), Mean(b + edge, b + 2 * edge));
                double inside = Mean(a + 2 * edge, b - 2 * edge), contrast = inside - outside;
                if (contrast < .025 || outside > inside * .4 + .03) continue;
                // Two 18x24 mm half-frames separated by an unexposed septum fit a 36 mm span,
                // with full-height gate edges on both sides of the central gap. Dark subject
                // pillars inside a single C400 frame do not form a full-height central gate.
                double gutterFloor = Math.Min(Mean(a - 2 * edge, a - edge), Mean(b + edge, b + 2 * edge));
                double centralLimit = gutterFloor + contrast * .12;
                bool splitFrames = false; int cRun = 0;
                int cStart = a + (b - a) * 3 / 8, cEnd = b - (b - a) * 3 / 8;
                for (int x = cStart; x <= cEnd; x++)
                {
                    if (x < cEnd && profile[x] < centralLimit) { cRun++; continue; }
                    if (cRun >= scale * .6 && cRun <= scale * 3.2)
                    {
                        int g0 = x - cRun, g1 = x, gm = g0 + cRun / 2;
                        if (g0 - edge >= a && g1 + edge <= b
                            && Math.Min(upperProfile[g0 - edge] - upperProfile[gm], lowerProfile[g0 - edge] - lowerProfile[gm]) >= .025
                            && Math.Min(upperProfile[g1 + edge] - upperProfile[gm], lowerProfile[g1 + edge] - lowerProfile[gm]) >= .025)
                        { splitFrames = true; break; }
                    }
                    cRun = 0;
                }
                if (splitFrames) continue;
                int run = 0; bool gutter = false; double limit = outside + contrast * .08;
                for (int x = a + (b - a) / 6; x < b - (b - a) / 6; x++)
                {
                    if (profile[x] < limit) { run++; continue; }
                    if (run >= scale * .7 && run <= scale * 3.2) { gutter = true; break; }
                    run = 0;
                }
                double score = (gutter ? .5 : 1) * (Math.Min(rise, fall) + contrast) / (1 + .3 * Math.Pow(((a + b) / 2d - along / 2d) / (b - a), 2));
                if (score <= best) continue;
                best = score; start = a; end = b;
            }
        }
        if (best == 0) return null;
        // The ruler provides the transverse bounds even when a dark scene touches its base.
        // Refine both only when there is real boundary evidence; an internal horizon alone
        // must not drag the frame away from its two perforation rows.
        double NormalProfile(int q)
        {
            double sum = 0; int count = 0, dx = Math.Max(1, (end - start) / 48);
            for (int a = start + (end - start) / 8; a < end - (end - start) / 8; a += dx) { sum += Distance(a, q); count++; }
            return sum / Math.Max(1, count);
        }
        int radius = Math.Max(2, (int)Math.Round(acrossScale * .8)), normalStep = Math.Max(1, (int)Math.Round(acrossScale * .25));
        (int At, double Score) Boundary(int at, int sign)
        {
            int position = at; double peak = 0;
            for (int q = Math.Max(normalStep, at - radius); q <= Math.Min(across - normalStep - 1, at + radius); q++)
            {
                double value = sign * (NormalProfile(q + normalStep) - NormalProfile(q - normalStep));
                if (value > peak) { peak = value; position = q; }
            }
            return (position, peak);
        }
        var top = Boundary(low, 1); var bottom = Boundary(high, -1);
        if (top.Score > .025 && bottom.Score > .025) { low = top.At; high = bottom.At; }
        double signedTone = 0; for (int a = start; a < end; a++) signedTone += tone[a];
        double frameBaseLinear = 0;
        for (int c = 0; c < 3; c++)
            frameBaseLinear += Math.Exp((basePrefix[end * 3 + c] - basePrefix[start * 3 + c]) / Math.Max(1, end - start)) / 3;
        var polarity = frameBaseLinear < referenceWhite * .06
            ? FilmPolarity.Positive
            : signedTone >= 0 ? FilmPolarity.Negative : FilmPolarity.Positive;
        var rect = horizontal ? new Rect(start / (double)w, low / (double)h, (end - start) / (double)w, (high - low) / (double)h)
            : new Rect(low / (double)w, start / (double)h, (high - low) / (double)w, (end - start) / (double)h);
        return new Detection(rect, Math.Max(.12, best * 2), image.Width, image.Height)
            { Polarity = polarity, HasPerforationRuler = true };
    }

    private static (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale)? PerforationRuler(ImageBuffer image)
    {
        // A thin negative can be almost as bright as its holes; a fixed low threshold merges
        // them into the film base. Each threshold must independently recover both periodic rows.
        foreach (double fraction in new[] { .72, .84, .92, .97, .60 })
            if (PerforationRulerAtThreshold(image, fraction) is { } ruler) return ruler;
        return null;
    }

    private static (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale)? PerforationRulerAtThreshold(ImageBuffer image, double fraction)
    {
        int w = image.Width, h = image.Height;
        var light = new double[w * h];
        for (int i = 0; i < light.Length; i++) light[i] = (image.Data[3 * i] + image.Data[3 * i + 1] + image.Data[3 * i + 2]) / 3;
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
                double slope = row.Sum(p => (A(p) - meanA) * (C(p) - centre)) / Math.Max(1, variance);
                double residual = Math.Sqrt(row.Average(p => Math.Pow(C(p) - centre - slope * (A(p) - meanA), 2)));
                if (residual > pitch * .12) continue;
                centre += slope * ((horizontal ? w : h) / 2.0 - meanA);
                rows.Add((centre, pitch, row.Length, slope));
            }
            foreach (var a in rows.OrderByDescending(r => r.Count)) foreach (var b in rows.OrderByDescending(r => r.Count))
            {
                double distance = b.Centre - a.Centre, scale = (a.Pitch + b.Pitch) / 2 / 4.75;
                if (distance < across * .25 || Math.Abs(distance / (scale * PerforationRowSpacing) - 1) > .12
                    || Math.Abs(a.Pitch / b.Pitch - 1) > .10 || Math.Abs(a.Slope - b.Slope) > .025) continue;
                double angle = Math.Atan((a.Slope + b.Slope) / 2) * 180 / Math.PI * (horizontal ? 1 : -1);
                return (horizontal, scale, (a.Centre + b.Centre) / 2, angle, distance / PerforationRowSpacing);
            }
        }
        return null;
    }

    /// <summary>One shared physical frame size per source geometry/orientation, individual centres.
    /// Uncertain detections and scale outliers stay untouched instead of changing the roll's zoom.</summary>
    public static IReadOnlyList<Rect?> Plan(IReadOnlyList<Detection?> frames, FilmCropMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var result = new Rect?[frames.Count];
        bool Valid(Detection? d) => d is not null && d.Width > 0 && d.Height > 0
            && double.IsFinite(d.Image.X + d.Image.Y + d.Image.W + d.Image.H + d.Confidence + d.RotationCorrection)
            && d.Confidence >= .12 && d.Image.X >= 0 && d.Image.Y >= 0 && d.Image.W > 0 && d.Image.H > 0
            && d.Image.X + d.Image.W <= 1 && d.Image.Y + d.Image.H <= 1;
        foreach (var group in Enumerable.Range(0, frames.Count).Where(i => Valid(frames[i]))
                     .GroupBy(i => (frames[i]!.Width, frames[i]!.Height,
                         Landscape: frames[i]!.Image.W * frames[i]!.Width >= frames[i]!.Image.H * frames[i]!.Height)))
        {
            var ids = group.ToArray();
            double Scale(Detection d) => group.Key.Landscape
                ? Math.Min(d.Image.W * d.Width / 36, d.Image.H * d.Height / 24)
                : Math.Min(d.Image.W * d.Width / 24, d.Image.H * d.Height / 36);
            double Median(IEnumerable<double> v) { var a = v.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }
            double median = Median(ids.Select(i => Scale(frames[i]!)));
            var reliable = ids.Where(i => Math.Abs(Scale(frames[i]!) / median - 1) <= 0.08).ToArray();
            if (reliable.Length == 0) continue;
            // The common content window must fit the smallest reliable exposure. A median
            // window silently skipped slightly smaller frames, despite a valid location. Scale
            // outliers were rejected above; never resize individual frames to hide this drift.
            double scale = (mode == FilmCropMode.ImageArea
                ? reliable.Min(i => Scale(frames[i]!)) * .975
                : Median(reliable.Select(i => Scale(frames[i]!))));
            double longSide = mode == FilmCropMode.ImageArea ? 36 : 37;
            // Integer multiples of 3×2 / 37×35 make export dimensions identical across the
            // group, including the one-pixel rounding drift of independently placed rectangles.
            int numerator = mode == FilmCropMode.ImageArea ? 3 : 37;
            int denominator = mode == FilmCropMode.ImageArea ? 2 : 35;
            int unit = (int)Math.Floor(longSide * scale / numerator);
            if (unit < 1) continue;
            int pixelWidth = (group.Key.Landscape ? numerator : denominator) * unit;
            int pixelHeight = (group.Key.Landscape ? denominator : numerator) * unit;
            double rw = (double)pixelWidth / group.Key.Width;
            double rh = (double)pixelHeight / group.Key.Height;
            if (rw > 1 || rh > 1) continue;
            foreach (int i in reliable)
            {
                Rect r = frames[i]!.Image;
                double x = Math.Round((r.X + r.W / 2 - rw / 2) * group.Key.Width) / group.Key.Width;
                double y = Math.Round((r.Y + r.H / 2 - rh / 2) * group.Key.Height) / group.Key.Height;
                // Moving a sprocket crop back into bounds would lose the film edge on one side.
                // Refuse that frame rather than silently shrinking its zoom or cutting its holes.
                if (x < 0 || y < 0 || x + rw > 1 || y + rh > 1) continue;
                if (mode == FilmCropMode.ImageArea &&
                    (x < r.X - .5 / group.Key.Width || y < r.Y - .5 / group.Key.Height
                    || x + rw > r.X + r.W + .5 / group.Key.Width || y + rh > r.Y + r.H + .5 / group.Key.Height)) continue;
                result[i] = new Rect(x, y, rw, rh);
            }
        }
        return result;
    }
}
