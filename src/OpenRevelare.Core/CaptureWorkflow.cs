using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

public enum CaptureWorkflowMode { RecordUpstream, CameraTransmission, ScannerInputIcc }
public sealed record CaptureWorkflowSource(string OriginalPath, string OriginalSha256,
    string WorkingPath, string WorkingSha256, long AboveOpenSamples);

/// <summary>Frozen provenance. Working paths are self-contained inputs, never instructions to reapply a correction.</summary>
public sealed record CaptureWorkflowRun(DeviceCalibrationProfile Profile, CaptureWorkflowMode Mode,
    string Recipe, CaptureWorkflowSource[] Sources)
{
    public void Validate()
    {
        if (Profile is null) throw new InvalidDataException("Missing capture workflow profile.");
        Profile.Validate();
        if (!Enum.IsDefined(Mode) || string.IsNullOrWhiteSpace(Recipe) || Sources is null || Sources.Length == 0
            || Sources.Any(s => s is null || !Path.IsPathFullyQualified(s.OriginalPath)
                || !Path.IsPathFullyQualified(s.WorkingPath) || !Digest(s.OriginalSha256)
                || !Digest(s.WorkingSha256) || s.AboveOpenSamples < 0))
            throw new InvalidDataException("Invalid capture workflow provenance.");
        if (Mode == CaptureWorkflowMode.CameraTransmission && Profile.Kind != CalibrationDeviceKind.CameraCopy
            || Mode == CaptureWorkflowMode.ScannerInputIcc && Profile.Kind != CalibrationDeviceKind.Scanner)
            throw new InvalidDataException("Capture workflow device type does not match its mode.");
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public static class CaptureWorkflow
{
    public static void ValidateSelection(DeviceCalibrationProfile profile, CaptureWorkflowMode mode, bool camera)
    {
        profile.Validate();
        if (!Enum.IsDefined(mode) || (profile.Kind == CalibrationDeviceKind.CameraCopy) != camera)
            throw new InvalidOperationException("Calibration device type does not match the selected files.");
        if (mode == CaptureWorkflowMode.RecordUpstream) return;
        if (mode == CaptureWorkflowMode.CameraTransmission)
        {
            if (!camera || profile.InputState != CaptureSignalState.RawSensorSamples
                || profile.MeasurementTarget != CaptureMeasurementTarget.DeviceRelativeTransmission)
                throw new InvalidOperationException("Camera workflow requires RAW samples and device-relative transmission.");
            _ = One(profile, CalibrationReferenceRole.Dark); _ = One(profile, CalibrationReferenceRole.OpenGate);
        }
        else
        {
            if (camera || profile.InputState == CaptureSignalState.ColorManagedPixels)
                throw new InvalidOperationException("Input ICC requires scanner device values that have not already been colour converted.");
            _ = One(profile, CalibrationReferenceRole.InputIcc);
        }
    }

    /// <summary>Sequential batch, with private new output directory and manifest committed last.</summary>
    public static CaptureWorkflowRun Prepare(DeviceCalibrationProfile profile, CaptureWorkflowMode mode,
        IReadOnlyList<string> paths, string outputRoot, IColorManagementEngine engine, Action<int, int>? progress = null)
    {
        if (paths.Count == 0) throw new ArgumentException("No capture inputs.");
        foreach (string path in paths) ValidateSelection(profile, mode, RawDecode.IsRawExtension(path));
        RequireReferences(profile);
        string recipe = mode switch
        {
            CaptureWorkflowMode.RecordUpstream => "upstream calibration recorded; source pixels unchanged",
            CaptureWorkflowMode.CameraTransmission => "camera-transmission-v1; active CFA (sample-dark)/(open-dark); bilinear RGB; LibRaw orientation; float32; no extra black, ICC or inversion",
            _ => "scanner-input-icc-v1; exact LittleCMS relative-colorimetric -> linear ACEScg; no BPC; float32; no inversion",
        };
        string? directory = mode == CaptureWorkflowMode.RecordUpstream ? null
            : Path.Combine(Path.GetFullPath(outputRoot), Guid.NewGuid().ToString("N"));
        var created = new List<string>(); var sources = new List<CaptureWorkflowSource>();
        try
        {
            if (directory is not null) Directory.CreateDirectory(directory);
            CameraRawMosaic? dark = null, open = null;
            ColorProfileRef? icc = null;
            if (mode == CaptureWorkflowMode.CameraTransmission)
            {
                dark = CameraCaptureCalibration.ReadRaw(One(profile, CalibrationReferenceRole.Dark).Path, profile.Id);
                open = CameraCaptureCalibration.ReadRaw(One(profile, CalibrationReferenceRole.OpenGate).Path, profile.Id);
                CheckCameraIdentity(profile, open.Metadata);
            }
            if (mode == CaptureWorkflowMode.ScannerInputIcc)
            {
                var reference = One(profile, CalibrationReferenceRole.InputIcc);
                var asset = ScannerStandardAssets.Read(reference.Path);
                if (asset.Role != CalibrationReferenceRole.InputIcc) throw new InvalidDataException("Expected scanner input ICC.");
                icc = ColorProfileRef.Create(File.ReadAllBytes(reference.Path), profile.Name,
                    ProfileRole.Input, new ProfileSource.Embedded(profile.Id, "capture calibration"));
            }
            for (int i = 0; i < paths.Count; i++)
            {
                string path = Path.GetFullPath(paths[i]);
                string originalHash = Hash(path);
                string output = path; long above = 0;
                if (directory is not null)
                {
                    ImageBuffer pixels;
                    if (mode == CaptureWorkflowMode.CameraTransmission)
                    {
                        var sample = CameraCaptureCalibration.ReadRaw(path, profile.Id);
                        var measured = CameraCaptureCalibration.Measure(sample, dark!, open!);
                        pixels = ToBilinearRgb(measured);
                        int minDenominator = int.MaxValue;
                        for (int p = 0; p < open!.Samples.Length; p++)
                            minDenominator = Math.Min(minDenominator, open.Samples[p] - dark!.Samples[p]);
                        // Conservative source-code lattice; float storage does not add measurement precision.
                        pixels.SourceQuantisationStep = 1.0 / minDenominator;
                        above = measured.Issues.LongCount(v => v == CaptureSampleIssue.AboveOpenReference);
                    }
                    else pixels = TiffIO.LoadWithScannerInputProfile(path, icc!, engine).Pixels;
                    if (pixels.Data.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Capture transform produced non-finite pixels.");
                    output = Path.Combine(directory, $"{i + 1:D4}-{Path.GetFileNameWithoutExtension(path)}.tif");
                    TiffIO.ExportCaptureIntermediate(pixels, output, mode == CaptureWorkflowMode.ScannerInputIcc,
                        recipe + "; original-sha256=" + originalHash);
                    created.Add(output);
                }
                if (Hash(path) != originalHash) throw new IOException("Capture input changed during preparation.");
                sources.Add(new(path, originalHash, output, output == path ? originalHash : Hash(output), above));
                progress?.Invoke(i + 1, paths.Count);
            }
            RequireReferences(profile);
            var run = new CaptureWorkflowRun(profile, mode, recipe, sources.ToArray()); run.Validate();
            if (directory is not null)
            {
                string manifest = Path.Combine(directory, "capture-workflow.json");
                ExportFile.Write(manifest, temp => File.WriteAllText(temp, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true })));
                created.Add(manifest);
            }
            return run;
        }
        catch
        {
            // Only exact files created by this run; no recursive deletion or broad cache cleanup.
            foreach (string path in created) { try { File.Delete(path); } catch (IOException) { } }
            if (directory is not null) { try { Directory.Delete(directory, false); } catch (IOException) { } }
            throw;
        }
    }

    public static ImageBuffer ToBilinearRgb(CameraTransmissionMeasurement measurement)
    {
        int w = measurement.Metadata.Width, h = measurement.Metadata.Height;
        if (w < 2 || h < 2 || measurement.Transmission.Length != checked(w * h)
            || measurement.Issues.Length != measurement.Transmission.Length)
            throw new InvalidDataException("Invalid transmission lattice.");
        long invalid = 0;
        for (int i = 0; i < measurement.Transmission.Length; i++)
            if (!float.IsFinite(measurement.Transmission[i]) || measurement.Transmission[i] <= 0
                || (measurement.Issues[i] & ~CaptureSampleIssue.AboveOpenReference) != 0) invalid++;
        if (invalid > 0) throw new InvalidOperationException($"Camera transmission has {invalid} unusable CFA samples (clipped, nonpositive or invalid reference). No interpolation across invalid measurements is allowed; review the RAW sample/dark/open captures.");
        int[] cfa = measurement.Metadata.BayerChannels.Split(',').Select(int.Parse).Select(c => c == 3 ? 1 : c).ToArray();
        if (cfa.Length != 4 || !cfa.Contains(0) || !cfa.Contains(1) || !cfa.Contains(2)) throw new NotSupportedException("RGB Bayer required.");
        int flip = measurement.Metadata.Orientation;
        if (flip is not (0 or 3 or 5 or 6)) throw new NotSupportedException("Unsupported sensor orientation.");
        bool rotate = flip is 5 or 6;
        var rgb = new ImageBuffer(rotate ? h : w, rotate ? w : h);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                (int ox, int oy) = flip switch { 3 => (w - 1 - x, h - 1 - y), 5 => (y, w - 1 - x), 6 => (h - 1 - y, x), _ => (x, y) };
                for (int c = 0; c < 3; c++)
                {
                    float value;
                    if (cfa[(y % 2) * 2 + x % 2] == c) value = measurement.Transmission[y * w + x];
                    else
                    {
                        double sum = 0; int count = 0;
                        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                                if (cfa[(yy % 2) * 2 + xx % 2] == c) { sum += measurement.Transmission[yy * w + xx]; count++; }
                        value = (float)(sum / count);
                    }
                    rgb.Data[(oy * rgb.Width + ox) * 3 + c] = value;
                }
            }
        });
        return rgb;
    }

    private static void CheckCameraIdentity(DeviceCalibrationProfile profile, CameraRawMosaicMetadata meta)
    {
        if (!profile.DeviceModel.Equals(meta.Model, StringComparison.OrdinalIgnoreCase)
            && !profile.DeviceModel.Equals(meta.Make + " " + meta.Model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Calibration camera model does not match the RAW reference metadata.");
        if (profile.DeviceSerial.Length > 0 && profile.DeviceSerial != meta.Serial)
            throw new InvalidOperationException("Calibration camera serial does not match the RAW reference metadata.");
    }
    private static CalibrationReference One(DeviceCalibrationProfile profile, CalibrationReferenceRole role)
    {
        var matches = profile.References.Where(r => r.Role == role).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException($"Exactly one {role} reference is required.");
    }
    private static void RequireReferences(DeviceCalibrationProfile profile)
    {
        if (DeviceCalibrationStore.CheckReferences(profile).Length > 0)
            throw new IOException("Calibration reference missing or changed. Register the reference again.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
}
