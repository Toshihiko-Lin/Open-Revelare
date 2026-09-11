using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public class CanonicalPreviewConverterTests
{
    private const string FrozenD50XyzSha256 =
        "b8689e0c956615ecc56c49d2a007c28dfebb480c20c8edf7a40ce322252b8058";

    [Fact]
    public void D50_XYZ_connection_profile_has_a_frozen_identity_and_valid_XYZ_signature()
    {
        ColorProfileRef profile = PcsColorProfiles.D50Xyz;

        Assert.Equal(FrozenD50XyzSha256, profile.Identity.Sha256Hex);
        Assert.Equal(ProfileRole.CanonicalPresentation, profile.Role);
        ProfileSource.Generated source = Assert.IsType<ProfileSource.Generated>(profile.Source);
        Assert.Equal("LittleCMS.cmsCreateXYZProfile", source.GeneratorId);
        Assert.Equal("2.19.1/frozen-2024-01-01", source.GeneratorVersion);

        using var cmm = new LittleCmsEngine();
        ProfileValidationResult validation = cmm.Validate(profile);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(0x58595A20u, validation.ColorSpaceSignature); // 'XYZ '
        Assert.Equal(0x58595A20u, validation.PcsSignature);        // 'XYZ '
    }

    [Fact]
    public void XYZ_float_destination_uses_the_normal_complete_cache_key_and_lease_diagnostics()
    {
        using var cmm = new LittleCmsEngine();
        ColorProfileRef p3 = BuiltInColorProfiles.DisplayP3(ProfileRole.Output);
        var request = new ColorTransformRequest(
            p3,
            PcsColorProfiles.D50Xyz,
            TransformPurpose.CanonicalPreview,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            adaptationState: 1.0,
            sourceFormat: PixelFormatDescriptor.RgbFloat32,
            destinationFormat: PixelFormatDescriptor.XyzFloat32);

        float[] firstXyz = Transform(cmm, request, new[] { 1f, 0f, 0f }, out ColorTransformKey firstKey);
        float[] secondXyz = Transform(cmm, request, new[] { 1f, 0f, 0f }, out ColorTransformKey secondKey);

        Assert.Equal(firstKey, secondKey);
        Assert.Equal(p3.Identity, firstKey.Source);
        Assert.Equal(PcsColorProfiles.D50Xyz.Identity, firstKey.Destination);
        Assert.Equal(TransformPurpose.CanonicalPreview, firstKey.Purpose);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, firstKey.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.XyzFloat32, firstKey.DestinationFormat);
        Assert.Equal(LittleCmsEngine.FixedTransformFlags, firstKey.EffectiveFlags);
        Assert.Equal(firstXyz, secondXyz);

        // Independent values frozen from the Display P3 profile's D50 rXYZ tag. This confirms
        // that XYZ float is PCS data, not RGB numbers merely relabelled as XYZ.
        Assert.InRange(firstXyz[0], 0.5149f, 0.5153f);
        Assert.InRange(firstXyz[1], 0.2410f, 0.2414f);
        Assert.InRange(firstXyz[2], -0.0013f, -0.0008f);

        CmmDiagnosticsSnapshot diagnostics = cmm.GetDiagnostics();
        Assert.Equal(2, diagnostics.LeaseRequests);
        Assert.Equal(1, diagnostics.CacheMisses);
        Assert.Equal(1, diagnostics.CacheHits);
        Assert.Equal(1, diagnostics.TransformsCreated);
        Assert.Equal(2, diagnostics.TransformCalls);
        Assert.Equal(2, diagnostics.PixelsTransformed);
        Assert.Equal(0, diagnostics.ActiveOperations);
    }

    [Fact]
    public void Pixel_format_and_profile_color_space_must_match_on_each_transform_side()
    {
        using var cmm = new LittleCmsEngine();
        var invalid = new ColorTransformRequest(
            BuiltInColorProfiles.DisplayP3(ProfileRole.Output),
            PcsColorProfiles.D50Xyz,
            TransformPurpose.CanonicalPreview,
            sourceFormat: PixelFormatDescriptor.XyzFloat32,
            destinationFormat: PixelFormatDescriptor.XyzFloat32);

        ColorManagementException error = Assert.Throws<ColorManagementException>(
            () => cmm.Lease(invalid));
        Assert.Contains("source profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(PixelFormatDescriptor.XyzFloat32), error.Message);
        Assert.Contains("RGB", error.Message);
    }

    [Fact]
    public void Saturated_Display_P3_red_keeps_negative_and_above_one_canonical_components()
    {
        using var cmm = new LittleCmsEngine();
        RenderedFrame rendered = Rendered(
            BuiltInColorProfiles.DisplayP3(ProfileRole.Output),
            1f, 0f, 0f,
            0f, 1f, 0f);

        PresentationScene scene = CanonicalPreviewConverter.Convert(
            rendered,
            cmm,
            referenceWhiteScale: 1.25f);
        IReadOnlyList<Half> rgba = scene.LinearExtendedSrgbRgba;

        Assert.Equal(new PixelSize(2, 1), scene.Size);
        Assert.Equal(1.25f, scene.ReferenceWhiteScale);
        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, scene.Encoding);
        Assert.Equal(8, rgba.Count);

        // ICC -> D50 XYZ -> Bradford D65 -> linear sRGB, independently calculated from the
        // public Display P3 primaries. Half tolerance includes only the final F16 rounding.
        Assert.InRange((float)rgba[0], 1.223f, 1.227f);
        Assert.InRange((float)rgba[1], -0.043f, -0.041f);
        Assert.InRange((float)rgba[2], -0.021f, -0.018f);
        Assert.Equal((Half)1f, rgba[3]);

        // The second pixel proves RGBA is pixel-interleaved rather than planar and that alpha is
        // synthesized as opaque for every rendered RGB pixel.
        Assert.True((float)rgba[4] < 0f);
        Assert.True((float)rgba[5] > 1f);
        Assert.True((float)rgba[6] < 0f);
        Assert.Equal((Half)1f, rgba[7]);
    }

    [Fact]
    public void Saturated_Adobe_RGB_green_keeps_out_of_sRGB_negative_components()
    {
        using var cmm = new LittleCmsEngine();
        RenderedFrame rendered = Rendered(
            BuiltInColorProfiles.AdobeRgb(ProfileRole.Output),
            0f, 1f, 0f);

        PresentationScene scene = CanonicalPreviewConverter.Convert(
            rendered,
            cmm,
            referenceWhiteScale: 1f);
        IReadOnlyList<Half> rgba = scene.LinearExtendedSrgbRgba;

        Assert.InRange((float)rgba[0], -0.401f, -0.395f);
        Assert.InRange((float)rgba[1], 0.998f, 1.002f);
        Assert.InRange((float)rgba[2], -0.045f, -0.040f);
        Assert.Equal((Half)1f, rgba[3]);
    }

    private static float[] Transform(
        IColorManagementEngine cmm,
        ColorTransformRequest request,
        float[] source,
        out ColorTransformKey key)
    {
        float[] destination = new float[source.Length];
        using IColorTransformLease lease = cmm.Lease(request);
        key = lease.Key;
        lease.Apply(source, destination, source.Length / 3);
        return destination;
    }

    private static RenderedFrame Rendered(ColorProfileRef profile, params float[] rgb)
    {
        var pixels = new ImageBuffer(rgb.Length / 3, 1, rgb);
        var encoding = new CharacterizedPixelEncoding(
            profile,
            ColorReference.DisplayReferred,
            TransferState.ProfileEncoded,
            NumericRange.Normalized);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            profile,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            gamutPolicy: "test-vector",
            printLutIdentity: "",
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            new RenderFingerprint.Unavailable(FingerprintUnavailableReason.TransientPreview));
    }
}
