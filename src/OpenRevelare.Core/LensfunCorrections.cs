namespace OpenRevelare.Core;

/// <summary>
/// Linear-domain Lensfun database correction. Models and radius conventions are documented at
/// https://lensfun.github.io/manual/latest/group__Lens.html . No native Lensfun code is required.
/// Vignetting is corrected in sensor coordinates, before distortion resamples the image.
/// </summary>
public static class LensfunCorrections
{
    public static bool IsActive(FrameParams p) => p.LensfunEnabled && p.Lensfun is { IsUsable: true };

    public static ImageBuffer Apply(ImageBuffer source, FrameParams p, FrameRegion? sourceRegion = null)
    {
        if (!IsActive(p)) return source;
        var calibration = p.Lensfun!;
        var region = sourceRegion ?? FrameRegion.Whole(source.Width, source.Height);
        var result = new ImageBuffer(source.Width, source.Height, (float[])source.Data.Clone()).InheritSourceFrom(source);
        // LCC includes corner colour casts and illumination; never multiply profile vignetting
        // on top. Both must see the original sensor coordinates before geometric correction.
        if (p.LccFlatField is not null)
            Lcc.Apply(result.Data, result.Width, result.Height, p.LccFlatField, region);
        else ApplyVignetting(result, calibration, region);
        if (calibration.Distortion is null) return result;

        int w = source.Width, h = source.Height;
        var output = new ImageBuffer(w, h).InheritSourceFrom(source);
        var (cx, cy, norm) = Coordinates(calibration, region.FrameWidth, region.FrameHeight);
        var distortion = calibration.Distortion;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x + region.OffsetX - cx, dy = y + region.OffsetY - cy;
                double r = Math.Sqrt(dx * dx + dy * dy) / norm;
                double scale = DistortionScale(distortion, r);
                double sx = Math.Clamp(Math.Clamp(cx + dx * scale, 0, region.FrameWidth - 1) - region.OffsetX, 0, w - 1);
                double sy = Math.Clamp(Math.Clamp(cy + dy * scale, 0, region.FrameHeight - 1) - region.OffsetY, 0, h - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                double fx = sx - x0, fy = sy - y0;
                for (int c = 0; c < 3; c++)
                    output.Data[(y * w + x) * 3 + c] = (float)(
                        (result.Data[(y0 * w + x0) * 3 + c] * (1 - fx) + result.Data[(y0 * w + x1) * 3 + c] * fx) * (1 - fy)
                        + (result.Data[(y1 * w + x0) * 3 + c] * (1 - fx) + result.Data[(y1 * w + x1) * 3 + c] * fx) * fy);
            }
        });
        return output;
    }

    internal static double DistortionScale(LensfunDistortion d, double radius) => d.Model switch
    {
        "poly3" => 1 - d.A + d.A * radius * radius,
        "poly5" => 1 + d.A * radius * radius + d.B * Math.Pow(radius, 4),
        "ptlens" => 1 - d.A - d.B - d.C + d.A * Math.Pow(radius, 3) + d.B * radius * radius + d.C * radius,
        _ => 1,
    };

    private static (double X, double Y, double Norm) Coordinates(LensfunCalibration c, double w, double h)
        => ((w - 1) * 0.5 + Math.Min(w - 1, h - 1) * 0.5 * c.CenterX,
            (h - 1) * 0.5 + Math.Min(w - 1, h - 1) * 0.5 * c.CenterY,
            Math.Sqrt(w * w + h * h) * c.CameraCropFactor
                / (2 * c.Distortion!.CropFactor * Math.Sqrt(1 + c.Distortion.AspectRatio * c.Distortion.AspectRatio)));

    /// <summary>Conservative interval bounds, including interior polynomial extrema.</summary>
    public static (double X0, double Y0, double X1, double Y1) SourceBounds(
        double x0, double y0, double x1, double y1, LensfunCalibration c, int w, int h)
    {
        if (c.Distortion is not { } d) return (x0, y0, x1, y1);
        var (cx, cy, norm) = Coordinates(c, w, h);
        double dx0 = x0 - cx, dx1 = x1 - cx, dy0 = y0 - cy, dy1 = y1 - cy;
        double MinAbs(double a, double b) => a <= 0 && b >= 0 ? 0 : Math.Min(Math.Abs(a), Math.Abs(b));
        double rmin = Math.Sqrt(Math.Pow(MinAbs(dx0, dx1), 2) + Math.Pow(MinAbs(dy0, dy1), 2)) / norm;
        double rmax = Math.Sqrt(Math.Max(dx0 * dx0, dx1 * dx1) + Math.Max(dy0 * dy0, dy1 * dy1)) / norm;
        var radii = new List<double> { rmin, rmax };
        void Root(double r) { if (double.IsFinite(r) && r > rmin && r < rmax) radii.Add(r); }
        if (d.Model == "poly5" && d.B != 0) Root(Math.Sqrt(-d.A / (2 * d.B)));
        if (d.Model == "ptlens")
        {
            if (d.A == 0) { if (d.B != 0) Root(-d.C / (2 * d.B)); }
            else
            {
                double discriminant = 4 * d.B * d.B - 12 * d.A * d.C;
                if (discriminant >= 0)
                {
                    Root((-2 * d.B - Math.Sqrt(discriminant)) / (6 * d.A));
                    Root((-2 * d.B + Math.Sqrt(discriminant)) / (6 * d.A));
                }
            }
        }
        double s0 = radii.Min(r => DistortionScale(d, r)), s1 = radii.Max(r => DistortionScale(d, r));
        (double Min, double Max) Product(double a, double b)
        {
            double[] values = [a * s0, a * s1, b * s0, b * s1];
            return (values.Min(), values.Max());
        }
        var xs = Product(dx0, dx1); var ys = Product(dy0, dy1);
        return (cx + xs.Min, cy + ys.Min, cx + xs.Max, cy + ys.Max);
    }

    public static void ApplyVignetting(ImageBuffer image, LensfunCalibration calibration, FrameRegion? sourceRegion = null)
    {
        if (calibration.Vignetting is not { } v || !calibration.IsUsable) return;
        var region = sourceRegion ?? FrameRegion.Whole(image.Width, image.Height);
        double fw = region.FrameWidth, fh = region.FrameHeight;
        double cx = (fw - 1) * 0.5 + Math.Min(fw - 1, fh - 1) * 0.5 * calibration.CenterX;
        double cy = (fh - 1) * 0.5 + Math.Min(fw - 1, fh - 1) * 0.5 * calibration.CenterY;
        // PA uses half the calibration sensor's diagonal, unlike the distortion models.
        double norm = Math.Sqrt(fw * fw + fh * fh) * calibration.CameraCropFactor / (2 * v.CropFactor);
        Parallel.For(0, image.Height, y =>
        {
            for (int x = 0; x < image.Width; x++)
            {
                double dx = (x + region.OffsetX - cx) / norm, dy = (y + region.OffsetY - cy) / norm;
                double r2 = dx * dx + dy * dy;
                double falloff = 1 + r2 * (v.K1 + r2 * (v.K2 + r2 * v.K3));
                double gain = 1 / Math.Max(0.01, falloff);
                int i = (y * image.Width + x) * 3;
                for (int c = 0; c < 3; c++) image.Data[i + c] *= (float)gain;
            }
        });
    }
}
