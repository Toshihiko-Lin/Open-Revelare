using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly byte[] SchemaMarker =
        Encoding.UTF8.GetBytes("OpenRevelare.RenderFingerprint/v1");

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

    /// <summary>
    /// Computes the stable ManagedV2 identity of the exact rendered handoff. Integers and
    /// IEEE-754 sample bits are serialized little-endian so the same pixels/profile/recipe hash
    /// identically on every supported platform. Display state is deliberately absent.
    /// </summary>
    public static Computed ComputeManaged(
        ImageBuffer pixels,
        CharacterizedPixelEncoding encoding,
        OutputRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.PipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentException(
                "Only ManagedV2 renders have a versioned fingerprint recipe.", nameof(recipe));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SchemaMarker);

        AppendInt32(hash, pixels.Width);
        AppendInt32(hash, pixels.Height);
        AppendInt32(hash, pixels.Data.Length);
        AppendFloatBits(hash, pixels.Data);

        AppendProfile(hash, encoding.Profile);
        AppendInt32(hash, (int)encoding.Reference);
        AppendInt32(hash, (int)encoding.Transfer);
        AppendInt32(hash, (int)encoding.Range);

        AppendInt32(hash, (int)recipe.PipelineVersion);
        AppendProfile(hash, recipe.RequestedOutputProfile);
        AppendInt32(hash, (int)recipe.Intent);
        AppendBoolean(hash, recipe.BlackPointCompensation);
        AppendString(hash, recipe.GamutPolicy);
        AppendString(hash, StablePrintLutFingerprintIdentity(recipe.PrintLutIdentity));
        AppendBoolean(hash, recipe.PixelProfileMismatch);

        return new Computed(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// A project from the legacy era may still carry an external .cube filesystem path in the
    /// recipe. A path is diagnostic context, not render identity: the same file moves between
    /// machines. ManagedV2 currently rejects uncharacterized external cubes, so successful
    /// managed recipes use a portable built-in sentinel. Keep the fingerprint future-proof by
    /// accepting an explicit content identity and otherwise hashing only a stable external-LUT
    /// marker; the rendered pixel bits still distinguish different looks.
    /// </summary>
    private static string StablePrintLutFingerprintIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
        if (PrintLuts.IsBuiltin(identity)) return identity.ToLowerInvariant();

        const string ContentPrefix = "sha256:";
        if (identity.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ProfileIdentity content = ProfileIdentity.Parse(identity[ContentPrefix.Length..]);
                return ContentPrefix + content.Sha256Hex;
            }
            catch (FormatException)
            {
                // An invalid content token is not allowed to smuggle a path-like, machine-local
                // value back into a supposedly deterministic fingerprint.
            }
            catch (ArgumentException)
            {
                // Empty/whitespace payload: treat it like every other unstable external token.
            }
        }

        return "external-print-lut:path-excluded";
    }

    private static void AppendProfile(IncrementalHash hash, ColorProfileRef profile)
    {
        byte[] bytes = profile.IccBytes.ToArray();
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendBoolean(IncrementalHash hash, bool value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value ? (byte)1 : (byte)0;
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendFloatBits(IncrementalHash hash, ReadOnlySpan<float> values)
    {
        // Every supported production target is little-endian. Hash its contiguous backing bytes
        // directly instead of serialising tens of millions of samples one at a time; retain the
        // explicit path below so the fingerprint format itself is still architecture-independent.
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendData(MemoryMarshal.AsBytes(values));
            return;
        }

        const int BatchFloats = 1024;
        Span<byte> bytes = stackalloc byte[BatchFloats * sizeof(float)];
        int offset = 0;
        while (offset < values.Length)
        {
            int count = Math.Min(BatchFloats, values.Length - offset);
            Span<byte> batch = bytes[..(count * sizeof(float))];
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    batch.Slice(i * sizeof(float), sizeof(float)),
                    BitConverter.SingleToInt32Bits(values[offset + i]));
            }
            hash.AppendData(batch);
            offset += count;
        }
    }
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

    /// <summary>
    /// Retains the exact encoding/profile/recipe identity when a post-render spatial operation
    /// (for example export downsampling) replaces only the pixel storage.
    /// </summary>
    public RenderedFrame WithPixels(ImageBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        RenderFingerprint fingerprint = Fingerprint is RenderFingerprint.Computed
            ? RenderFingerprint.ComputeManaged(pixels, Encoding, Recipe)
            : Fingerprint;
        return new RenderedFrame(pixels, Encoding, Recipe, fingerprint);
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
