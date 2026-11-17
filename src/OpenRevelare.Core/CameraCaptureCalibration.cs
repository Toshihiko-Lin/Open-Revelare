using OpenRevelare.Core.NativeRaw;

namespace OpenRevelare.Core;

/// <summary>Active-area coordinates in the unrotated sensor lattice; no display RGB meaning.</summary>
public sealed record CameraRawMosaicMetadata(int Width, int Height, int SensorWidth, int SensorHeight,
    int Left, int Top, int Orientation, string BayerChannels, string Make, string Model, string Serial,
    double Iso, double ExposureSeconds, double WhiteLevel, string SessionId, string Decoder,
    double Aperture = 0, double FocalLength = 0);
public sealed record CameraRawMosaic(ushort[] Samples, CameraRawMosaicMetadata Metadata, string SourcePath);

[Flags]
public enum CaptureSampleIssue : byte
{
    None = 0, SampleSaturated = 1, ReferenceSaturated = 2, InvalidDenominator = 4,
    NegativeTransmission = 8, ZeroTransmission = 16, AboveOpenReference = 32,
}

/// <summary>One transmission per CFA site, not demosaiced or oriented. NaN means no usable measurement.</summary>
public sealed record CameraTransmissionMeasurement(float[] Transmission, CaptureSampleIssue[] Issues,
    CameraRawMosaicMetadata Metadata, long UsableSamples, string[] SourcePaths)
{
    public string Domain => "device-channel-relative-transmission/cfa-v1";
    public string Processing => "LibRaw unpack -> active-area copy -> (sample-dark)/(open-dark); no extra black subtraction";
}

public static class CameraCaptureCalibration
{
    /// <summary>Session ID declares the same physical rig/light setup; it is not a hardware measurement.</summary>
    public static CameraRawMosaic ReadRaw(string path, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A declared capture session is required.", nameof(sessionId));
        if (RawDecode.UnsupportedRawReason(path) is { } reason) throw new NotSupportedException(reason);
        var revision = RawSourceCache.Revision.Capture(path);
        using var raw = RawContext.OpenFile(path);
        raw.Unpack();
        var result = raw.CopyUnprocessedMosaic(path, sessionId);
        if (RawSourceCache.Revision.Capture(path) != revision) throw new IOException("Source changed while reading CFA.");
        return result;
    }

    public static CameraTransmissionMeasurement Measure(CameraRawMosaic sample, CameraRawMosaic dark, CameraRawMosaic open)
    {
        Validate(sample); Validate(dark); Validate(open);
        RequireCompatible(sample.Metadata, dark.Metadata);
        RequireCompatible(sample.Metadata, open.Metadata);
        int length = sample.Samples.Length; long usable = 0;
        var output = new float[length]; var issues = new CaptureSampleIssue[length];
        for (int i = 0; i < length; i++)
        {
            int s = sample.Samples[i], d = dark.Samples[i], o = open.Samples[i];
            var issue = CaptureSampleIssue.None;
            if (s >= sample.Metadata.WhiteLevel) issue |= CaptureSampleIssue.SampleSaturated;
            if (o >= open.Metadata.WhiteLevel || d >= dark.Metadata.WhiteLevel) issue |= CaptureSampleIssue.ReferenceSaturated;
            int denominator = o - d;
            if (denominator <= 0) issue |= CaptureSampleIssue.InvalidDenominator;
            if (s < d) issue |= CaptureSampleIssue.NegativeTransmission;
            if (s == d) issue |= CaptureSampleIssue.ZeroTransmission;
            if (s > o) issue |= CaptureSampleIssue.AboveOpenReference;
            // Above-open values remain measured, with a diagnostic flag rather than a clamp.
            bool valid = (issue & ~CaptureSampleIssue.AboveOpenReference) == CaptureSampleIssue.None;
            output[i] = valid ? (float)((double)(s - d) / denominator) : float.NaN;
            issues[i] = issue;
            if (valid) usable++;
        }
        return new(output, issues, sample.Metadata, usable, [sample.SourcePath, dark.SourcePath, open.SourcePath]);
    }

    private static void Validate(CameraRawMosaic mosaic)
    {
        ArgumentNullException.ThrowIfNull(mosaic);
        var m = mosaic.Metadata ?? throw new ArgumentException("Missing CFA metadata.");
        if (m.Width < 1 || m.Height < 1 || m.SensorWidth < m.Width || m.SensorHeight < m.Height
            || m.Left < 0 || m.Top < 0 || (long)m.Left + m.Width > m.SensorWidth || (long)m.Top + m.Height > m.SensorHeight
            || mosaic.Samples is null || mosaic.Samples.LongLength != (long)m.Width * m.Height)
            throw new ArgumentException("Invalid active-area CFA geometry.");
        if (!double.IsFinite(m.Iso) || m.Iso <= 0 || !double.IsFinite(m.ExposureSeconds) || m.ExposureSeconds <= 0
            || !double.IsFinite(m.WhiteLevel) || m.WhiteLevel <= 0 || m.WhiteLevel > 65535
            || !double.IsFinite(m.Aperture) || m.Aperture < 0 || !double.IsFinite(m.FocalLength) || m.FocalLength < 0
            || string.IsNullOrWhiteSpace(m.SessionId) || string.IsNullOrWhiteSpace(m.Make)
            || string.IsNullOrWhiteSpace(m.Model) || string.IsNullOrWhiteSpace(m.Decoder))
            throw new ArgumentException("Missing or invalid capture conditions/electrical range.");
        string[] pattern = m.BayerChannels?.Split(',') ?? [];
        if (pattern.Length != 4 || pattern.Count(c => c == "0") != 1 || pattern.Count(c => c == "2") != 1
            || pattern.Count(c => c is "1" or "3") != 2
            || (Array.IndexOf(pattern, "0") ^ Array.IndexOf(pattern, "2")) != 3)
            throw new NotSupportedException("Only a declared 2x2 RGB Bayer pattern is supported.");
    }

    private static void RequireCompatible(CameraRawMosaicMetadata sample, CameraRawMosaicMetadata reference)
    {
        if (sample.Width != reference.Width || sample.Height != reference.Height
            || sample.SensorWidth != reference.SensorWidth || sample.SensorHeight != reference.SensorHeight
            || sample.Left != reference.Left || sample.Top != reference.Top || sample.Orientation != reference.Orientation
            || sample.BayerChannels != reference.BayerChannels)
            throw new InvalidOperationException("Reference CFA geometry, orientation or phase does not match.");
        if (sample.Make != reference.Make || sample.Model != reference.Model || sample.Serial != reference.Serial
            || sample.SessionId != reference.SessionId || sample.Decoder != reference.Decoder)
            throw new InvalidOperationException("Reference device, declared session or decoder does not match.");
        // First version requires exactly matching recorded exposure/gain; no inferred cross-exposure correction.
        if (sample.Iso != reference.Iso || sample.ExposureSeconds != reference.ExposureSeconds
            || sample.WhiteLevel != reference.WhiteLevel || sample.Aperture != reference.Aperture
            || sample.FocalLength != reference.FocalLength)
            throw new InvalidOperationException("Reference exposure, gain or white level does not match.");
    }
}
