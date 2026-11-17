namespace OpenRevelare.Core;

public sealed record ScannerReferencePatch(int X, int Y, int Width, int Height, double[] ReferenceDensity, bool Validation);
public sealed record ScannerReferenceSet
{
    public int SchemaVersion { get; init; } = 1;
    public bool IsTemplate { get; init; } = true;
    public bool IndependentValidationCaptureDeclared { get; init; }
    public string TargetIdentifier { get; init; } = "";
    public string ReferenceDefinition { get; init; } = "";
    public string ReferenceMeasurementConditions { get; init; } = "";
    public string MaterialScope { get; init; } = "";
    public double MaximumValidationDensityError { get; init; }
    public ScannerReferencePatch[] Patches { get; init; } = [];
}
public sealed record ScannerDensityModel(string Domain, string ReferenceDefinition, string TargetIdentifier,
    double[] Scale, double[] Offset, double[] MinimumInputDensity, double[] MaximumInputDensity,
    double[] ValidationRmse, double[] ValidationMaximumError, int TrainingPatches, int ValidationPatches,
    double AcceptedMaximumError, string MaterialScope, string ReferenceMeasurementConditions,
    double[] TrainingRmse, double[] TrainingMaximumError);
public sealed record ScannerDensityMeasurement(float[] Density, bool[] ValidChannels, ScannerDensityModel Model);

