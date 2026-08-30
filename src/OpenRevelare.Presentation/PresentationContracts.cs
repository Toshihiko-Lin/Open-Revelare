using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Presentation;

/// <summary>The byte-level encoding accepted by a presentation surface.</summary>
public enum PresentationEncoding
{
    /// <summary>D65 linear extended-sRGB/scRGB, interleaved little-endian RGBA half.</summary>
    LinearExtendedSrgbRgba16F,

    /// <summary>Already transformed to the target monitor, interleaved BGRA8.</summary>
    MonitorDeviceBgra8,

    /// <summary>Explicitly unmanaged interleaved sRGB BGRA8 emergency output.</summary>
    UnmanagedEmergencySrgb8,
}

/// <summary>Owns the single final transform from presentation RGB to monitor-device RGB.</summary>
public enum FinalTransformOwner
{
    SystemCompositor,
    Application,
    None,
}

/// <summary>A non-empty pixel extent.</summary>
public readonly record struct PixelSize
{
    public int Width { get; }
    public int Height { get; }

    public PixelSize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

        // Validate the most common downstream multiplication while the offending dimensions are
        // still visible at the API boundary.
        _ = checked(width * height);
        Width = width;
        Height = height;
    }

    public int PixelCount => checked(Width * Height);

    internal int RequiredByteCount(int bytesPerPixel) => checked(PixelCount * bytesPerPixel);
}

/// <summary>
/// Immutable pixels prepared for one exact display-contract revision.
///
/// <para>
/// <see cref="ApplicationMonitorTransformCount"/> is deliberately carried with the pixels rather
/// than inferred by a platform presenter. It makes the exactly-once transform invariant auditable:
/// system-managed and emergency buffers carry zero; monitor-device buffers carry one.
/// </para>
/// </summary>
public sealed class PresentationBuffer
{
    public ImmutableArray<byte> Bytes { get; }
    public PixelSize Size { get; }
    public PresentationEncoding Encoding { get; }
    public string TargetDisplayId { get; }
    public long ContractRevision { get; }
    public ProfileIdentity? AppliedDeviceProfileIdentity { get; }
    public float SdrReferenceWhite { get; }
    public float ReferenceWhiteScale { get; }
    public int ApplicationMonitorTransformCount { get; }

    public PresentationBuffer(
        ReadOnlySpan<byte> bytes,
        PixelSize size,
        PresentationEncoding encoding,
        string targetDisplayId,
        long contractRevision,
        ProfileIdentity? appliedDeviceProfileIdentity,
        float sdrReferenceWhite,
        float referenceWhiteScale,
        int applicationMonitorTransformCount)
        : this(
            bytes.ToArray(),
            size,
            encoding,
            targetDisplayId,
            contractRevision,
            appliedDeviceProfileIdentity,
            sdrReferenceWhite,
            referenceWhiteScale,
            applicationMonitorTransformCount)
    {
    }

