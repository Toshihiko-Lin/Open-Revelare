namespace OpenRevelare.Core;

public static partial class FilmAutoCrop
{
    // A reference deliberately contains no position. Moving the film between captures must
    // never move a weak frame to a different frame's centre.
    public sealed record RollReference(int Width, int Height, bool Horizontal, FilmPolarity Polarity,
        double NormalisedFilmWidth, double GateLengthMm)
    {
        public double TotalRotation { get; init; }
        public double AngleTolerance { get; init; } = .5;
    }

    public static bool NeedsRollReference(Detection? detection, IReadOnlyList<RollReference> references)
    {
        if (detection is null) return true;
        var sameSource = references.Where(r => r.Width == detection.Width && r.Height == detection.Height).ToArray();
        if (sameSource.Length == 0) return false;
        // The unconstrained fallback can confuse a portrait window/building inside a landscape
        // gate with the gate itself. It must not bypass the roll's geometric evidence.
        if (!detection.HasPerforationRuler || !double.IsFinite(detection.FilmWidth) || detection.FilmWidth <= 0) return true;
        bool horizontal = detection.Image.W * detection.Width >= detection.Image.H * detection.Height;
        double length = (horizontal ? detection.Image.W * detection.Width : detection.Image.H * detection.Height)
            / (detection.FilmWidth * (horizontal ? detection.Height : detection.Width) / 35);
        return sameSource.Any(r => r.Width == detection.Width && r.Height == detection.Height
            && r.Horizontal == horizontal && r.Polarity == detection.Polarity
            && (Math.Abs(r.NormalisedFilmWidth / detection.FilmWidth - 1) > .035
                || Math.Abs(length - r.GateLengthMm) > .6));
    }