/// <summary>Reference-driven per-channel density response, not ICC/colour inversion or dark/open.</summary>
public static class ScannerCaptureCalibration
{
    public static ScannerDensityModel Fit(ImageBuffer training, ImageBuffer validation, ScannerReferenceSet reference)
    {
        ArgumentNullException.ThrowIfNull(training); ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.SchemaVersion != 1 || reference.IsTemplate || !reference.IndependentValidationCaptureDeclared || string.IsNullOrWhiteSpace(reference.TargetIdentifier)
            || string.IsNullOrWhiteSpace(reference.ReferenceDefinition)
            || string.IsNullOrWhiteSpace(reference.ReferenceMeasurementConditions) || string.IsNullOrWhiteSpace(reference.MaterialScope)
            || !double.IsFinite(reference.MaximumValidationDensityError) || reference.MaximumValidationDensityError <= 0
            || reference.Patches is null || reference.Patches.Any(p => p is null))
            throw new ArgumentException("Complete the measured target definition, explicit error tolerance and template flag first.");
        var train = new List<(double[] Input, double[] Target)>();
        var check = new List<(double[] Input, double[] Target)>();
        foreach (var p in reference.Patches)
        {
            ImageBuffer image = p.Validation ? validation : training;
            if (p.ReferenceDensity is null || p.ReferenceDensity.Length != 3
                || p.ReferenceDensity.Any(d => !double.IsFinite(d) || d < 0))
                throw new ArgumentException("Each target patch requires three finite measured reference densities.");
            var input = SampleDensity(image, p);
            (p.Validation ? check : train).Add((input, p.ReferenceDensity));
        }
        if (train.Count < 3 || check.Count < 2)
            throw new ArgumentException("At least three training and two independent validation patches are required.");
        // Overlap within one image would count the same measurement more than once.
        var patches = reference.Patches;
        for (int i = 0; i < patches.Length; i++)
        for (int j = 0; j < i; j++)
            if (patches[i].Validation == patches[j].Validation && Overlap(patches[i], patches[j]))
                throw new ArgumentException("Reference regions in the same image must not overlap.");
        var scale = new double[3]; var offset = new double[3];
        var min = new double[3]; var max = new double[3]; var rmse = new double[3]; var error = new double[3];
        var trainingRmse = new double[3]; var trainingError = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double mx = train.Average(p => p.Input[c]), my = train.Average(p => p.Target[c]);
            double variance = train.Sum(p => Math.Pow(p.Input[c] - mx, 2));
            if (variance < 1e-10 || train.Select(p => p.Target[c]).Distinct().Count() < 3)
                throw new ArgumentException("Target patches do not span a nondegenerate density response.");
            scale[c] = train.Sum(p => (p.Input[c] - mx) * (p.Target[c] - my)) / variance;
            offset[c] = my - scale[c] * mx;
            if (!double.IsFinite(scale[c]) || scale[c] <= 0 || !double.IsFinite(offset[c]))
                throw new InvalidOperationException("Scanner response must be finite and monotonic.");
            min[c] = train.Min(p => p.Input[c]); max[c] = train.Max(p => p.Input[c]);
            var trainingErrors = train.Select(p => scale[c] * p.Input[c] + offset[c] - p.Target[c]).ToArray();
            trainingRmse[c] = Math.Sqrt(trainingErrors.Average(e => e * e)); trainingError[c] = trainingErrors.Max(Math.Abs);
            if (trainingError[c] > reference.MaximumValidationDensityError)
                throw new InvalidOperationException("Training residual exceeds the specified density-error limit; the affine model is insufficient.");
            if (check.Any(p => p.Input[c] < min[c] || p.Input[c] > max[c]))
                throw new InvalidOperationException("Validation patches must lie within the trained response range.");
            var errors = check.Select(p => scale[c] * p.Input[c] + offset[c] - p.Target[c]).ToArray();
            rmse[c] = Math.Sqrt(errors.Average(e => e * e)); error[c] = errors.Max(Math.Abs);
            if (error[c] > reference.MaximumValidationDensityError)
                throw new InvalidOperationException("Independent validation exceeds the user-specified density-error limit.");
        }
        return new("specified-reference-density/scanner-affine-v1", reference.ReferenceDefinition, reference.TargetIdentifier,
            scale, offset, min, max, rmse, error, train.Count, check.Count, reference.MaximumValidationDensityError,
            reference.MaterialScope, reference.ReferenceMeasurementConditions, trainingRmse, trainingError);
    }

    /// <summary>Produces density, not display RGB. Outside the trained range is invalid, never extrapolated.</summary>
    public static ScannerDensityMeasurement Apply(ImageBuffer deviceChannels, ScannerDensityModel model)
    {
        ArgumentNullException.ThrowIfNull(deviceChannels); ArgumentNullException.ThrowIfNull(model);
        if (model.Domain != "specified-reference-density/scanner-affine-v1"
            || string.IsNullOrWhiteSpace(model.MaterialScope) || string.IsNullOrWhiteSpace(model.ReferenceMeasurementConditions)
            || new[] { model.Scale, model.Offset, model.MinimumInputDensity, model.MaximumInputDensity }
                .Any(a => a is null || a.Length != 3 || a.Any(v => !double.IsFinite(v)))
            || Enumerable.Range(0, 3).Any(c => model.Scale[c] <= 0 || model.MinimumInputDensity[c] >= model.MaximumInputDensity[c]))
            throw new ArgumentException("Invalid scanner density-response model.");
        var output = new float[deviceChannels.Data.Length]; var valid = new bool[output.Length];
        for (int i = 0; i < output.Length; i++)
        {
            float v = deviceChannels.Data[i]; int c = i % 3;
            double density = v > 0 && float.IsFinite(v) ? -Math.Log10(v) : double.NaN;
            double mapped = model.Scale[c] * density + model.Offset[c];
            valid[i] = density >= model.MinimumInputDensity[c] && density <= model.MaximumInputDensity[c]
                && double.IsFinite(mapped) && float.IsFinite((float)mapped)
                && !(deviceChannels.SourceQuantisationStep > 0 && v >= 1);
            output[i] = valid[i] ? (float)mapped : float.NaN;
        }
        return new(output, valid, model);
    }

    /// <summary>Mean in linear device units first, then log; no clipping/epsilon, CMM or black subtraction.</summary>
    private static double[] SampleDensity(ImageBuffer image, ScannerReferencePatch p)
    {
        if (p.X < 0 || p.Y < 0 || p.Width <= 0 || p.Height <= 0
            || (long)p.X + p.Width > image.Width || (long)p.Y + p.Height > image.Height)
            throw new ArgumentException("Patch lies outside the unrotated source image.");
        var mean = new double[3];
        for (int y = p.Y; y < p.Y + p.Height; y++)
        for (int x = p.X; x < p.X + p.Width; x++)
        for (int c = 0; c < 3; c++)
        {
            float v = image.Data[(y * image.Width + x) * 3 + c];
            if (!float.IsFinite(v) || v <= 0 || (image.SourceQuantisationStep > 0 && v >= 1))
                throw new InvalidOperationException("Patch contains nonpositive, nonfinite or endpoint samples; choose an unclipped reference region.");
            mean[c] += v;
        }
        long count = (long)p.Width * p.Height;
        return mean.Select(v => -Math.Log10(v / count)).ToArray();
    }
    private static bool Overlap(ScannerReferencePatch a, ScannerReferencePatch b) =>
        a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width && a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
}
