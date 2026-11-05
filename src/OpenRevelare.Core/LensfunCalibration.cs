using System.Globalization;
using System.Xml.Linq;

namespace OpenRevelare.Core;

// Coefficients are stored with the project, so reopening never depends on a database update.
public sealed record LensfunDistortion(string Model, double A, double B, double C,
    double CropFactor, double AspectRatio);
public sealed record LensfunVignetting(double K1, double K2, double K3, double CropFactor);
public sealed record LensfunCalibration(string Profile, string Camera, double FocalLength, double? Aperture,
    double CameraCropFactor, double FocusDistance, double CenterX, double CenterY,
    LensfunDistortion? Distortion, LensfunVignetting? Vignetting)
{
    public bool IsUsable => double.IsFinite(CameraCropFactor) && CameraCropFactor > 0
        && double.IsFinite(FocalLength) && FocalLength > 0
        && double.IsFinite(CenterX) && double.IsFinite(CenterY)
        && (Distortion is not null || Vignetting is not null)
        && (Distortion is not { } d || (d.Model is "poly3" or "poly5" or "ptlens"
            && Finite(d.A, d.B, d.C, d.CropFactor, d.AspectRatio) && d.CropFactor > 0 && d.AspectRatio >= 1))
        && (Vignetting is not { } v || (Finite(v.K1, v.K2, v.K3, v.CropFactor) && v.CropFactor > 0));

    private static bool Finite(params double[] numbers) => numbers.All(double.IsFinite);
}

public sealed partial class LensfunIdentificationDatabase
{
    private static double Number(string? value, double fallback = 0)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n)
            ? n : fallback;
    private static double Attr(XElement e, string name, double fallback = 0) => Number(e.Attribute(name)?.Value, fallback);

    /// <summary>Resolve the first file's parameters once for the entire roll.</summary>
    public LensfunCalibration? CreateCalibration(LensfunProfile profile, LensCaptureInfo capture)
    {
        if (!_profileXml.TryGetValue(profile, out var lens)) return null;
        double focal = capture.FocalLength ?? Attr(lens.Element("focal") ?? new XElement("focal"), "value");
        var cameras = _cameras.Where(c => SameName(capture.Camera, c.Model, c.Maker)
            && (capture.Make.Length == 0 || Normalize(capture.Make) == Normalize(c.Maker))).ToArray();
        double crop = cameras.Length == 1 ? cameras[0].CropFactor : capture.CropFactor ?? 0;
        if (focal <= 0 || crop <= 0) return null;
        double defaultCrop = Number(lens.Element("cropfactor")?.Value, 1);
        double Ratio(string? s)
        {
            var parts = s?.Split(':');
            return parts is { Length: 2 } && Number(parts[1]) > 0
                ? Number(parts[0], 3) / Number(parts[1]) : Number(s, 1.5);
        }
        double aspect = Ratio(lens.Element("aspect-ratio")?.Value);
        // Crop-factor tolerance follows the database's calibration applicability convention.
        var sets = lens.Elements("calibration").Select(c => new
        {
            Xml = c, Crop = Attr(c, "cropfactor", defaultCrop),
            Aspect = c.Attribute("aspect-ratio") is { } ar ? Ratio(ar.Value) : aspect,
        }).Where(c => c.Crop > 0 && crop / c.Crop >= 0.96).OrderByDescending(c => c.Crop).ToArray();
        var ds = sets.FirstOrDefault(c => c.Xml.Elements("distortion")
            .Any(d => d.Attribute("model")?.Value is "poly3" or "poly5" or "ptlens"));
        LensfunDistortion? distortion = null;
        if (ds is not null)
        {
            var samples = ds.Xml.Elements("distortion")
                .Where(d => d.Attribute("model")?.Value is "poly3" or "poly5" or "ptlens")
                .OrderBy(d => Attr(d, "focal")).ToArray();
            var lower = samples.LastOrDefault(d => Attr(d, "focal") <= focal) ?? samples[0];
            string model = lower.Attribute("model")!.Value;
            var upper = samples.FirstOrDefault(d => Attr(d, "focal") >= focal && d.Attribute("model")?.Value == model) ?? lower;
            double span = Attr(upper, "focal") - Attr(lower, "focal");
            double t = span > 0 ? Math.Clamp((focal - Attr(lower, "focal")) / span, 0, 1) : 0;
            double Coefficient(string name) => Attr(lower, name) * (1 - t) + Attr(upper, name) * t;
            distortion = model == "ptlens"
                ? new(model, Coefficient("a"), Coefficient("b"), Coefficient("c"), ds.Crop, ds.Aspect)
                : new(model, Coefficient("k1"), Coefficient("k2"), 0, ds.Crop, ds.Aspect);
        }
        double distance = capture.FocusDistance ?? 1000;
        LensfunVignetting? vignetting = null;
        var vs = sets.FirstOrDefault(c => c.Xml.Elements("vignetting").Any(v => v.Attribute("model")?.Value == "pa"));
        if (vs is not null && capture.Aperture is > 0)
        {
            // Inverse-distance interpolation over focal length, reciprocal aperture and distance.
            // Exact calibration measurements always win, including close-focus macro samples.
            var samples = vs.Xml.Elements("vignetting").Where(v => v.Attribute("model")?.Value == "pa"
                && Attr(v, "focal") > 0 && Attr(v, "aperture") > 0 && Attr(v, "distance") > 0).ToArray();
            double range = samples.Length > 0 ? Math.Max(1, samples.Max(v => Attr(v, "focal")) - samples.Min(v => Attr(v, "focal"))) : 1;
            double Distance(XElement v) => Math.Sqrt(Math.Pow((Attr(v, "focal") - focal) / range, 2)
                + Math.Pow(4 / Attr(v, "aperture") - 4 / capture.Aperture.Value, 2)
                + Math.Pow(0.1 / Attr(v, "distance") - 0.1 / distance, 2));
            var exact = samples.FirstOrDefault(v => Distance(v) < 0.0001);
            if (exact is not null) vignetting = new(Attr(exact, "k1"), Attr(exact, "k2"), Attr(exact, "k3"), vs.Crop);
            else if (samples.Length > 0 && samples.Min(Distance) <= 1)
            {
                var weighted = samples.Select(v => (Sample: v, Weight: Math.Pow(Distance(v), -3.5))).ToArray();
                double sum = weighted.Sum(v => v.Weight);
                double Coefficient(string name) => weighted.Sum(v => Attr(v.Sample, name) * v.Weight) / sum;
                vignetting = new(Coefficient("k1"), Coefficient("k2"), Coefficient("k3"), vs.Crop);
            }
        }
        var center = lens.Element("center");
        var result = new LensfunCalibration(profile.ToString(), capture.Camera, focal, capture.Aperture, crop, distance,
            center is null ? 0 : Attr(center, "x"), center is null ? 0 : Attr(center, "y"), distortion, vignetting);
        return result.IsUsable ? result : null;
    }
}
