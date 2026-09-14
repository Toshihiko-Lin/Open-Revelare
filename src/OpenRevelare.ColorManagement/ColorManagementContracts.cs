namespace OpenRevelare.ColorManagement;

public enum RenderingIntent : uint
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
}

public enum TransformPurpose
{
    InputToWorking,
    WorkingToOutput,
    PrintLutOutput,
    Proof,
    CanonicalPreview,
    MonitorPresentation,
    Test,
}

public enum PixelFormatDescriptor : uint
{
    /// <summary>Interleaved R, G, B; one IEEE-754 float32 per channel.</summary>
    RgbFloat32 = (1u << 22) | (4u << 16) | (3u << 3) | 4u,

    /// <summary>
    /// Interleaved CIE X, Y, Z in the ICC D50 profile connection space; one IEEE-754
    /// float32 per channel.
    /// </summary>
    XyzFloat32 = (1u << 22) | (9u << 16) | (3u << 3) | 4u,
}

[Flags]
public enum CmmTransformFlags : uint
{
    None = 0,
    NoCache = 0x0040,
    NoOptimize = 0x0100,
    HighResPrecalc = 0x0400,
    BlackPointCompensation = 0x2000,
}

/// <summary>
/// How faithfully a transform reproduces the profiles' maths.
///
/// <see cref="Exact"/> is the contract every export, full-resolution decode and render runs
/// under: float in and out, no optimisation, no cache — the pipeline is evaluated stage by
/// stage per pixel, which is what keeps extended-range values (negative, above one) intact and
/// makes the result the CMM's own answer to the last bit. It is also slow, because LittleCMS
/// applies none of its optimisations to a float pipeline: a matrix-shaper profile costs a
/// parametric-curve evaluation per channel per pixel (~400 ns a pixel on a 2020s desktop).
///
/// <see cref="Preview"/> trades that for speed where the pixels are only LOOKED AT and
/// measured — the editor preview, the strip, the sheet tiles, the roll-wide analysis: the
/// transform runs in LittleCMS's 16-bit integer domain with its optimisations on (a
/// pre-linearised 33-point CLUT, or the 1.14 fixed-point matrix shaper), so values are
/// quantised to 1/65535 and clamped to [0,1], and agree with <see cref="Exact"/> to roughly
/// 1e-4. The decode recipe records which precision produced a frame, and nothing that is
/// written to a file ever comes from a <see cref="Preview"/> transform.
/// </summary>
public enum TransformPrecision
{
    Exact,
    Preview,
}

public sealed record CmmBuildIdentity(
    string Product,
    string RequiredRelease,
    int EncodedNativeVersion,
    string ReportedNativeVersion,
    string LogicalLibraryName,
    CmmTransformFlags FixedFlags,
    string NativeSha256,
    string SourceSha256,
    string BuildOptions,
    string NativePath);

public sealed record CmmNativeError(uint Code, string Message)
{
    public override string ToString() => $"lcms error {Code}: {Message}";
}

public sealed record ProfileValidationResult(
    bool IsValid,
    ProfileIdentity Identity,
    string Description,
    uint? ColorSpaceSignature,
    uint? PcsSignature,
    uint? EncodedIccVersion,
    CmmNativeError? NativeError,
    string Message);

public sealed class ColorTransformRequest
{
    public ColorProfileRef Source { get; }
    public ColorProfileRef Destination { get; }
    public TransformPurpose Purpose { get; }
    public RenderingIntent Intent { get; }
    public bool BlackPointCompensation { get; }
    public double AdaptationState { get; }
    public PixelFormatDescriptor SourceFormat { get; }
    public PixelFormatDescriptor DestinationFormat { get; }
    public TransformPrecision Precision { get; }

