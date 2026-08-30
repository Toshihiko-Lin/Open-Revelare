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
    BlackPointCompensation = 0x2000,
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

    public ColorTransformRequest(
        ColorProfileRef source,
        ColorProfileRef destination,
        TransformPurpose purpose,
        RenderingIntent intent = RenderingIntent.RelativeColorimetric,
        bool blackPointCompensation = false,
        double adaptationState = 1.0,
        PixelFormatDescriptor sourceFormat = PixelFormatDescriptor.RgbFloat32,
        PixelFormatDescriptor destinationFormat = PixelFormatDescriptor.RgbFloat32)
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

        Source = source;
        Destination = destination;
        Purpose = purpose;
        Intent = intent;
        BlackPointCompensation = blackPointCompensation;
        AdaptationState = adaptationState;
        SourceFormat = sourceFormat;
        DestinationFormat = destinationFormat;

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
    CmmTransformFlags EffectiveFlags)
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
            effectiveFlags);
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

public sealed class ColorManagementException : Exception
{
    public ColorManagementException(string message) : base(message) { }
    public ColorManagementException(string message, Exception innerException) : base(message, innerException) { }
}