    private PresentationBuffer(
        byte[] bytes,
        PixelSize size,
        PresentationEncoding encoding,
        string targetDisplayId,
        long contractRevision,
        ProfileIdentity? appliedDeviceProfileIdentity,
        float sdrReferenceWhite,
        float referenceWhiteScale,
        int applicationMonitorTransformCount)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        ValidatePixelSize(size);
        if (!Enum.IsDefined(encoding)) throw new ArgumentOutOfRangeException(nameof(encoding));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDisplayId);
        if (contractRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(contractRevision), "Revision cannot be negative.");
        RequireFinitePositive(sdrReferenceWhite, nameof(sdrReferenceWhite));
        RequireFinitePositive(referenceWhiteScale, nameof(referenceWhiteScale));
        if (applicationMonitorTransformCount < 0)
            throw new ArgumentOutOfRangeException(
                nameof(applicationMonitorTransformCount),
                "Transform count cannot be negative.");

        int bytesPerPixel = encoding == PresentationEncoding.LinearExtendedSrgbRgba16F ? 8 : 4;
        int requiredLength = size.RequiredByteCount(bytesPerPixel);
        if (bytes.Length != requiredLength)
        {
            throw new ArgumentException(
                $"{encoding} requires exactly {requiredLength} bytes for {size.Width}x{size.Height}; received {bytes.Length}.",
                nameof(bytes));
        }

        switch (encoding)
        {
            case PresentationEncoding.LinearExtendedSrgbRgba16F:
                RejectAppliedProfile(appliedDeviceProfileIdentity, encoding);
                RequireTransformCount(applicationMonitorTransformCount, expected: 0, encoding);
                break;

            case PresentationEncoding.MonitorDeviceBgra8:
                if (appliedDeviceProfileIdentity is null)
                {
                    throw new ArgumentException(
                        "Monitor-device pixels must identify the exact device profile applied to them.",
                        nameof(appliedDeviceProfileIdentity));
                }
                RequireTransformCount(applicationMonitorTransformCount, expected: 1, encoding);
                break;

            case PresentationEncoding.UnmanagedEmergencySrgb8:
                RejectAppliedProfile(appliedDeviceProfileIdentity, encoding);
                RequireTransformCount(applicationMonitorTransformCount, expected: 0, encoding);
                break;
        }

        Bytes = ImmutableCollectionsMarshal.AsImmutableArray(bytes);
        Size = size;
        Encoding = encoding;
        TargetDisplayId = targetDisplayId;
        ContractRevision = contractRevision;
        AppliedDeviceProfileIdentity = appliedDeviceProfileIdentity;
        SdrReferenceWhite = sdrReferenceWhite;
        ReferenceWhiteScale = referenceWhiteScale;
        ApplicationMonitorTransformCount = applicationMonitorTransformCount;
    }

    /// <summary>
    /// Adopts fully-built bytes that have never escaped the presentation assembly. The public
    /// constructor remains defensive for caller-owned storage.
    /// </summary>
    internal static PresentationBuffer FromOwnedBytes(
        byte[] bytes,
        PixelSize size,
        PresentationEncoding encoding,
        string targetDisplayId,
        long contractRevision,
        ProfileIdentity? appliedDeviceProfileIdentity,
        float sdrReferenceWhite,
        float referenceWhiteScale,
        int applicationMonitorTransformCount) =>
        new(
            bytes,
            size,
            encoding,
            targetDisplayId,
            contractRevision,
            appliedDeviceProfileIdentity,
            sdrReferenceWhite,
            referenceWhiteScale,
            applicationMonitorTransformCount);

    private static void RejectAppliedProfile(ProfileIdentity? profile, PresentationEncoding encoding)
    {
        if (profile is not null)
        {
            throw new ArgumentException(
                $"{encoding} pixels must not carry an application-applied monitor profile.",
                nameof(profile));
        }
    }

    private static void RequireTransformCount(int actual, int expected, PresentationEncoding encoding)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"{encoding} requires application monitor transform count {expected}; received {actual}.",
                nameof(actual));
        }
    }

    private static void ValidatePixelSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Pixel size must be non-empty.");
    }

    private static void RequireFinitePositive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and positive.");
    }
}

/// <summary>
/// A snapshot of one display's presentation requirements. The valid owner/encoding combinations
/// are intentionally closed: system+FP16, application+device-BGRA8, or none+warned emergency.
/// </summary>
public sealed class DisplayContract
{
    public string DisplayId { get; }
    public long Revision { get; }
    public PresentationEncoding Encoding { get; }
    public FinalTransformOwner TransformOwner { get; }
    public ColorProfileRef? DeviceProfile { get; }
    public float SdrReferenceWhite { get; }
    public float ReferenceWhiteScale { get; }
    public float ExtendedHeadroom { get; }
    public string DiagnosticName { get; }
    public string? VisibleWarning { get; }

    /// <summary>The only valid application monitor-transform count for this contract.</summary>
    public int RequiredApplicationMonitorTransformCount =>
        TransformOwner == FinalTransformOwner.Application ? 1 : 0;

    public bool RequiresPassthroughSurface => TransformOwner == FinalTransformOwner.Application;
    public bool RequiresSystemColorManagedSurface => TransformOwner == FinalTransformOwner.SystemCompositor;
    public bool IsWysiwygGuaranteed => TransformOwner != FinalTransformOwner.None;

