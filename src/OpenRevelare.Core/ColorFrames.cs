using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>Stable semantic identity of the working representation.</summary>
public readonly record struct WorkingSpaceId(string Name, int Version)
{
    public static WorkingSpaceId LinearAcesCgV1 { get; } = new("linear ACEScg F32", 1);
}

public enum WorkingAdmission
{
    ConvertedFromCharacterized,
    AlreadyLinearAcesCg,
    LegacyPartialIccTransform,
    LegacyUncharacterizedPassthrough,
}

/// <summary>Where a decoded frame came from and how its original meaning was resolved.</summary>
public sealed record SourceDescriptor
{
    public string StableSourceId { get; }
    public string DisplayName { get; }
    public PixelEncoding OriginalEncoding { get; }
    public string DecodeRecipe { get; }

    public SourceDescriptor(
        string stableSourceId,
        string displayName,
        PixelEncoding originalEncoding,
        string decodeRecipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(originalEncoding);
        ArgumentException.ThrowIfNullOrWhiteSpace(decodeRecipe);
        StableSourceId = stableSourceId;
        DisplayName = displayName;
        OriginalEncoding = originalEncoding;
        DecodeRecipe = decodeRecipe;
    }
}

/// <summary>Decoded pixels whose encoding still describes those exact numbers.</summary>
public sealed class SourceFrame
{
    public ImageBuffer Pixels { get; }
    public PixelEncoding Encoding { get; }
    public SourceDescriptor Source { get; }

    public SourceFrame(ImageBuffer pixels, PixelEncoding encoding, SourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(source);
        if (!Equals(encoding, source.OriginalEncoding))
            throw new ArgumentException("Source descriptor encoding must describe the same pixels.", nameof(source));
        Pixels = pixels;
        Encoding = encoding;
        Source = source;
    }
}

/// <summary>
/// Pixels admitted to the render working domain. Legacy admission is explicit: it preserves the
/// v1 look without rewriting an unknown camera/scanner source as a characterized profile.
/// </summary>
public sealed class WorkingFrame
{
    public ImageBuffer Pixels { get; }
    public WorkingSpaceId Space { get; }
    public WorkingAdmission Admission { get; }
    public SourceDescriptor Source { get; }

    public WorkingFrame(
        ImageBuffer pixels,
        WorkingSpaceId space,
        WorkingAdmission admission,
        SourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(source);
        Pixels = pixels;
        Space = space;
        Admission = admission;
        Source = source;
    }

    public WorkingFrame WithPixels(ImageBuffer pixels) => new(pixels, Space, Admission, Source);
}

public enum ColorPipelineVersion
{
    LegacyV1 = 1,
    ManagedV2 = 2,
}

public sealed record OutputRecipe
{
    public ColorPipelineVersion PipelineVersion { get; }
    public ColorProfileRef RequestedOutputProfile { get; }
    public RenderingIntent Intent { get; }
    public bool BlackPointCompensation { get; }
    public string GamutPolicy { get; }
    public string PrintLutIdentity { get; }
    public bool PixelProfileMismatch { get; }

    public OutputRecipe(
        ColorPipelineVersion pipelineVersion,
        ColorProfileRef requestedOutputProfile,
        RenderingIntent intent,
        bool blackPointCompensation,
        string gamutPolicy,
        string printLutIdentity,
        bool pixelProfileMismatch)
    {
        ArgumentNullException.ThrowIfNull(requestedOutputProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamutPolicy);
        ArgumentNullException.ThrowIfNull(printLutIdentity);
        PipelineVersion = pipelineVersion;
        RequestedOutputProfile = requestedOutputProfile;
        Intent = intent;
        BlackPointCompensation = blackPointCompensation;
        GamutPolicy = gamutPolicy;
        PrintLutIdentity = printLutIdentity;
        PixelProfileMismatch = pixelProfileMismatch;
    }
}

public enum FingerprintUnavailableReason
{
    LegacyPipelineHasNoVersionedRecipe,
    TransientPreview,
}

public abstract record RenderFingerprint
{
    private RenderFingerprint() { }

    public sealed record Computed : RenderFingerprint
    {
        public string Sha256Hex { get; }

        public Computed(string sha256Hex) => Sha256Hex = Validate(sha256Hex);

        private static string Validate(string value)
        {
            _ = ProfileIdentity.Parse(value);
            return value.ToLowerInvariant();
        }
    }

    public sealed record Unavailable(FingerprintUnavailableReason Reason) : RenderFingerprint;
}

/// <summary>Immutable semantic handoff produced by the render boundary.</summary>
public sealed class RenderedFrame
{
    public ImageBuffer Pixels { get; }
    public CharacterizedPixelEncoding Encoding { get; }
    public ColorProfileRef OutputProfile => Encoding.Profile;
    public OutputRecipe Recipe { get; }
    public RenderFingerprint Fingerprint { get; }

    public RenderedFrame(
        ImageBuffer pixels,
        CharacterizedPixelEncoding encoding,
        OutputRecipe recipe,
        RenderFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(fingerprint);
        Pixels = pixels;
        Encoding = encoding;
        Recipe = recipe;
        Fingerprint = fingerprint;
    }
}

/// <summary>LCC calibration field; not a display/render image.</summary>
public sealed class FlatFieldFrame
{
    public ImageBuffer Pixels { get; }

    public FlatFieldFrame(ImageBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Pixels = pixels;
    }
}

/// <summary>Normalized sRGB-encoded frame at the Deep-WB model boundary.</summary>
public sealed class SrgbModelFrame
{
    public ImageBuffer Pixels { get; }

    public SrgbModelFrame(ImageBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Pixels = pixels;
    }
}
