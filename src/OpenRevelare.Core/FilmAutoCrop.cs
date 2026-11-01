namespace OpenRevelare.Core;

public enum FilmCropMode { ImageArea, WithSprockets }
public enum FilmPolarity { Negative, Positive }

/// <summary>135 frame detection in uncropped, oriented, linear pixels. No render or mask is used.</summary>
public static class FilmAutoCrop
{
    public const double ImageAspect = 3.0 / 2;
    public const double SprocketAspect = 37.0 / 35;
    public readonly record struct Rect(double X, double Y, double W, double H);
    public sealed record Detection(Rect Image, double Confidence, int Width, int Height)
    {
        public double RotationCorrection { get; init; }
        public FilmPolarity Polarity { get; init; }
    }

    public static Detection? Detect(ImageBuffer source) => DetectCore(source, straighten: true);

    private static Detection? DetectCore(ImageBuffer source, bool straighten,
        (bool Horizontal, double Scale, double Centre, double Angle)? knownRuler = null)
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
                var aligned = DetectCore(Geometry.ApplyRotation(source, correction), straighten: false,
                    (ruler.Horizontal, ruler.Scale, ruler.Centre, 0));
                return aligned is null ? null : aligned with { RotationCorrection = correction };
            }
            bool horizontal = ruler.Horizontal;
            int along = horizontal ? w : h, across = horizontal ? h : w;
            int length = (int)Math.Round(36 * ruler.Scale), height = (int)Math.Round(24 * ruler.Scale);
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
                double baseTone = (Value(nominalLow - step) + Value(nominalHigh + step)) / 2;
                double interior = 0;
                int count = 0;
                for (int q = nominalLow + height / 4; q < nominalHigh - height / 4; q++)
                { interior += Value(q); count++; }
                double difference = baseTone - interior / count;
                if (Math.Abs(difference) > .04) toneVotes.Add(difference);
            }
            if (toneVotes.Count < along / 10) return null;
            toneVotes.Sort();
            // Decide from the exposed area's relation to its base across many columns, not
            // the winning rectangle: a night scene can otherwise win with the wrong polarity.
            int sign = toneVotes[toneVotes.Count / 2] > 0 ? 1 : -1;
            for (int cy = (int)ruler.Centre - margin; cy <= ruler.Centre + margin; cy++)
            {
                int low = cy - height / 2, high = low + height;
                if (low < step || high >= across - step) continue;
                for (int start = step; start + length < along - step; start++)
                {
                    int end = start + length, m = height / 8, lm = length / 8;
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
            return bestHoleScore < 0.12 ? null : new Detection(holeFrame, bestHoleScore, source.Width, source.Height) { Polarity = holePolarity };
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

    private static (bool Horizontal, double Scale, double Centre, double Angle)? PerforationRuler(ImageBuffer image)
    {
        int w = image.Width, h = image.Height;
        var light = new double[w * h];
        for (int i = 0; i < light.Length; i++) light[i] = (image.Data[3 * i] + image.Data[3 * i + 1] + image.Data[3 * i + 2]) / 3;
        var sorted = light.Order().ToArray();
        double cut = sorted[(int)(sorted.Length * 0.99)] * 0.72;
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
                if (distance < across * .25 || Math.Abs(distance / (scale * 29.5) - 1) > .12
                    || Math.Abs(a.Pitch / b.Pitch - 1) > .10 || Math.Abs(a.Slope - b.Slope) > .025) continue;
                double angle = Math.Atan((a.Slope + b.Slope) / 2) * 180 / Math.PI * (horizontal ? 1 : -1);
                return (horizontal, scale, (a.Centre + b.Centre) / 2, angle);
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
            // The inset absorbs grain, boundary rounding and small alignment errors; never
            // independently fit each frame to its scene, which makes a roll breathe on playback.
            double scale = Median(reliable.Select(i => Scale(frames[i]!))) * (mode == FilmCropMode.ImageArea ? 0.975 : 1);
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
