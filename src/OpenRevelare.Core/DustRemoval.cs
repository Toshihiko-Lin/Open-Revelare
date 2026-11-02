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
        // Exclude every marked defect, including strokes outside a regional decode.
        RepairDisc[] discs = spots.Select(s => s.Clamped())
            .Where(s => double.IsFinite(s.X) && double.IsFinite(s.Y) && double.IsFinite(s.Radius))
            .Select(s => new RepairDisc(s.X * fullWidth - 0.5, s.Y * fullHeight - 0.5,
                Math.Max(1.25, s.Radius * minFull)))
            .Distinct().ToArray();

        foreach (RepairDisc disc in discs)
        {
            double cx = disc.X, cy = disc.Y, radius = disc.Radius;
            if (cx + radius < offsetX || cy + radius < offsetY ||
                cx - radius >= offsetX + image.Width || cy - radius >= offsetY + image.Height)
                continue;

            Donor? donor = FindDonor(image, disc, discs, offsetX, offsetY);
            // Leave an area intact rather than import another known defect.
            if (donor is null) continue;
            var (dx, dy) = (donor.Dx, donor.Dy);
            int x0 = Math.Max(0, (int)Math.Floor(cx - radius) - offsetX);
            int y0 = Math.Max(0, (int)Math.Floor(cy - radius) - offsetY);
            int x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(cx + radius) - offsetX);
            int y1 = Math.Min(image.Height - 1, (int)Math.Ceiling(cy + radius) - offsetY);
            int patchWidth = x1 - x0 + 1, patchHeight = y1 - y0 + 1;
            var exemplar = new float[checked(patchWidth * patchHeight * 3)];
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                int sx = Math.Clamp(x + dx, 0, image.Width - 1);
                int sy = Math.Clamp(y + dy, 0, image.Height - 1);
                int src = (sy * image.Width + sx) * 3;
                int local = ((y - y0) * patchWidth + x - x0) * 3;
                Array.Copy(image.Data, src, exemplar, local, 3);
            }
            prepared.Add(new PreparedSpot(cx, cy, radius, x0, y0, x1, y1,
                                          exemplar, donor.ColourPlane));
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
                double feather = Math.Clamp((1.0 - d) / 0.22, 0.0, 1.0);
                float alpha = (float)(feather * feather * (3 - 2 * feather));
                int pixel = y * image.Width + x;
                int local = ((y - patch.Y0) * patchWidth + x - patch.X0) * 3;
                covered.TryGetValue(pixel, out RepairAccumulator contribution);
                contribution.Weight += alpha;
                contribution.Alpha = Math.Max(contribution.Alpha, alpha);
                for (int c = 0; c < 3; c++)
                {
                    double correction = patch.ColourPlane[c * 3]
                        + patch.ColourPlane[c * 3 + 1] * rx / patch.Radius
                        + patch.ColourPlane[c * 3 + 2] * ry / patch.Radius;
                    // Optical-corrected source samples may be negative or greater than one.
                    contribution.Add(c, (patch.Exemplar[local + c] + correction) * alpha);
                }
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
        float[] Exemplar, double[] ColourPlane);

    private readonly record struct RepairDisc(double X, double Y, double Radius);
    private readonly record struct BoundaryPixel(int FullX, int FullY, int Index);
    private sealed record Donor(int Dx, int Dy, double[] ColourPlane);
    private readonly record struct BoundarySample(double X, double Y, double R, double G, double B)
    {
        public double Channel(int c) => c == 0 ? R : c == 1 ? G : B;
    }

    /// <summary>Maximum source-grid neighbourhood a regional decode must retain.</summary>
    public static int RequiredHalo(IReadOnlyList<DustSpot> spots, int width, int height)
    {
        if (spots.Count == 0) return 0;
        double max = spots.Max(s => s.Clamped().Radius) * Math.Min(width, height);
        // Target centre can be one radius outside the requested ROI. The farthest donor
        // is 3.5 r away and its clean boundary ring extends another 1.35 r.
        return (int)Math.Ceiling(Math.Max(1.25, max) * 6.0 + 8.0);
    }

    public static bool[] MakeMask(int width, int height, IReadOnlyList<DustSpot> spots)
    {
        var mask = new bool[checked(width * height)];
        int min = Math.Min(width, height);
        foreach (DustSpot raw in spots)
        {
            DustSpot s = raw.Clamped();
            double cx = s.X * width - 0.5, cy = s.Y * height - 0.5;
            double r = Math.Max(1.25, s.Radius * min);
            int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + r));
            int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + r));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy < r * r) mask[y * width + x] = true;
            }
        }
        return mask;
    }

    private static bool IsMasked(double x, double y, IReadOnlyList<RepairDisc> discs)
    {
        foreach (var disc in discs)
            if ((x - disc.X) * (x - disc.X) + (y - disc.Y) * (y - disc.Y) < disc.Radius * disc.Radius)
                return true;
        return false;
    }

    private static Donor? FindDonor(ImageBuffer image, RepairDisc target,
        IReadOnlyList<RepairDisc> discs, int offsetX, int offsetY)
    {
        const int SampleCount = 48;
        Span<BoundarySample> samples = stackalloc BoundarySample[SampleCount];
        Span<BoundaryPixel> boundary = stackalloc BoundaryPixel[SampleCount];
        int boundaryCount = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            double a = i * Math.PI * 2 / SampleCount;
            // Full-grid rounding keeps half-pixel ties independent of decode offsets.
            int fullX = (int)Math.Round(target.X + Math.Cos(a) * target.Radius * 1.35);
            int fullY = (int)Math.Round(target.Y + Math.Sin(a) * target.Radius * 1.35);
            int x = fullX - offsetX, y = fullY - offsetY;
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height || IsMasked(fullX, fullY, discs)) continue;
            boundary[boundaryCount++] = new(fullX, fullY, (y * image.Width + x) * 3);
        }
        if (boundaryCount < 8) return null;
        BoundarySample[]? bestSamples = null;
        int bestDx = 0, bestDy = 0;
        double best = double.MaxValue;
        foreach (double distance in new[] { 2.5, 3.5 })
        for (int direction = 0; direction < 16; direction++)
        {
            double angle = direction * Math.PI / 8;
            int dx = (int)Math.Round(Math.Cos(angle) * target.Radius * distance);
            int dy = (int)Math.Round(Math.Sin(angle) * target.Radius * distance);
            double donorX = target.X + dx, donorY = target.Y + dy;
            double context = target.Radius * 1.35 + 1;
            if (donorX - context < offsetX || donorY - context < offsetY
                || donorX + context >= offsetX + image.Width || donorY + context >= offsetY + image.Height)
                continue;
            bool dirty = false;
            foreach (var disc in discs)
            {
                double minDistance = target.Radius * 1.35 + disc.Radius + 1;
                if ((donorX - disc.X) * (donorX - disc.X) + (donorY - disc.Y) * (donorY - disc.Y)
                    < minDistance * minDistance) { dirty = true; break; }
            }
            if (dirty) continue;
            int count = 0;
            double sumR = 0, sumG = 0, sumB = 0;
            for (int i = 0; i < boundaryCount; i++)
            {
                var pixel = boundary[i];
                int fullX = pixel.FullX, fullY = pixel.FullY;
                int x = fullX - offsetX, y = fullY - offsetY;
                int sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height)
                    continue;
                int p = pixel.Index, q = (sy * image.Width + sx) * 3;
                double r = image.Data[p] - image.Data[q];
                double g = image.Data[p + 1] - image.Data[q + 1];
                double b = image.Data[p + 2] - image.Data[q + 2];
                if (!double.IsFinite(r + g + b)) continue;
                samples[count++] = new((fullX - target.X) / target.Radius,
                    (fullY - target.Y) / target.Radius, r, g, b);
                sumR += r; sumG += g; sumB += b;
            }
            if (count < 8) continue;
            double mr = sumR / count, mg = sumG / count, mb = sumB / count;
            double score = 0;
            for (int i = 0; i < count; i++)
            {
                var s = samples[i];
                score += (s.R - mr) * (s.R - mr) + (s.G - mg) * (s.G - mg) + (s.B - mb) * (s.B - mb);
            }
            // Compare boundary structure independently of exposure. A small colour penalty
            // breaks ties without preferring a flat donor over a matching real edge.
            score = score / count + 0.015 * (mr * mr + mg * mg + mb * mb);
            if (score < best)
            {
                best = score; bestDx = dx; bestDy = dy;
                bestSamples = samples[..count].ToArray();
            }
        }
        return bestSamples is null ? null : new(bestDx, bestDy, FitColourPlane(bestSamples));
    }

    private static double[] FitColourPlane(BoundarySample[] samples)
    {
        // Match low-frequency shading using clean boundary samples; keep the donor's grain.
        var matrix = new double[3, 6];
        Span<double> v = stackalloc double[3];
        foreach (var sample in samples)
        {
            v[0] = 1; v[1] = sample.X; v[2] = sample.Y;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) matrix[row, col] += v[row] * v[col];
                for (int c = 0; c < 3; c++) matrix[row, c + 3] += v[row] * sample.Channel(c);
            }
        }
        // Stabilise partially obscured rings without penalising the constant colour offset.
        matrix[1, 1] += samples.Length * 0.05;
        matrix[2, 2] += samples.Length * 0.05;
        for (int pivot = 0; pivot < 3; pivot++)
        {
            double scale = matrix[pivot, pivot];
            for (int col = pivot; col < 6; col++) matrix[pivot, col] /= scale;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = matrix[row, pivot];
                for (int col = pivot; col < 6; col++) matrix[row, col] -= factor * matrix[pivot, col];
            }
        }
        var result = new double[9];
        for (int c = 0; c < 3; c++) for (int row = 0; row < 3; row++) result[c * 3 + row] = matrix[row, c + 3];
        return result;
    }
}
