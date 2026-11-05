namespace OpenRevelare.Core;

public static partial class FilmAutoCrop
{
    private static bool HasFourGateEdges(ImageBuffer image, Detection detection)
    {
        int x0 = (int)Math.Round(detection.Image.X * image.Width), y0 = (int)Math.Round(detection.Image.Y * image.Height);
        int x1 = (int)Math.Round((detection.Image.X + detection.Image.W) * image.Width);
        int y1 = (int)Math.Round((detection.Image.Y + detection.Image.H) * image.Height);
        int step = Math.Max(2, Math.Min(image.Width, image.Height) / 100);
        if (x0 < step || y0 < step || x1 >= image.Width - step || y1 >= image.Height - step) return false;
        double Light(int x, int y)
        {
            int p = (y * image.Width + x) * 3;
            return Math.Log(Math.Max(1e-6, (image.Data[p] + image.Data[p + 1] + image.Data[p + 2]) / 3d));
        }
        double Edge(bool vertical, int at, int low, int high, int direction)
        {
            var contrasts = new List<double>();
            for (int p = low; p < high; p++)
                contrasts.Add(Math.Clamp(direction * (vertical ? Light(at - step, p) - Light(at + step, p)
                    : Light(p, at - step) - Light(p, at + step)), -1.5, 1.5));
            contrasts.Sort();
            return .35 * contrasts.Average() + .65 * contrasts[contrasts.Count / 3];
        }
        int sign = detection.Polarity == FilmPolarity.Negative ? 1 : -1;
        int mx = (x1 - x0) / 8, my = (y1 - y0) / 8;
        double[] edges = { Edge(true, x0, y0 + my, y1 - my, sign), Edge(true, x1, y0 + my, y1 - my, -sign),
            Edge(false, y0, x0 + mx, x1 - mx, sign), Edge(false, y1, x0 + mx, x1 - mx, -sign) };
        return edges.Min() >= .035 && edges.Min() >= edges.Sum() * .04;
    }

    private static (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale) RulerAfterRotation(
        ImageBuffer image, (bool Horizontal, double Scale, double Centre, double Angle, double AcrossScale) original)
    {
        int across = original.Horizontal ? image.Height : image.Width;
        double centre = across / 2d + (original.Centre - across / 2d) * Math.Cos(original.Angle * Math.PI / 180);
        var fresh = PerforationRuler(image);
        // Rotating a tight capture fills its corners. A previously clipped hole can then look
        // like a complete, shorter component, corrupting the row spacing. Keep the original
        // physical ruler if the new measurement contradicts it; do not use the corner fill.
        if (fresh is { } r && r.Horizontal == original.Horizontal
            && Math.Abs(r.Scale / original.Scale - 1) <= .035
            && Math.Abs(r.AcrossScale / original.AcrossScale - 1) <= .035
            && Math.Abs(r.Centre - centre) <= original.AcrossScale * .4)
            return r;
        return (original.Horizontal, original.Scale, centre, 0, original.AcrossScale);
    }

    private static (int Low, int High) AcrossGateBounds(ImageBuffer image, bool horizontal,
        double scale, double centre, int nominalLow, int nominalHigh)
    {
        int along = horizontal ? image.Width : image.Height, across = horizontal ? image.Height : image.Width;
        double Light(int a, int q)
        {
            int p = (horizontal ? q * image.Width + a : a * image.Width + q) * 3;
            return Math.Log(Math.Max(1e-6, (image.Data[p] + image.Data[p + 1] + image.Data[p + 2]) / 3d));
        }
        var profile = new double[across];
        int windowRadius = Math.Max(3, (int)Math.Ceiling(scale) + 1);
        // A physical boundary is shared by much of the strip, unlike a single horizon/window.
        for (int q = Math.Max(0, nominalLow - windowRadius); q <= Math.Min(across - 1, nominalHigh + windowRadius); q++)
        {
            if (Math.Abs(q - nominalLow) > windowRadius && Math.Abs(q - nominalHigh) > windowRadius) continue;
            var samples = new List<double>();
            for (int a = along / 8; a < along * 7 / 8; a += Math.Max(1, along / 96)) samples.Add(Light(a, q));
            samples.Sort(); profile[q] = samples[samples.Count / 2];
        }
        (int At, double Strength) Find(int nominal, int direction)
        {
            int radius = Math.Max(2, (int)Math.Floor(scale * .75));
            int best = nominal; double peak = 0;
            for (int q = Math.Max(1, nominal - radius); q <= Math.Min(across - 1, nominal + radius); q++)
            {
                double strength = direction * (profile[q] - profile[q - 1]);
                if (strength > peak) { peak = strength; best = q; }
            }
            return (best, peak);
        }
        (int Low, int High) bounds = (nominalLow, nominalHigh);
        double bestScore = 0;
        foreach (int sign in new[] { -1, 1 })
        {
            var top = Find(nominalLow, sign); var bottom = Find(nominalHigh, -sign);
            double height = (bottom.At - top.At) / scale;
            if (Math.Min(top.Strength, bottom.Strength) < .12 || height < 23.3 || height > 25.5
                || Math.Abs((top.At + bottom.At) / 2d - centre) > scale * .65) continue;
            double score = Math.Min(top.Strength, bottom.Strength);
            if (score > bestScore) { bestScore = score; bounds = (top.At, bottom.At); }
        }
        return bounds;
    }
}