    public static IReadOnlyList<RollReference> BuildRollReferences(IReadOnlyList<Detection?> frames)
    {
        var result = new List<RollReference>();
        foreach (var group in frames.Where(d => d is { HasPerforationRuler: true, UsedRollReference: false }
                     && d.Confidence >= .12 && double.IsFinite(d.FilmWidth) && d.FilmWidth > 0)
                 .Select(d => d!).GroupBy(d => (d.Width, d.Height, Horizontal: d.Image.W * d.Width >= d.Image.H * d.Height, d.Polarity)))
        {
            var values = group.ToArray();
            if (values.Length < 3) continue;
            double Median(IEnumerable<double> numbers)
            {
                var sorted = numbers.Order().ToArray();
                return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            double width = Median(values.Select(d => d.FilmWidth));
            double Length(Detection d) => (group.Key.Horizontal ? d.Image.W * d.Width : d.Image.H * d.Height)
                / (d.FilmWidth * (group.Key.Horizontal ? d.Height : d.Width) / 35);
            double length = Median(values.Select(Length));
            double angle = Median(values.Select(d => d.AppliedRotation + d.RotationCorrection));
            double angleMad = Median(values.Select(d => Math.Abs(d.AppliedRotation + d.RotationCorrection - angle)));
            double angleTolerance = Math.Clamp(3 * angleMad, .5, 1.5);
            var agreeing = values.Where(d => Math.Abs(d.FilmWidth / width - 1) <= .025
                && Math.Abs(Length(d) - length) <= .6 && Length(d) is >= 34.5 and <= 36.8
                && Math.Abs(d.AppliedRotation + d.RotationCorrection - angle) <= angleTolerance).ToArray();
            // Mixed magnifications or a lone plausible rectangle are not a roll reference.
            if (agreeing.Length < 3 || agreeing.Length * 3 < values.Length * 2) continue;
            result.Add(new(group.Key.Width, group.Key.Height, group.Key.Horizontal, group.Key.Polarity,
                Median(agreeing.Select(d => d.FilmWidth)), Median(agreeing.Select(Length)))
            { TotalRotation = Median(agreeing.Select(d => d.AppliedRotation + d.RotationCorrection)), AngleTolerance = angleTolerance });
        }
        return result;
    }

    /// <summary>Recover a weak gate using a corroborated physical size and this frame's ruler
    /// and exposure edge. No source-centre or reference-position assumption is made.</summary>
    public static Detection? DetectWithRollReference(ImageBuffer source, IReadOnlyList<RollReference> references,
        int sourceWidth, int sourceHeight, double appliedRotation = 0)
    {
        if (references.Count == 0 || source.Width < 64 || source.Height < 64) return null;
        var image = Resample.Box(source, 1200);
        if (image.Data.Any(value => !float.IsFinite(value))) return null;
        if (PerforationRuler(image) is not { } ruler || Math.Abs(ruler.Angle) > 5) return null;
        var originalRuler = ruler;
        var originalImage = image;
        double totalRotation = appliedRotation - ruler.Angle;
        double correction = Math.Abs(ruler.Angle) > .15 ? -ruler.Angle : 0;
        if (correction != 0)
        {
            image = Resample.Box(Geometry.ApplyRotation(source, correction, fill: 0), 1200);
            ruler = RulerAfterRotation(image, ruler);
        }
        bool horizontal = ruler.Horizontal;
        int along = horizontal ? image.Width : image.Height, across = horizontal ? image.Height : image.Width;
        double filmWidth = 35 * ruler.AcrossScale / across;
        var matching = references.Where(r => r.Width == sourceWidth && r.Height == sourceHeight
            && r.Horizontal == horizontal && Math.Abs(r.NormalisedFilmWidth / filmWidth - 1) <= .035).ToArray();
        // Polarity ambiguity must not be resolved by picking the strongest scene edge.
        if (matching.Length != 1) return null;
        var reference = matching[0];
        if (Math.Abs(totalRotation - reference.TotalRotation) > reference.AngleTolerance
            && !CorroboratesRuler(originalImage, originalRuler)) return null;
        // Try measured boundaries first. Patterned/dirty gutters can differ from the shoulder
        // even on a sound negative; four real edges plus the roll's physical size can confirm
        // that frame without requiring every gutter to be blank film base.
        var bounded = DetectCore(image, straighten: false, ruler, rollSupported: true);
        if (bounded is not null && bounded.Polarity == reference.Polarity && HasFourGateEdges(image, bounded)
            && Math.Abs((horizontal ? bounded.Image.W * image.Width : bounded.Image.H * image.Height)
                / ruler.AcrossScale - reference.GateLengthMm) <= .6)
        {
            return MeasureFilmEdges(source, bounded with
            {
                Width = sourceWidth, Height = sourceHeight, RotationCorrection = correction,
                AppliedRotation = appliedRotation, UsedRollReference = true,
            });
        }
        int length = (int)Math.Round(reference.GateLengthMm * ruler.AcrossScale);
        if (Math.Abs(ruler.Scale / ruler.AcrossScale - 1) > .035) return null;
        int low = (int)Math.Round(ruler.Centre - 12 * ruler.AcrossScale);
        int high = (int)Math.Round(ruler.Centre + 12 * ruler.AcrossScale);
        (low, high) = AcrossGateBounds(image, horizontal, ruler.AcrossScale, ruler.Centre, low, high);
        int shoulder = (int)Math.Round(12.35 * ruler.AcrossScale);
        if (low < 2 || high >= across - 2 || ruler.Centre - shoulder < 0 || ruler.Centre + shoulder >= across) return null;
        _ = FindFrameGutters(image, horizontal, ruler.Scale, ruler.AcrossScale, ruler.Centre,
            out var stableBase, out _, out bool darkBase, rollSupported: true);
        if (darkBase && reference.Polarity == FilmPolarity.Negative) return null;
        double Log(int a, int q, int c) => Math.Log(Math.Max(1e-6,
            image.Data[(horizontal ? q * image.Width + a : a * image.Width + q) * 3 + c]));
        var upper = new double[along]; var lower = new double[along];
        int sign = reference.Polarity == FilmPolarity.Negative ? 1 : -1;
        for (int a = 0; a < along; a++)
        {
            double[] baseColour = new double[3];
            int baseMargin = Math.Max(1, (int)Math.Round(ruler.AcrossScale * .35));
            for (int c = 0; c < 3; c++) baseColour[c] = (Log(a, Math.Max(0, low - baseMargin), c)
                + Log(a, Math.Min(across - 1, high + baseMargin), c)) / 2;
            double Sample(int q)
            {
                double value = 0;
                for (int c = 0; c < 3; c++) value += Math.Clamp(sign * (baseColour[c] - Log(a, q, c)), -2.5, 2.5);
                return value / 3;
            }
            int count = 0;
            for (int j = 2; j < 10; j++)
            {
                upper[a] += Sample(low + (high - low) * j / 20);
                lower[a] += Sample(high - (high - low) * j / 20); count++;
            }
            upper[a] /= count; lower[a] /= count;
        }
        int edge = Math.Max(2, (int)Math.Round(ruler.AcrossScale * .3));
        double Mean(double[] profile, int a, int b)
        {
            double sum = 0;
            for (int x = a; x < b; x++) sum += profile[x];
            return sum / (b - a);
        }
        var candidates = new List<(int Start, double Score)>();
        for (int a = edge * 2; a + length + edge * 2 < along; a++)
        {
            int b = a + length;
            if (!stableBase(a, b)) continue;
            double rise = Math.Min(Mean(upper, a + edge, a + 2 * edge) - Mean(upper, a - 2 * edge, a - edge),
                Mean(lower, a + edge, a + 2 * edge) - Mean(lower, a - 2 * edge, a - edge));
            double fall = Math.Min(Mean(upper, b - 2 * edge, b - edge) - Mean(upper, b + edge, b + 2 * edge),
                Mean(lower, b - 2 * edge, b - edge) - Mean(lower, b + edge, b + 2 * edge));
            if (Math.Max(rise, fall) < .06 || Math.Min(rise, fall) < -.015) continue;
            double outside = new[] { Mean(upper, a - 2 * edge, a - edge), Mean(lower, a - 2 * edge, a - edge),
                Mean(upper, b + edge, b + 2 * edge), Mean(lower, b + edge, b + 2 * edge) }.Max();
            double inside = Math.Min(Mean(upper, a + 2 * edge, b - 2 * edge), Mean(lower, a + 2 * edge, b - 2 * edge));
            if (outside > .045 || inside - outside < .035) continue;
            bool internalBoundary = false;
            for (int x = a + 2 * edge; x < b - 2 * edge; x++)
            {
                bool baseOnOneSide = Math.Max(upper[x], lower[x]) < .025 || Math.Max(upper[x - 1], lower[x - 1]) < .025;
                if (baseOnOneSide && (Math.Min(-(upper[x] - upper[x - 1]), -(lower[x] - lower[x - 1])) > .12
                    && x < b - ruler.AcrossScale * .8
                    || Math.Min(upper[x] - upper[x - 1], lower[x] - lower[x - 1]) > .12
                    && x > a + ruler.AcrossScale * .8))
                { internalBoundary = true; break; }
            }
            if (internalBoundary) continue;
            // A full-height unexposed septum inside the window denotes separate frames.
            int run = 0; bool split = false;
            for (int x = a + length / 5; x < b - length / 5; x++)
            {
                run = Math.Max(upper[x], lower[x]) < .02 ? run + 1 : 0;
                if (run >= ruler.AcrossScale * .7) { split = true; break; }
            }
            if (split) continue;
            candidates.Add((a, Math.Max(rise, fall) + Math.Max(0, Math.Min(rise, fall)) + inside - outside));
        }
        if (candidates.Count == 0) return null;
        var best = candidates.MaxBy(c => c.Score);
        if (candidates.Any(c => Math.Abs(c.Start - best.Start) > ruler.AcrossScale * .8 && c.Score >= best.Score * .85)) return null;
        // Anchor the inferred gate on the actual transition, not on the first/best window
        // score: the latter also contains interior contrast and biases an asymmetric scene.
        (double At, double Strength) Transition(int at, int direction)
        {
            double weighted = 0, weight = 0;
            for (int x = Math.Max(1, at - 2 * edge); x <= Math.Min(along - 1, at + 2 * edge); x++)
            {
                double v = Math.Max(0, Math.Min(direction * (upper[x] - upper[x - 1]),
                    direction * (lower[x] - lower[x - 1])));
                weighted += x * v; weight += v;
            }
            return (weight > .01 ? weighted / weight : at, weight);
        }
        var leftEdge = Transition(best.Start, 1); var rightEdge = Transition(best.Start + length, -1);
        double start = leftEdge.Strength >= rightEdge.Strength ? leftEdge.At : rightEdge.At - length;
        var rect = horizontal ? new Rect(start / along, (double)low / across, (double)length / along, (double)(high - low) / across)
            : new Rect((double)low / across, start / along, (double)(high - low) / across, (double)length / along);
        var detection = new Detection(rect, .12, sourceWidth, sourceHeight)
        {
            Polarity = reference.Polarity, HasPerforationRuler = true, UsedRollReference = true,
            AcrossCentre = ruler.Centre / across, FilmWidth = filmWidth, RotationCorrection = correction,
            AppliedRotation = appliedRotation,
        };
        return MeasureFilmEdges(source, detection);
    }

    private static bool CorroboratesRuler(ImageBuffer image,
        (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale) expected)
    {
        int agreeing = 0;
        var (light, white) = PerforationLight(image);
        foreach (double threshold in new[] { .60, .72, .84, .92, .97 })
        {
            if (PerforationRulerAtThreshold(image, threshold, light, white) is not { } r || r.Horizontal != expected.Horizontal
                || Math.Abs(r.Angle - expected.Angle) > .25
                || Math.Abs(r.Scale / expected.Scale - 1) > .025
                || Math.Abs(r.AcrossScale / expected.AcrossScale - 1) > .025
                || Math.Abs(r.Centre - expected.Centre) > expected.AcrossScale * .35) continue;
            if (++agreeing >= 2) return true;
        }
        return false;
    }

    private static Detection MeasureFilmEdges(ImageBuffer source, Detection detection)
    {
        if (!detection.HasPerforationRuler || !double.IsFinite(detection.FilmWidth)) return detection;
        var image = Resample.Box(detection.RotationCorrection == 0 ? source
            : Geometry.ApplyRotation(source, detection.RotationCorrection, fill: 0), 1200);
        bool horizontal = detection.Image.W * image.Width >= detection.Image.H * image.Height;
        int along = horizontal ? image.Width : image.Height, across = horizontal ? image.Height : image.Width;
        double scale = detection.FilmWidth * across / 35, centre = detection.AcrossCentre * across;
        int a0 = (int)((horizontal ? detection.Image.X : detection.Image.Y) * along);
        int a1 = (int)((horizontal ? detection.Image.X + detection.Image.W : detection.Image.Y + detection.Image.H) * along);
        int margin = (a1 - a0) / 8;
        if (a1 - a0 < 16) return detection;
        double Profile(int q)
        {
            var values = new List<double>();
            for (int a = a0 + margin; a < a1 - margin; a += Math.Max(1, (a1 - a0) / 64))
            {
                int p = (horizontal ? q * image.Width + a : a * image.Width + q) * 3;
                double light = (image.Data[p] + image.Data[p + 1] + image.Data[p + 2]) / 3d;
                if (!double.IsFinite(light)) return double.NaN;
                values.Add(Math.Log(Math.Max(1e-6, light)));
            }
            values.Sort(); return values[values.Count / 2];
        }
        double Find(double expected, int sign)
        {
            int radius = Math.Max(2, (int)Math.Ceiling(scale * .8));
            double weight = 0, weighted = 0, peak = 0;
            for (int q = Math.Max(1, (int)expected - radius); q <= Math.Min(across - 1, (int)expected + radius); q++)
            {
                double value = sign * (Profile(q) - Profile(q - 1));
                if (!double.IsFinite(value)) return double.NaN;
                value = Math.Max(0, value); peak = Math.Max(peak, value);
                weighted += q * value; weight += value;
            }
            return peak >= .10 && weight >= .15 ? weighted / weight : double.NaN;
        }
        double top = Find(centre - scale * 17.5, -1), bottom = Find(centre + scale * 17.5, 1);
        if (!double.IsFinite(top + bottom) || Math.Abs((bottom - top) / scale - 35) > .8) return detection;
        return detection with { AcrossCentre = (top + bottom) / (2 * across), FilmWidth = (bottom - top) / across };
    }
}
