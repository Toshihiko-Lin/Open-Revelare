using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ManagedRegionRenderTests
{
    private static readonly RegionRender.Roi FullRoi = new(0.0, 0.0, 1.0, 1.0);

    [Fact]
    public void Managed_v2_full_region_matches_full_pipeline_with_print_LUT_and_curve()
    {
        WorkingFrame source = MakeWorkingFrame(MakeNegative(14, 11));
        FrameParams parameters = ManagedParameters();
        using var engine = new LittleCmsEngine();

        RenderedFrame full = Pipeline.Render(
            source,
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);
        var patch = RegionRender.Render(
            source,
            parameters,
            FullRoi,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(FullRoi, patch.Realised);
        Assert.Same(patch.Frame.Pixels, patch.Image);
        Assert.Equal(full.Pixels.Width, patch.Frame.Pixels.Width);
        Assert.Equal(full.Pixels.Height, patch.Frame.Pixels.Height);
        AssertPixelsClose(full.Pixels.Data, patch.Frame.Pixels.Data, 2e-6f, "full managed region");
        AssertSameRenderSemantics(full, patch.Frame);
    }

    [Fact]
    public void Managed_v2_slice_matches_full_region_and_corresponding_pipeline_pixels()
    {
        WorkingFrame source = MakeWorkingFrame(MakeNegative(18, 13));
        FrameParams parameters = ManagedParameters();
        var requested = new RegionRender.Roi(0.22, 0.18, 0.44, 0.51);
        using var engine = new LittleCmsEngine();

        RenderedFrame full = Pipeline.Render(
            source,
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);
        var fromFull = RegionRender.Render(
            source,
            parameters,
            requested,
            ColorPipelineVersion.ManagedV2,
            engine);

        var bounds = RegionRender.RequiredSourceBounds(
            source.Pixels.Width,
            source.Pixels.Height,
            parameters,
            requested);
        ImageBuffer slice = Slice(source.Pixels, bounds);
        var fromSlice = RegionRender.RenderFromSlice(
            source.WithPixels(slice),
            bounds.X0,
            bounds.Y0,
            source.Pixels.Width,
            source.Pixels.Height,
            parameters,
            requested,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(fromFull.Realised, fromSlice.Realised);
        AssertFloatBitsEqual(
            fromFull.Frame.Pixels.Data,
            fromSlice.Frame.Pixels.Data,
            "managed slice/full");
        AssertSameRenderSemantics(full, fromFull.Frame, compareFingerprint: false);
        AssertSameRenderSemantics(full, fromSlice.Frame, compareFingerprint: false);
        Assert.NotEqual(full.Fingerprint, fromFull.Frame.Fingerprint);
        Assert.Equal(fromFull.Frame.Fingerprint, fromSlice.Frame.Fingerprint);

        ImageBuffer expectedCrop = CropRealised(full.Pixels, fromFull.Realised);
        Assert.Equal(expectedCrop.Width, fromFull.Frame.Pixels.Width);
        Assert.Equal(expectedCrop.Height, fromFull.Frame.Pixels.Height);
        AssertPixelsClose(
            expectedCrop.Data,
            fromFull.Frame.Pixels.Data,
            2e-6f,
            "managed region/full pipeline");
    }

    [Fact]
    public void Legacy_v1_full_and_slice_overloads_bit_delegate_without_using_the_CMM()
    {
        WorkingFrame source = MakeWorkingFrame(MakeNegative(16, 12));
        FrameParams parameters = ManagedParameters();
        var requested = new RegionRender.Roi(0.17, 0.25, 0.53, 0.42);
        using var neverCalled = new NeverCalledEngine();
        RenderedFrame canonical = Pipeline.Render(source, parameters);

        var frozenFull = RegionRender.Render(source.Pixels, parameters, requested);
        var versionedFull = RegionRender.Render(
            source,
            parameters,
            requested,
            ColorPipelineVersion.LegacyV1,
            neverCalled);
        Assert.Equal(frozenFull.Realised, versionedFull.Realised);
        AssertFloatBitsEqual(
            frozenFull.Image.Data,
            versionedFull.Frame.Pixels.Data,
            "legacy full delegate");
        AssertSameRenderSemantics(canonical, versionedFull.Frame);
        Assert.True(versionedFull.Frame.Recipe.PixelProfileMismatch);
        Assert.Equal(
            BuiltInColorProfiles.LegacyPrintLutOutput(parameters.ResolvedOutputSpace).Identity,
            versionedFull.Frame.OutputProfile.Identity);
        Assert.NotEqual(
            versionedFull.Frame.Recipe.RequestedOutputProfile.Identity,
            versionedFull.Frame.OutputProfile.Identity);

        var bounds = RegionRender.RequiredSourceBounds(
            source.Pixels.Width,
            source.Pixels.Height,
            parameters,
            requested);
        ImageBuffer slice = Slice(source.Pixels, bounds);
        var frozenSlice = RegionRender.RenderFromSlice(
            slice,
            bounds.X0,
            bounds.Y0,
            source.Pixels.Width,
            source.Pixels.Height,
            parameters,
            requested);
        var versionedSlice = RegionRender.RenderFromSlice(
            source.WithPixels(slice),
            bounds.X0,
            bounds.Y0,
            source.Pixels.Width,
            source.Pixels.Height,
            parameters,
            requested,
            ColorPipelineVersion.LegacyV1,
            neverCalled);
        Assert.Equal(frozenSlice.Realised, versionedSlice.Realised);
        AssertFloatBitsEqual(
            frozenSlice.Image.Data,
            versionedSlice.Frame.Pixels.Data,
            "legacy slice delegate");
        AssertSameRenderSemantics(canonical, versionedSlice.Frame);
        Assert.Equal(0, neverCalled.LeaseCalls);
    }

    /// <summary>
    /// An HDR roll's sharp patch is composited over an extended preview and described as the
    /// linear extended carrier, so it must be RENDERED to that carrier: the same pixels the whole
    /// frame produces, highlights above diffuse white intact. Taking the SDR exit here used to
    /// hand the presentation sRGB-encoded numbers under an extended label — the picture changed
    /// colour and lost its HDR the moment the user zoomed in.
    /// </summary>
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 0.0)]
    [InlineData(false, 9.0)]
    [InlineData(true, 9.0)]
    public void Hdr_roll_sharp_patch_matches_the_extended_full_frame(bool sprocket, double rotation)
    {
        // The gradient fixture never gets thin enough to clear diffuse white; a few very thin
        // (dark) negative pixels inside the requested region give the extended target a real
        // highlight to carry.
        ImageBuffer negative = MakeNegative(18, 13);
        foreach (int x in new[] { 10, 11 })
        {
            int offset = (8 * negative.Width + x) * 3;
            negative.Data[offset] = 0.008f;
            negative.Data[offset + 1] = 0.0015f;
            negative.Data[offset + 2] = 0.00012f;
        }
        WorkingFrame source = MakeWorkingFrame(negative);
        var parameters = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = "",
            HdrPeakNits = 1000d,
            ExposureEv = 0.7,
            SprocketEnabled = sprocket,
            SprocketThreshold = sprocket ? 0.5 : null,
            Rotation = rotation,
        };
        var requested = new RegionRender.Roi(0.3, 0.25, 0.7, 0.75);
        using var engine = new LittleCmsEngine();

        RenderedFrame full = Pipeline.Render(
            source,
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);
        RenderedRegion patch = RegionRender.Render(
            source,
            parameters,
            requested,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.True(full.Encoding.Range == NumericRange.Extended);
        Assert.Contains(full.Pixels.Data, value => value > 1f);
        ImageBuffer expected = CropRealised(full.Pixels, patch.Realised);
        Assert.Equal(expected.Width, patch.Image.Width);
        Assert.Equal(expected.Height, patch.Image.Height);
        AssertPixelsClose(expected.Data, patch.Image.Data, 2e-5f, "hdr sharp patch");
        Assert.Contains(patch.Image.Data, value => value > 1f);
        AssertSameRenderSemantics(full, patch.Frame, compareFingerprint: false);

        if (sprocket || rotation != 0.0)
        {
            // Fill is pinned to diffuse white in the patch exactly where it is in the full frame.
            int pinned = 0;
            for (int p = 0; p < expected.PixelCount; p++)
            {
                if (expected.Data[p * 3] != 1f || expected.Data[p * 3 + 1] != 1f) continue;
                pinned++;
                for (int c = 0; c < 3; c++) Assert.Equal(1f, patch.Image.Data[p * 3 + c]);
            }
            Assert.True(pinned > 0, "the fixture must put some fill inside the patch");
        }
    }

    [Fact]
    public void Legacy_v1_false_region_carries_the_ACEScg_selected_TRC_compatibility_profile()
    {
        WorkingFrame source = MakeWorkingFrame(MakeNegative(12, 10));
        FrameParams parameters = ManagedParameters();
        parameters.DisplayReferredStage2 = false;
        RenderedFrame canonical = Pipeline.Render(source, parameters);

        RenderedRegion region = RegionRender.Render(
            source,
            parameters,
            FullRoi,
            ColorPipelineVersion.LegacyV1,
            colorManagement: null);

        AssertSameRenderSemantics(canonical, region.Frame);
        ColorProfileRef compatibility = BuiltInColorProfiles.LegacyLinearStage2Output(
            parameters.ResolvedOutputSpace);
        Assert.Equal(compatibility.Identity, region.Frame.OutputProfile.Identity);
        Assert.Equal(compatibility.IccBytes.ToArray(), region.Frame.OutputProfile.IccBytes.ToArray());
        Assert.NotEqual(
            region.Frame.Recipe.RequestedOutputProfile.Identity,
            region.Frame.OutputProfile.Identity);
        Assert.Equal(string.Empty, region.Frame.Recipe.PrintLutIdentity);
        Assert.True(region.Frame.Recipe.PixelProfileMismatch);
        Assert.Contains("print LUT ignored", region.Frame.Recipe.GamutPolicy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ColorPipelineVersion.LegacyV1)]
    [InlineData(ColorPipelineVersion.ManagedV2)]
    public void Versioned_negative_view_is_typed_transient_and_needs_no_CMM(
        ColorPipelineVersion pipelineVersion)
    {
        WorkingFrame source = MakeWorkingFrame(MakeNegative(13, 9));
        FrameParams parameters = ManagedParameters();
        var requested = new RegionRender.Roi(0.1, 0.15, 0.7, 0.65);
        double[] negativeWhiteBalance = { 1.12, 1.0, 0.91 };

        var frozen = RegionRender.Render(
            source.Pixels,
            parameters,
            requested,
            negative: true,
            negativeWb: negativeWhiteBalance);
        RenderedRegion typed = RegionRender.Render(
            source,
            parameters,
            requested,
            pipelineVersion,
            colorManagement: null,
            negative: true,
            negativeWb: negativeWhiteBalance);

        Assert.Equal(frozen.Realised, typed.Realised);
        AssertFloatBitsEqual(
            frozen.Image.Data,
            typed.Frame.Pixels.Data,
            "versioned negative delegate");
        ColorProfileRef exactOutput = BuiltInColorProfiles.For(
            parameters.ResolvedOutputSpace,
            ProfileRole.Output);
        Assert.Equal(exactOutput.Identity, typed.Frame.OutputProfile.Identity);
        Assert.Equal(exactOutput.IccBytes.ToArray(), typed.Frame.OutputProfile.IccBytes.ToArray());
        Assert.Equal(ProfileRole.Output, typed.Frame.OutputProfile.Role);
        Assert.Same(typed.Frame.OutputProfile, typed.Frame.Recipe.RequestedOutputProfile);
        Assert.Equal(pipelineVersion, typed.Frame.Recipe.PipelineVersion);
        Assert.False(typed.Frame.Recipe.PixelProfileMismatch);
        Assert.Equal(string.Empty, typed.Frame.Recipe.PrintLutIdentity);
        Assert.Contains("negative viewer", typed.Frame.Recipe.GamutPolicy, StringComparison.Ordinal);
        Assert.Equal(ColorReference.DisplayReferred, typed.Frame.Encoding.Reference);
        Assert.Equal(TransferState.ProfileEncoded, typed.Frame.Encoding.Transfer);
        Assert.Equal(NumericRange.Normalized, typed.Frame.Encoding.Range);
        Assert.Equal(
            new RenderFingerprint.Unavailable(FingerprintUnavailableReason.TransientPreview),
            typed.Frame.Fingerprint);
    }

    private static FrameParams ManagedParameters() => new()
    {
        OutputSpace = "DisplayP3",
        PrintLut = ":kodak-2383",
        DisplayReferredStage2 = true,
        ExposureEv = 0.1,
        Contrast = 0.08,
        Saturation = 0.06,
        CurveHasEndpoints = true,
        CurvePreserveHue = false,
        CurvePointsM = new List<(double, double)>
        {
            (0.0, 0.02),
            (0.45, 0.36),
            (1.0, 0.98),
        },
    };

    private static WorkingFrame MakeWorkingFrame(ImageBuffer pixels)
    {
        var original = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:managed-region",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var descriptor = new SourceDescriptor(
            "test:managed-region",
            "Managed region synthetic negative",
            original,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            descriptor);
    }

    private static ImageBuffer MakeNegative(int width, int height)
    {
        var image = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 3;
                image.Data[offset] = 0.16f + 0.66f * x / Math.Max(width - 1, 1);
                image.Data[offset + 1] = 0.09f + 0.48f * y / Math.Max(height - 1, 1);
                image.Data[offset + 2] = 0.025f + 0.27f * (x + y) / Math.Max(width + height - 2, 1);
            }
        }
        image.SourceQuantisationStep = 1.0 / 65535.0;
        return image;
    }

    private static ImageBuffer Slice(
        ImageBuffer source,
        (int X0, int Y0, int X1, int Y1) bounds)
    {
        int width = bounds.X1 - bounds.X0;
        int height = bounds.Y1 - bounds.Y0;
        var slice = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            Array.Copy(
                source.Data,
                ((bounds.Y0 + y) * source.Width + bounds.X0) * 3,
                slice.Data,
                y * width * 3,
                width * 3);
        }
        return slice.InheritSourceFrom(source);
    }

    private static ImageBuffer CropRealised(ImageBuffer source, RegionRender.Roi realised)
    {
        int x0 = (int)Math.Round(realised.X * source.Width);
        int y0 = (int)Math.Round(realised.Y * source.Height);
        int width = (int)Math.Round(realised.W * source.Width);
        int height = (int)Math.Round(realised.H * source.Height);
        var crop = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            Array.Copy(
                source.Data,
                ((y0 + y) * source.Width + x0) * 3,
                crop.Data,
                y * width * 3,
                width * 3);
        }
        return crop;
    }

    private static void AssertPixelsClose(
        float[] expected,
        float[] actual,
        float tolerance,
        string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            float difference = Math.Abs(expected[index] - actual[index]);
            Assert.True(
                difference <= tolerance,
                $"{label} sample {index}: expected={expected[index]:R}, actual={actual[index]:R}, " +
                $"difference={difference:R}");
        }
    }

    private static void AssertFloatBitsEqual(float[] expected, float[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            int expectedBits = BitConverter.SingleToInt32Bits(expected[index]);
            int actualBits = BitConverter.SingleToInt32Bits(actual[index]);
            Assert.True(
                expectedBits == actualBits,
                $"{label} sample {index}: expected=0x{expectedBits:X8}, actual=0x{actualBits:X8}");
        }
    }

    private static void AssertSameRenderSemantics(
        RenderedFrame expected,
        RenderedFrame actual,
        bool compareFingerprint = true)
    {
        Assert.Equal(expected.Encoding, actual.Encoding);
        Assert.Equal(expected.Recipe, actual.Recipe);
        if (compareFingerprint) Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.OutputProfile.Identity, actual.OutputProfile.Identity);
        Assert.Equal(expected.OutputProfile.IccBytes.ToArray(), actual.OutputProfile.IccBytes.ToArray());
        Assert.Equal(expected.OutputProfile.Role, actual.OutputProfile.Role);
    }

    private sealed class NeverCalledEngine : IColorManagementEngine
    {
        public int LeaseCalls { get; private set; }

        public CmmBuildIdentity Build { get; } = new(
            "never-called test CMM",
            "test",
            0,
            "test",
            "test",
            CmmTransformFlags.None,
            new string('0', 64),
            new string('0', 64),
            "{}",
            "test");

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("the CMM must not be used");

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LeaseCalls++;
            throw new InvalidOperationException("the CMM must not be used");
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("the CMM must not be used");

        public void Dispose() { }
    }
}
