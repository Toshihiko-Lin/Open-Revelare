using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class RenderFingerprintTests
{
    [Fact]
    public void Managed_full_render_computes_a_stable_versioned_fingerprint()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "DisplayP3",
            PrintLut = string.Empty,
            ExposureEv = 0.25,
        };
        using var engine = new MustRemainUnusedEngine();

        RenderedFrame first = Pipeline.Render(
            WorkingFrameFor(SyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);
        RenderedFrame second = Pipeline.Render(
            WorkingFrameFor(SyntheticNegative()),
            parameters.Clone(),
            ColorPipelineVersion.ManagedV2,
            engine);

        var firstHash = Assert.IsType<RenderFingerprint.Computed>(first.Fingerprint);
        var secondHash = Assert.IsType<RenderFingerprint.Computed>(second.Fingerprint);
        Assert.Equal(firstHash.Sha256Hex, secondHash.Sha256Hex);
        Assert.Equal(64, firstHash.Sha256Hex.Length);
    }

    [Fact]
    public void Fingerprint_changes_with_pixels_dimensions_profile_or_recipe()
    {
        RenderedFrame baseline = Frame(
            new ImageBuffer(2, 1, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f }),
            BuiltInColorProfiles.Srgb(ProfileRole.Output),
            "baseline");
        string expected = Assert.IsType<RenderFingerprint.Computed>(baseline.Fingerprint).Sha256Hex;

        RenderedFrame changedPixels = baseline.WithPixels(
            new ImageBuffer(2, 1, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6001f }));
        RenderedFrame changedDimensions = baseline.WithPixels(
            new ImageBuffer(1, 2, (float[])baseline.Pixels.Data.Clone()));
        RenderedFrame changedProfile = Frame(
            new ImageBuffer(2, 1, (float[])baseline.Pixels.Data.Clone()),
            BuiltInColorProfiles.DisplayP3(ProfileRole.Output),
            "baseline");
        RenderedFrame changedRecipe = Frame(
            new ImageBuffer(2, 1, (float[])baseline.Pixels.Data.Clone()),
            BuiltInColorProfiles.Srgb(ProfileRole.Output),
            "different gamut policy");

        string RecipeHash(
            ColorProfileRef requested,
            RenderingIntent intent = RenderingIntent.RelativeColorimetric,
            bool blackPointCompensation = false,
            string printLutIdentity = "",
            bool pixelProfileMismatch = false)
        {
            var recipe = new OutputRecipe(
                ColorPipelineVersion.ManagedV2,
                requested,
                intent,
                blackPointCompensation,
                "baseline",
                printLutIdentity,
                pixelProfileMismatch);
            return RenderFingerprint.ComputeManaged(
                baseline.Pixels,
                baseline.Encoding,
                recipe).Sha256Hex;
        }

        Assert.NotEqual(expected, Assert.IsType<RenderFingerprint.Computed>(changedPixels.Fingerprint).Sha256Hex);
        Assert.NotEqual(expected, Assert.IsType<RenderFingerprint.Computed>(changedDimensions.Fingerprint).Sha256Hex);
        Assert.NotEqual(expected, Assert.IsType<RenderFingerprint.Computed>(changedProfile.Fingerprint).Sha256Hex);
        Assert.NotEqual(expected, Assert.IsType<RenderFingerprint.Computed>(changedRecipe.Fingerprint).Sha256Hex);
        Assert.NotEqual(expected, RecipeHash(BuiltInColorProfiles.DisplayP3(ProfileRole.Output)));
        Assert.NotEqual(expected, RecipeHash(
            baseline.OutputProfile,
            intent: RenderingIntent.Perceptual));
        Assert.NotEqual(expected, RecipeHash(
            baseline.OutputProfile,
            blackPointCompensation: true));
        Assert.NotEqual(expected, RecipeHash(
            baseline.OutputProfile,
            printLutIdentity: ":kodak-2383"));
        Assert.NotEqual(expected, RecipeHash(
            baseline.OutputProfile,
            pixelProfileMismatch: true));
    }

    [Fact]
    public void Fingerprint_excludes_machine_specific_external_lut_paths_and_keeps_builtin_ids()
    {
        string firstMachinePath = FingerprintForPrintLut(
            @"C:\Users\one\Looks\stock.cube");
        string secondMachinePath = FingerprintForPrintLut(
            "/Users/two/Looks/stock.cube");

        Assert.Equal(firstMachinePath, secondMachinePath);
        Assert.Equal(
            FingerprintForPrintLut(":kodak-2383"),
            FingerprintForPrintLut(":KODAK-2383"));
        Assert.NotEqual(
            FingerprintForPrintLut(":kodak-2383"),
            FingerprintForPrintLut(":fujifilm-3513di"));
        Assert.NotEqual(firstMachinePath, FingerprintForPrintLut(string.Empty));
    }

    [Fact]
    public void Legacy_render_remains_explicitly_unfingerprinted()
    {
        RenderedFrame legacy = Pipeline.Render(WorkingFrameFor(SyntheticNegative()), new FrameParams());

        var unavailable = Assert.IsType<RenderFingerprint.Unavailable>(legacy.Fingerprint);
        Assert.Equal(FingerprintUnavailableReason.LegacyPipelineHasNoVersionedRecipe, unavailable.Reason);
    }

    [Fact]
    public void A_managed_render_does_not_hash_its_pixels_until_the_fingerprint_is_read()
    {
        // Hashing every sample costs ~9 ms for a preview and ~130 ms for a 24 MP export frame,
        // and the only consumer is the diagnostics panel. Mutating the buffer after the render and
        // seeing the change in the fingerprint is direct evidence the work was not done up front —
        // an eager hash would still describe the pre-mutation pixels.
        var pixels = new ImageBuffer(2, 1, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f });
        RenderedFrame rendered = Pipeline.Render(
            WorkingFrameFor(pixels),
            ManagedParams(),
            ColorPipelineVersion.ManagedV2,
            new LittleCmsEngine());

        Assert.True(rendered.HasComputedFingerprint);
        rendered.Pixels.Data[0] = 0.9f;

        var computed = Assert.IsType<RenderFingerprint.Computed>(rendered.Fingerprint);
        Assert.Equal(
            RenderFingerprint.ComputeManaged(rendered.Pixels, rendered.Encoding, rendered.Recipe)
                .Sha256Hex,
            computed.Sha256Hex);
    }

    [Fact]
    public void The_fingerprint_is_computed_once_and_then_stable()
    {
        var pixels = new ImageBuffer(1, 1, new[] { 0.1f, 0.2f, 0.3f });
        RenderedFrame rendered = Pipeline.Render(
            WorkingFrameFor(pixels),
            ManagedParams(),
            ColorPipelineVersion.ManagedV2,
            new LittleCmsEngine());

        string first = Assert.IsType<RenderFingerprint.Computed>(rendered.Fingerprint).Sha256Hex;
        rendered.Pixels.Data[0] = 0.9f;

        // Already materialized, so a later mutation cannot retroactively change what was reported.
        Assert.Equal(first, Assert.IsType<RenderFingerprint.Computed>(rendered.Fingerprint).Sha256Hex);
    }

    [Fact]
    public void WithPixels_fingerprints_the_new_buffer_without_hashing_the_old_one()
    {
        var pixels = new ImageBuffer(2, 1, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f });
        RenderedFrame rendered = Pipeline.Render(
            WorkingFrameFor(pixels),
            ManagedParams(),
            ColorPipelineVersion.ManagedV2,
            new LittleCmsEngine());

        var replacement = new ImageBuffer(1, 1, new[] { 0.7f, 0.8f, 0.9f });
        RenderedFrame downsampled = rendered.WithPixels(replacement);

        Assert.True(downsampled.HasComputedFingerprint);
        Assert.Equal(
            RenderFingerprint.ComputeManaged(replacement, rendered.Encoding, rendered.Recipe)
                .Sha256Hex,
            Assert.IsType<RenderFingerprint.Computed>(downsampled.Fingerprint).Sha256Hex);
    }

    [Fact]
    public void A_legacy_render_still_reports_no_fingerprint_through_WithPixels()
    {
        var pixels = new ImageBuffer(2, 1, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f });
        RenderedFrame legacy = Pipeline.Render(WorkingFrameFor(pixels), ManagedParams());

        Assert.False(legacy.HasComputedFingerprint);
        RenderedFrame downsampled = legacy.WithPixels(new ImageBuffer(1, 1, new[] { 0.7f, 0.8f, 0.9f }));

        Assert.IsType<RenderFingerprint.Unavailable>(downsampled.Fingerprint);
        Assert.False(downsampled.HasComputedFingerprint);
    }

    private static FrameParams ManagedParams() => new()
    {
        OutputSpace = "sRGB",
        DisplayReferredStage2 = true,
    };

    private static RenderedFrame Frame(
        ImageBuffer pixels,
        ColorProfileRef profile,
        string gamutPolicy)
    {
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
            gamutPolicy,
            printLutIdentity: string.Empty,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static string FingerprintForPrintLut(string printLutIdentity)
    {
        ImageBuffer pixels = new(1, 1, new[] { 0.1f, 0.2f, 0.3f });
        ColorProfileRef profile = BuiltInColorProfiles.Srgb(ProfileRole.Output);
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
            "stable print-LUT identity test",
            printLutIdentity,
            pixelProfileMismatch: false);
        return RenderFingerprint.ComputeManaged(pixels, encoding, recipe).Sha256Hex;
    }

    private static WorkingFrame WorkingFrameFor(ImageBuffer pixels)
    {
        var original = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:fingerprint",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:fingerprint",
            "fingerprint fixture",
            original,
            "test-generated float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }

    private static ImageBuffer SyntheticNegative() => new(2, 1, new[]
    {
        0.81f, 0.52f, 0.29f,
        0.24f, 0.075f, 0.015f,
    });

    private sealed class MustRemainUnusedEngine : IColorManagementEngine
    {
        public CmmBuildIdentity Build { get; } = new(
            "test", "1", 1, "1", "none", CmmTransformFlags.None,
            new string('0', 64), new string('0', 64), "none", "none");

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("The no-LUT managed render must not validate a CMM profile.");

        public IColorTransformLease Lease(ColorTransformRequest request) =>
            throw new InvalidOperationException("The no-LUT managed render must not lease a CMM transform.");

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("Diagnostics are not used by this test.");

        public void Dispose() { }
    }
}
