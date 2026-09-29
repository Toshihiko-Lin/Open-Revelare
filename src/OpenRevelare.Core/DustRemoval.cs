namespace OpenRevelare.Core;

/// <summary>A circular dust repair in the optical-corrected, pre-orientation source grid.</summary>
public readonly record struct DustSpot(
    double X,
    double Y,
    double Radius,
    bool Automatic = false,
    double Confidence = 1.0)
{
    public DustSpot Clamped() => new(
        Math.Clamp(X, 0.0, 1.0),
        Math.Clamp(Y, 0.0, 1.0),
        Math.Clamp(Radius, 0.00025, 0.08),
        Automatic,
        Math.Clamp(Confidence, 0.0, 1.0));
}

/// <summary>
/// Texture-preserving, non-destructive dust repair. Dust locations are deliberately user-selected:
/// single-frame automatic detection cannot reliably distinguish film detail from dust.
/// </summary>
public static class DustRemoval
{
    /// <summary>
    /// Repair the union of all spots in one pass using nearby texture patches. Overlapping brush
    /// discs contribute to one target pixel only once, rather than repeatedly repairing it.
    /// <paramref name="offsetX"/>/<paramref name="offsetY"/> place a decoded slice in the full
    /// source grid and make regional rendering match the whole-frame path.
    /// </summary>
    public static void Apply(ImageBuffer image, IReadOnlyList<DustSpot> spots,
                             int fullWidth = 0, int fullHeight = 0,
                             int offsetX = 0, int offsetY = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(spots);
        if (spots.Count == 0) return;
        fullWidth = fullWidth > 0 ? fullWidth : image.Width;
        fullHeight = fullHeight > 0 ? fullHeight : image.Height;
        int minFull = Math.Min(fullWidth, fullHeight);
        var prepared = new List<PreparedSpot>(spots.Count);

        foreach (DustSpot raw in spots)
        {
            DustSpot spot = raw.Clamped();
            double cx = spot.X * fullWidth - 0.5;
            double cy = spot.Y * fullHeight - 0.5;
            double radius = Math.Max(1.25, spot.Radius * minFull);
            if (cx + radius < offsetX || cy + radius < offsetY ||
                cx - radius >= offsetX + image.Width || cy - radius >= offsetY + image.Height)
                continue;

            var (dx, dy) = BestOffset(image.Data, image.Width, image.Height,
                                      cx - offsetX, cy - offsetY, radius);
            int x0 = Math.Max(0, (int)Math.Floor(cx - radius) - offsetX);
            int y0 = Math.Max(0, (int)Math.Floor(cy - radius) - offsetY);
            int x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(cx + radius) - offsetX);
            int y1 = Math.Min(image.Height - 1, (int)Math.Ceiling(cy + radius) - offsetY);
            int patchWidth = x1 - x0 + 1, patchHeight = y1 - y0 + 1;
            var target = new float[checked(patchWidth * patchHeight * 3)];
            var exemplar = new float[target.Length];
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                int sx = Math.Clamp(x + dx, 0, image.Width - 1);
                int sy = Math.Clamp(y + dy, 0, image.Height - 1);
                int dst = (y * image.Width + x) * 3;
                int src = (sy * image.Width + sx) * 3;
                int local = ((y - y0) * patchWidth + x - x0) * 3;
                Array.Copy(image.Data, dst, target, local, 3);
                Array.Copy(image.Data, src, exemplar, local, 3);
            }
            float[] colourOffset = EstimateColourOffset(
                target, exemplar, patchWidth, patchHeight, cx - offsetX, cy - offsetY,
                radius, x0, y0);
            prepared.Add(new PreparedSpot(cx, cy, radius, x0, y0, x1, y1,
                                          target, exemplar, colourOffset));
        }

        var covered = new Dictionary<int, RepairAccumulator>();
        foreach (PreparedSpot patch in prepared)
        {
            int patchWidth = patch.X1 - patch.X0 + 1;
            for (int y = patch.Y0; y <= patch.Y1; y++)
            for (int x = patch.X0; x <= patch.X1; x++)
            {
                double rx = x + offsetX - patch.CentreX, ry = y + offsetY - patch.CentreY;
                double d = Math.Sqrt(rx * rx + ry * ry) / patch.Radius;
                if (d >= 1.0) continue;
                float alpha = (float)Math.Clamp((1.0 - d) / 0.22, 0.0, 1.0);
                int pixel = y * image.Width + x;
                int local = ((y - patch.Y0) * patchWidth + x - patch.X0) * 3;
                covered.TryGetValue(pixel, out RepairAccumulator contribution);
                contribution.Weight += alpha;
                contribution.Alpha = Math.Max(contribution.Alpha, alpha);
                for (int c = 0; c < 3; c++)
                    contribution.Add(c, Math.Max(0f, patch.Exemplar[local + c] + patch.ColourOffset[c]) * alpha);
                covered[pixel] = contribution;
            }
        }

        foreach ((int pixel, RepairAccumulator contribution) in covered)
        {
            int dst = pixel * 3;
            for (int c = 0; c < 3; c++)
                image.Data[dst + c] = image.Data[dst + c] * (1f - contribution.Alpha)
                                    + (float)(contribution.Get(c) / contribution.Weight) * contribution.Alpha;
        }
    }

    private struct RepairAccumulator
    {
        public float Alpha;
        public double Weight;
        public double Red;
        public double Green;
        public double Blue;

        public void Add(int channel, double value)
        {
            if (channel == 0) Red += value;
            else if (channel == 1) Green += value;
            else Blue += value;
        }

        public readonly double Get(int channel) => channel switch
        {
            0 => Red,
            1 => Green,
            _ => Blue,
        };
    }

    private sealed record PreparedSpot(
        double CentreX, double CentreY, double Radius,
        int X0, int Y0, int X1, int Y1,
        float[] Target, float[] Exemplar, float[] ColourOffset);

    private static float[] EstimateColourOffset(
        float[] target, float[] exemplar, int width, int height,
        double centreX, double centreY, double radius, int x0, int y0)
    {
        var offset = new float[3];
        int samples = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double dx = x + x0 - centreX, dy = y + y0 - centreY;
            double distance = Math.Sqrt(dx * dx + dy * dy) / radius;
            if (distance < 0.82 || distance > 1.12) continue;
            int p = (y * width + x) * 3;
            for (int c = 0; c < 3; c++) offset[c] += target[p + c] - exemplar[p + c];
            samples++;
        }
        if (samples == 0) return offset;
        for (int c = 0; c < 3; c++)
            offset[c] = Math.Clamp(offset[c] / samples, -0.5f, 0.5f);
        return offset;
    }

    /// <summary>Maximum source-grid neighbourhood a regional decode must retain.</summary>
    public static int RequiredHalo(IReadOnlyList<DustSpot> spots, int width, int height)
    {
        if (spots.Count == 0) return 0;
        double max = spots.Max(s => s.Clamped().Radius) * Math.Min(width, height);
        return (int)Math.Ceiling(max * 3.5 + 3.0);
    }

    public static bool[] MakeMask(int width, int height, IReadOnlyList<DustSpot> spots)
    {
        var mask = new bool[checked(width * height)];
        int min = Math.Min(width, height);
        foreach (DustSpot raw in spots)
        {
            DustSpot s = raw.Clamped();
            double cx = s.X * width - 0.5, cy = s.Y * height - 0.5;
            double r = Math.Max(1, s.Radius * min);
            int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + r));
            int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + r));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r) mask[y * width + x] = true;
            }
        }
        return mask;
    }

    private static (int X, int Y) BestOffset(float[] data, int w, int h,
                                             double cx, double cy, double radius)
    {
        int step = Math.Max(2, (int)Math.Ceiling(radius * 2.2));
        (int X, int Y)[] candidates =
        {
            (step, 0), (-step, 0), (0, step), (0, -step),
            (step, step), (step, -step), (-step, step), (-step, -step),
        };
        double best = double.MaxValue;
        (int X, int Y) answer = candidates[0];
        int samples = 24;
        foreach (var candidate in candidates)
        {
            double score = 0;
            int valid = 0;
            for (int i = 0; i < samples; i++)
            {
                double a = i * Math.PI * 2.0 / samples;
                int x = (int)Math.Round(cx + Math.Cos(a) * radius);
                int y = (int)Math.Round(cy + Math.Sin(a) * radius);
                int sx = x + candidate.X, sy = y + candidate.Y;
                if (x < 0 || x >= w || y < 0 || y >= h || sx < 0 || sx >= w || sy < 0 || sy >= h)
                    continue;
                int p = (y * w + x) * 3, q = (sy * w + sx) * 3;
                for (int c = 0; c < 3; c++) score += Math.Abs(data[p + c] - data[q + c]);
                valid++;
            }
            if (valid >= samples / 2 && score / valid < best)
            {
                best = score / valid;
                answer = candidate;
            }
        }
        return answer;
    }

}
