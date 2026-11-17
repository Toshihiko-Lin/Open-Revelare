using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRevelare.Core;

public enum CalibrationDeviceKind { CameraCopy, Scanner }
/// <summary>Requested physical quantity, never an assertion that calibration has succeeded.</summary>
public enum CaptureMeasurementTarget { DeviceRelativeTransmission, DefinedSpectralTransmission, StandardDensity }
public enum CaptureSignalState { Unknown, RawSensorSamples, LinearDeviceChannels, ColorManagedPixels }
/// <summary>User-recorded upstream processing, not independently verified applicability.</summary>
public enum UpstreamCorrectionState { Unknown, NotApplied, Applied }
public enum ScannerAcquisitionMode { Unknown, Transmission, Reflection }
public enum ScannerCalibrationWorkflow { Unspecified, SilverFastIt8, VueScanIt8, ArgyllCms, ManufacturerMaintenance, MeasuredDensity }
public sealed record CameraCaptureSetup
{
    public string Lens { get; init; } = "";
    public string Aperture { get; init; } = "";
    public string Iso { get; init; } = "";
    public string Exposure { get; init; } = "";
    public string Geometry { get; init; } = "";
}
public sealed record ScannerCaptureSetup
{
    public string Software { get; init; } = "";
    public string SoftwareVersion { get; init; } = "";
    public ScannerAcquisitionMode Mode { get; init; }
    public string Resolution { get; init; } = "";
    public string ExposureAndGain { get; init; } = "";
    public UpstreamCorrectionState BlackCorrection { get; init; }
    public UpstreamCorrectionState SpatialCorrection { get; init; }
    public string ExportSettings { get; init; } = "";
    public ScannerCalibrationWorkflow Workflow { get; init; }
}
public enum CalibrationReferenceRole { Dark, OpenGate, FlatField, TargetImage, TargetData, InputIcc, LightSpectrum, CameraResponse, ValidationImage }
public sealed record CalibrationReference(CalibrationReferenceRole Role, string Path, string Sha256);

/// <summary>A reference collection, not a validated or automatically applied calibration.</summary>
public sealed record DeviceCalibrationProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public CalibrationDeviceKind Kind { get; init; }
    public string DeviceModel { get; init; } = "";
    public string DeviceSerial { get; init; } = "";
    public string CaptureConditions { get; init; } = "";
    public CaptureMeasurementTarget MeasurementTarget { get; init; } = CaptureMeasurementTarget.DeviceRelativeTransmission;
    public CaptureSignalState InputState { get; init; } = CaptureSignalState.Unknown;
    public string MeasurementDefinition { get; init; } = "";
    public string LightSource { get; init; } = "";
    public string DecodeRecipe { get; init; } = "";
    public string AlreadyAppliedCorrections { get; init; } = "";
    public CameraCaptureSetup Camera { get; init; } = new();
    public ScannerCaptureSetup Scanner { get; init; } = new();
    public string TargetIdentifier { get; init; } = "";
    public string Notes { get; init; } = "";
    public CalibrationReference[] References { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported device calibration schema.");
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name)
            || string.IsNullOrWhiteSpace(DeviceModel) || !Enum.IsDefined(Kind)
            || !Enum.IsDefined(MeasurementTarget) || !Enum.IsDefined(InputState))
            throw new InvalidDataException("Invalid device calibration identity, name or model.");
        if (Camera is null || Scanner is null || !Enum.IsDefined(Scanner.Mode)
            || !Enum.IsDefined(Scanner.BlackCorrection) || !Enum.IsDefined(Scanner.SpatialCorrection) || !Enum.IsDefined(Scanner.Workflow))
            throw new InvalidDataException("Invalid camera or scanner acquisition setup.");
        if (References is null || References.Any(r => r is null || !Enum.IsDefined(r.Role)
            || string.IsNullOrWhiteSpace(r.Path) || !System.IO.Path.IsPathFullyQualified(r.Path)
            || r.Sha256 is null || r.Sha256.Length != 64 || !r.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid device calibration reference.");
    }
}

/// <summary>Local manifest storage. Reference files remain external and are never edited.</summary>
public sealed class DeviceCalibrationStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string[] LoadErrors { get; private set; } = [];
    public DeviceCalibrationProfile[] LoadAll()
    {
        var profiles = new List<DeviceCalibrationProfile>(); var errors = new List<string>();
        if (Directory.Exists(directory))
            foreach (string path in Directory.GetFiles(directory, "*.json").Order())
            {
                try { profiles.Add(Read(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
        LoadErrors = errors.ToArray(); return profiles.ToArray();
    }

    public static DeviceCalibrationProfile Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Calibration manifest exceeds 1 MiB.");
        var profile = JsonSerializer.Deserialize<DeviceCalibrationProfile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty calibration manifest.");
        profile.Validate();
        return profile;
    }

    public void Save(DeviceCalibrationProfile profile)
    {
        profile.Validate();
        Directory.CreateDirectory(directory);
        Export(profile, Path.Combine(directory, profile.Id + ".json"));
    }

    public static void Export(DeviceCalibrationProfile profile, string path)
    {
        profile.Validate();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (profile.References.Any(r => comparer.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(path))))
            throw new IOException("Calibration manifest cannot overwrite a reference file.");
        string json = JsonSerializer.Serialize(profile, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
            throw new InvalidDataException("Calibration manifest exceeds 1 MiB.");
        ExportFile.Write(path, temporary => File.WriteAllText(temporary, json));
    }

    public static CalibrationReference Register(CalibrationReferenceRole role, string path)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        path = Path.GetFullPath(path);
        var before = RawSourceCache.Revision.Capture(path);
        using var stream = File.OpenRead(path);
        string digest = Convert.ToHexString(SHA256.HashData(stream));
        if (RawSourceCache.Revision.Capture(path) != before) throw new IOException("Reference changed while reading.");
        return new(role, path, digest);
    }

    public static string[] CheckReferences(DeviceCalibrationProfile profile)
    {
        profile.Validate();
        return profile.References.Select(r =>
        {
            try { return Register(r.Role, r.Path).Sha256.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase) ? null : r.Path; }
            catch (IOException) { return r.Path; }
            catch (UnauthorizedAccessException) { return r.Path; }
        }).OfType<string>().ToArray();
    }
}