    public DisplayContract(
        string displayId,
        long revision,
        PresentationEncoding encoding,
        FinalTransformOwner transformOwner,
        ColorProfileRef? deviceProfile,
        float sdrReferenceWhite,
        float referenceWhiteScale,
        float extendedHeadroom,
        string diagnosticName,
        string? visibleWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision), "Revision cannot be negative.");
        if (!Enum.IsDefined(encoding)) throw new ArgumentOutOfRangeException(nameof(encoding));
        if (!Enum.IsDefined(transformOwner)) throw new ArgumentOutOfRangeException(nameof(transformOwner));
        RequireFinitePositive(sdrReferenceWhite, nameof(sdrReferenceWhite));
        RequireFinitePositive(referenceWhiteScale, nameof(referenceWhiteScale));
        if (!float.IsFinite(extendedHeadroom) || extendedHeadroom < 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extendedHeadroom),
                "Extended headroom must be finite and at least 1.0.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticName);

        ValidateMode(encoding, transformOwner, deviceProfile, visibleWarning);

        DisplayId = displayId;
        Revision = revision;
        Encoding = encoding;
        TransformOwner = transformOwner;
        DeviceProfile = deviceProfile;
        SdrReferenceWhite = sdrReferenceWhite;
        ReferenceWhiteScale = referenceWhiteScale;
        ExtendedHeadroom = extendedHeadroom;
        DiagnosticName = diagnosticName;
        VisibleWarning = visibleWarning;
    }

    /// <summary>
    /// Rejects a buffer prepared for another display, revision, encoding, profile, reference-white
    /// policy, or final-transform count. Presenters should call this immediately before upload.
    /// </summary>
    public void Validate(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!string.Equals(frame.TargetDisplayId, DisplayId, StringComparison.Ordinal))
            throw Mismatch($"display id '{frame.TargetDisplayId}' does not match '{DisplayId}'");
        if (frame.ContractRevision != Revision)
            throw Mismatch($"contract revision {frame.ContractRevision} is stale; current revision is {Revision}");
        if (frame.Encoding != Encoding)
            throw Mismatch($"encoding {frame.Encoding} does not match {Encoding}");
        if (frame.ApplicationMonitorTransformCount != RequiredApplicationMonitorTransformCount)
        {
            throw Mismatch(
                $"application monitor transform count {frame.ApplicationMonitorTransformCount} does not match required count {RequiredApplicationMonitorTransformCount}");
        }
        if (frame.SdrReferenceWhite != SdrReferenceWhite || frame.ReferenceWhiteScale != ReferenceWhiteScale)
            throw Mismatch("reference-white data does not match the current display contract");

        ProfileIdentity? expectedProfile =
            TransformOwner == FinalTransformOwner.Application ? DeviceProfile!.Identity : null;
        if (frame.AppliedDeviceProfileIdentity != expectedProfile)
            throw Mismatch("applied device-profile identity does not match the current display contract");
    }

    private static void ValidateMode(
        PresentationEncoding encoding,
        FinalTransformOwner owner,
        ColorProfileRef? deviceProfile,
        string? visibleWarning)
    {
        switch (owner)
        {
            case FinalTransformOwner.SystemCompositor:
                if (encoding != PresentationEncoding.LinearExtendedSrgbRgba16F)
                    throw InvalidMode(owner, encoding);
                ValidateMonitorRoleIfPresent(deviceProfile);
                if (!string.IsNullOrWhiteSpace(visibleWarning))
                    throw new ArgumentException("A managed system-compositor contract cannot carry an unmanaged warning.", nameof(visibleWarning));
                break;

            case FinalTransformOwner.Application:
                if (encoding != PresentationEncoding.MonitorDeviceBgra8)
                    throw InvalidMode(owner, encoding);
                if (deviceProfile is null)
                    throw new ArgumentNullException(nameof(deviceProfile), "Application-managed presentation requires an exact monitor profile.");
                ValidateMonitorRoleIfPresent(deviceProfile);
                if (!string.IsNullOrWhiteSpace(visibleWarning))
                    throw new ArgumentException("A managed application contract cannot carry an unmanaged warning.", nameof(visibleWarning));
                break;

            case FinalTransformOwner.None:
                if (encoding != PresentationEncoding.UnmanagedEmergencySrgb8)
                    throw InvalidMode(owner, encoding);
                if (deviceProfile is not null)
                    throw new ArgumentException("Unmanaged emergency presentation cannot claim a device profile.", nameof(deviceProfile));
                ArgumentException.ThrowIfNullOrWhiteSpace(visibleWarning);
                break;
        }
    }

    private static void ValidateMonitorRoleIfPresent(ColorProfileRef? profile)
    {
        if (profile is not null && profile.Role != ProfileRole.Monitor)
        {
            throw new ArgumentException(
                $"Display device profile must have role {ProfileRole.Monitor}; received {profile.Role}.",
                nameof(profile));
        }
    }

    private static ArgumentException InvalidMode(FinalTransformOwner owner, PresentationEncoding encoding) =>
        new($"Final-transform owner {owner} is incompatible with presentation encoding {encoding}.");

    private PresentationContractException Mismatch(string detail) =>
        new($"Presentation buffer rejected for display '{DisplayId}' revision {Revision}: {detail}.");

    private static void RequireFinitePositive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and positive.");
    }
}

public sealed class PresentationContractException : InvalidOperationException
{
    public PresentationContractException(string message) : base(message) { }
}

public interface IDisplayEnvironment : IDisposable
{
    DisplayContract Current { get; }
    event EventHandler<DisplayContract>? ContractChanged;
}

public interface IPreviewPresenter : IDisposable
{
    PresentationEncoding AcceptedEncoding { get; }
    void Resize(PixelSize pixels, double scale);
    void Present(PresentationBuffer frame);
}