    public ColorTransformRequest(
        ColorProfileRef source,
        ColorProfileRef destination,
        TransformPurpose purpose,
        RenderingIntent intent = RenderingIntent.RelativeColorimetric,
        bool blackPointCompensation = false,
        double adaptationState = 1.0,
        PixelFormatDescriptor sourceFormat = PixelFormatDescriptor.RgbFloat32,
        PixelFormatDescriptor destinationFormat = PixelFormatDescriptor.RgbFloat32,
        TransformPrecision precision = TransformPrecision.Exact)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        if (!double.IsFinite(adaptationState) || adaptationState is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(adaptationState), "Adaptation state must be finite and in [0,1].");
        if (!IsSupported(sourceFormat))
            throw new NotSupportedException($"Unsupported source pixel format: {sourceFormat}.");
        if (!IsSupported(destinationFormat))
            throw new NotSupportedException($"Unsupported destination pixel format: {destinationFormat}.");
        if (!Enum.IsDefined(precision)) throw new ArgumentOutOfRangeException(nameof(precision));
        if (precision == TransformPrecision.Preview
            && (sourceFormat != PixelFormatDescriptor.RgbFloat32 || destinationFormat != PixelFormatDescriptor.RgbFloat32))
            throw new NotSupportedException("Preview precision is defined for RGB float32 transforms only.");

        Source = source;
        Destination = destination;
        Purpose = purpose;
        Intent = intent;
        BlackPointCompensation = blackPointCompensation;
        AdaptationState = adaptationState;
        SourceFormat = sourceFormat;
        DestinationFormat = destinationFormat;
        Precision = precision;

        static bool IsSupported(PixelFormatDescriptor format) =>
            format is PixelFormatDescriptor.RgbFloat32 or PixelFormatDescriptor.XyzFloat32;
    }
}

public readonly record struct ColorTransformKey(
    ProfileIdentity Source,
    ProfileIdentity Destination,
    TransformPurpose Purpose,
    RenderingIntent Intent,
    bool BlackPointCompensation,
    long AdaptationStateBits,
    PixelFormatDescriptor SourceFormat,
    PixelFormatDescriptor DestinationFormat,
    int EncodedCmmVersion,
    CmmTransformFlags EffectiveFlags,
    TransformPrecision Precision)
{
    public static ColorTransformKey From(
        ColorTransformRequest request,
        CmmBuildIdentity build,
        CmmTransformFlags effectiveFlags) => new(
            request.Source.Identity,
            request.Destination.Identity,
            request.Purpose,
            request.Intent,
            request.BlackPointCompensation,
            BitConverter.DoubleToInt64Bits(request.AdaptationState),
            request.SourceFormat,
            request.DestinationFormat,
            build.EncodedNativeVersion,
            effectiveFlags,
            request.Precision);
}

public sealed record CmmDiagnosticsSnapshot(
    CmmBuildIdentity Build,
    long ProfileValidations,
    long ProfileValidationFailures,
    long LeaseRequests,
    long CacheHits,
    long CacheMisses,
    long TransformsCreated,
    long TransformCalls,
    long PixelsTransformed,
    long NativeErrors,
    int ThreadCaches,
    int CachedTransforms,
    int ActiveOperations);

public interface IColorTransformLease : IDisposable
{
    ColorTransformKey Key { get; }
    void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount);
}

public interface IColorManagementEngine : IDisposable
{
    CmmBuildIdentity Build { get; }
    ProfileValidationResult Validate(ColorProfileRef profile);
    IColorTransformLease Lease(ColorTransformRequest request);
    CmmDiagnosticsSnapshot GetDiagnostics();
}

public class ColorManagementException : Exception
{
    public ColorManagementException(string message) : base(message) { }
    public ColorManagementException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The CMM could not build a transform out of profiles that were themselves valid.
///
/// <para>
/// This is an engine or environment fault, not a statement about the file, and input admission
/// must therefore NOT answer it by substituting a fallback profile. Doing so would render the
/// picture through a space nobody chose, in response to a failure that says nothing about the
/// file's colorimetry. A profile the CMM *rejected* is the opposite case and does fall back —
/// there, the file really is the thing that cannot be trusted.
/// </para>
/// </summary>
public sealed class ColorTransformCreationException : ColorManagementException
{
    public ColorTransformCreationException(string message) : base(message) { }

    public ColorTransformCreationException(string message, Exception innerException)
        : base(message, innerException) { }
}
