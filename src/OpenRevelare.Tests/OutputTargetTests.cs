using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The parameterized render terminal (D-021 / D-022). The load-bearing assertion in this file is
/// the first one: an SDR target must remain byte-for-byte what the application already produced,
/// because every existing project and every golden render depends on it.
/// </summary>
public sealed class OutputTargetTests
{
    [Theory]
    [InlineData("sRGB", "")]
    [InlineData("AdobeRGB", "")]
    [InlineData("Rec709", "")]
    [InlineData("sRGB", ":kodak-2383")]
    [InlineData("AdobeRGB", ":kodak-2383")]
    public void Sdr_target_is_bit_exact_with_the_established_terminal_on_both_versions(
        string outputSpace,
        string printLut)
    {
        foreach (ColorPipelineVersion version in
                 new[] { ColorPipelineVersion.LegacyV1, ColorPipelineVersion.ManagedV2 })
        {
            var cal = new FrameParams { OutputSpace = outputSpace, PrintLut = printLut };
            float[] viaTarget = SceneLinearFixture();
            float[] viaEstablished = SceneLinearFixture();
            using var cmm = new LittleCmsEngine();

            ColorPipeline.ToOutputTargetFor(
                viaTarget, cal, OutputTarget.Sdr(cal.ResolvedOutputSpace), version, cmm);
            ColorPipeline.ToOutputSpaceFor(viaEstablished, cal, version, cmm);

            AssertSameFloatBits(viaEstablished, viaTarget, $"{outputSpace}/{printLut}/{version}");
        }
    }

    /// <summary>
    /// The headline behaviour of D-021. The SDR terminal ends by forcing every channel into
    /// [0,1]; the extended terminal must still be carrying the bright end of the negative.
    /// </summary>
    [Fact]
    public void Extended_target_keeps_highlights_that_the_sdr_terminal_clips()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "" };
        float[] sdr = SceneLinearFixture();
        float[] hdr = SceneLinearFixture();
        using var cmm = new LittleCmsEngine();

        ColorPipeline.ToOutputTargetFor(
            sdr, cal, OutputTarget.Sdr(cal.ResolvedOutputSpace), ColorPipelineVersion.ManagedV2, cmm);
        ColorPipeline.ToOutputTargetFor(
            hdr,
            cal,
            OutputTarget.Hdr(peakNits: 1000f),
            ColorPipelineVersion.ManagedV2,
            cmm);

        Assert.All(sdr, value => Assert.InRange(value, 0f, 1f));
        Assert.Contains(hdr, value => value > 1f);

        // Step 4 deliberately leaves the extended result UNBOUNDED: the shoulder is a finishing
        // step, applied once exposure and white balance can no longer move a value. The headroom
        // promise is therefore a property of a finished render, asserted at Pipeline.Render below.
    }

    /// <summary>
    /// End to end: a roll that asks for an HDR peak renders past diffuse white, stays inside the
    /// headroom it asked for, and says so in its own encoding rather than claiming to be a
    /// display-referred picture.
    /// </summary>
    [Fact]
    public void Hdr_roll_renders_scene_referred_extended_pixels_inside_its_headroom()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        using var cmm = new LittleCmsEngine();

        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        float headroom = cal.ResolvedOutputTarget.HighlightHeadroom;
        Assert.Contains(frame.Pixels.Data, value => value > 1f);
        Assert.All(frame.Pixels.Data, value => Assert.True(
            value <= headroom, $"{value:R} exceeded the promised headroom {headroom:R}."));

        Assert.Equal(ColorReference.SceneReferred, frame.Encoding.Reference);
        Assert.Equal(TransferState.LinearInProfilePrimaries, frame.Encoding.Transfer);
        Assert.Equal(NumericRange.Extended, frame.Encoding.Range);
    }

    /// <summary>
    /// A roll with no HDR peak must render exactly as it always has — same pixels, same
    /// display-referred encoding. This is the D-022 gate at the level users actually meet.
    /// </summary>
    [Fact]
    public void A_roll_without_an_hdr_peak_renders_display_referred_as_before()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "" };
        using var cmm = new LittleCmsEngine();

        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        Assert.Equal(0d, cal.HdrPeakNits);
        Assert.False(cal.ResolvedOutputTarget.IsExtended);
        Assert.Equal(ColorReference.DisplayReferred, frame.Encoding.Reference);
        Assert.Equal(NumericRange.Normalized, frame.Encoding.Range);
        Assert.All(frame.Pixels.Data, value => Assert.InRange(value, 0f, 1f));
    }

    /// <summary>
    /// Levels, contrast, highlights/shadows, curves and saturation are DEFINED against a display
    /// range. An extended render refuses them rather than dropping them silently, which would
    /// hand the user a picture that is not the one they graded (D-021, and D-015's precedent).
    /// </summary>
    [Theory]
    [InlineData("Contrast")]
    [InlineData("Saturation")]
    [InlineData("Highlights")]
    public void Hdr_roll_refuses_display_referred_stage2_adjustments(string adjustment)
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        switch (adjustment)
        {
            case "Contrast": cal.Contrast = 0.25; break;
            case "Saturation": cal.Saturation = 0.3; break;
            case "Highlights": cal.Highlights = -0.4; break;
        }
        using var cmm = new LittleCmsEngine();

        Assert.Throws<NotSupportedException>(() =>
            Pipeline.Render(MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm));
    }

    /// <summary>
    /// White balance and exposure are multiplicative in linear light, so they survive the move to
    /// a scene-referred target unchanged — and, being multiplicative, they must land BEFORE the
    /// shoulder or they would push the result back past the headroom.
    /// </summary>
    [Fact]
    public void Hdr_roll_applies_exposure_and_still_honours_the_headroom()
    {
        var cal = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = "",
            HdrPeakNits = 1000d,
            ExposureEv = 2.0,
        };
        using var cmm = new LittleCmsEngine();

        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        float headroom = cal.ResolvedOutputTarget.HighlightHeadroom;
        Assert.All(frame.Pixels.Data, value => Assert.True(
            value <= headroom, $"exposure escaped the headroom: {value:R} > {headroom:R}."));
    }

    [Fact]
    public void An_unreachable_stored_peak_degrades_to_sdr_rather_than_failing_to_open()
    {
        // Same judgement as ResolvedOutputSpace: a stored value this build cannot honour must
        // fall back to the safe rendering, never stop the roll from loading.
        Assert.False(new FrameParams { HdrPeakNits = 100d }.ResolvedOutputTarget.IsExtended);
        Assert.False(new FrameParams { HdrPeakNits = 203d }.ResolvedOutputTarget.IsExtended);
        Assert.False(new FrameParams { HdrPeakNits = double.NaN }.ResolvedOutputTarget.IsExtended);
        Assert.True(new FrameParams { HdrPeakNits = 1000d }.ResolvedOutputTarget.IsExtended);
    }

    /// <summary>
    /// D-021: a print stock emulation is itself a display rendering that spends the highlights
    /// inside its own shoulder, so an extended target does not consult it. Selecting a film look
    /// and asking for HDR must therefore produce the analytic extended rendering, not a brighter
    /// print.
    /// </summary>
    [Fact]
    public void Extended_target_does_not_consult_the_print_lut()
    {
        OutputTarget target = OutputTarget.Hdr(peakNits: 1000f);
        float[] withLut = SceneLinearFixture();
        float[] withoutLut = SceneLinearFixture();
        using var cmm = new LittleCmsEngine();

        ColorPipeline.ToOutputTargetFor(
            withLut,
            new FrameParams { OutputSpace = "sRGB", PrintLut = ":kodak-2383" },
            target,
            ColorPipelineVersion.ManagedV2,
            cmm);
        ColorPipeline.ToOutputTargetFor(
            withoutLut,
            new FrameParams { OutputSpace = "sRGB", PrintLut = "" },
            target,
            ColorPipelineVersion.ManagedV2,
            cmm);

        AssertSameFloatBits(withoutLut, withLut, "extended target must ignore the print LUT");
    }

    [Fact]
    public void Extended_target_leaves_diffuse_white_and_below_alone()
    {
        // 1.0 is diffuse white by construction, so the shoulder must not touch it or anything
        // under it: the HDR render and its SDR sibling are the same photograph below paper white.
        float[] data = [0f, 0.18f, 0.5f, 1f, -0.2f, 0.999f];
        float[] expected = (float[])data.Clone();

        HighlightRolloff.Apply(data, headroom: 5f);

        AssertSameFloatBits(expected, data, "identity at and below diffuse white");
    }

    [Fact]
    public void Rolloff_is_monotonic_and_never_reaches_past_the_headroom()
    {
        const float headroom = 4f;
        float[] data = [1.0001f, 1.5f, 2f, 4f, 8f, 64f, 4096f, float.PositiveInfinity];

        HighlightRolloff.Apply(data, headroom);

        for (int i = 1; i < data.Length; i++)
            Assert.True(data[i] > data[i - 1], $"index {i} broke monotonicity: {data[i - 1]} -> {data[i]}");
        Assert.All(data, value => Assert.InRange(value, 1f, headroom));
        Assert.Equal(headroom, data[^1]);
    }

    /// <summary>
    /// The join at diffuse white must be C¹, not merely continuous. A shoulder that met the
    /// identity in value but not in slope would crease exactly where film highlights live, which
    /// is the part of the picture this whole feature exists to show.
    /// </summary>
    [Fact]
    public void Rolloff_meets_the_identity_with_matching_slope_at_the_knee()
    {
        const float headroom = 5f;
        const float epsilon = 1e-3f;
        float[] data = [1f + epsilon];

        HighlightRolloff.Apply(data, headroom);

        // A C0-only join would depart from the identity linearly in epsilon; a C1 join departs
        // quadratically. 1e-6 sits between the two by three orders of magnitude.
        Assert.True(
            Math.Abs(data[0] - (1f + epsilon)) < 1e-6f,
            $"slope mismatch at the knee: {data[0]:R} against {1f + epsilon:R}");
    }

    [Fact]
    public void Rolloff_passes_non_finite_and_negative_values_through_untouched()
    {
        float[] data = [float.NaN, float.NegativeInfinity, -3f, 0f];

        HighlightRolloff.Apply(data, headroom: 2f);

        Assert.True(float.IsNaN(data[0]));
        Assert.True(float.IsNegativeInfinity(data[1]));
        Assert.Equal(-3f, data[2]);
        Assert.Equal(0f, data[3]);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    [InlineData(float.NaN)]
    public void Rolloff_rejects_a_target_without_headroom(float headroom) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HighlightRolloff.Apply([2f], headroom));

    [Theory]
    [InlineData(203f)]
    [InlineData(100f)]
    public void Hdr_target_rejects_a_peak_that_is_not_above_diffuse_white(float peakNits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputTarget.Hdr(peakNits));

    [Fact]
    public void Sdr_target_has_exactly_no_headroom()
    {
        OutputTarget sdr = OutputTarget.Sdr(ColorSpaces.Srgb);

        Assert.False(sdr.IsExtended);
        Assert.Equal(1f, sdr.HighlightHeadroom);
    }

    /// <summary>
    /// <see cref="GamutMapping.PreserveExtended"/> must rotate primaries and stop there. If it
    /// clamped like its siblings, the extended terminal would lose its highlights before the
    /// shoulder ever ran.
    /// </summary>
    [Fact]
    public void Preserve_extended_mapping_rotates_primaries_without_clamping()
    {
        float[] clamped = [4f, 0.5f, -0.25f];
        float[] preserved = [4f, 0.5f, -0.25f];

        OutputRender.Convert(clamped, ColorSpaces.AcesCg, ColorSpaces.Srgb, GamutMapping.Clip);
        OutputRender.Convert(preserved, ColorSpaces.AcesCg, ColorSpaces.Srgb, GamutMapping.PreserveExtended);

        Assert.All(clamped, value => Assert.InRange(value, 0f, 1f));
        Assert.Contains(preserved, value => value > 1f);
        Assert.Contains(preserved, value => value < 0f);
    }

    /// <summary>
    /// Interleaved scene-linear ACEScg RGB spanning the shadows, the mid grey, diffuse white and
    /// several stops above it — the range a negative actually holds and the SDR terminal discards.
    /// </summary>
    private static float[] SceneLinearFixture() =>
    [
        0.001f, 0.0012f, 0.0009f,
        0.18f, 0.17f, 0.19f,
        0.5f, 0.45f, 0.55f,
        1.0f, 0.98f, 1.02f,
        2.4f, 2.1f, 1.9f,
        6.0f, 5.2f, 4.4f,
        18.0f, 16.5f, 14.0f,
    ];

    /// <summary>
    /// The preview boundary must carry an extended render through untouched. Routing it via the
    /// CMM's D50 PCS would be entitled to discard exactly the negatives and above-one values the
    /// render exists to hold, so the conversion has to be the identity — and provably so: the
    /// engine handed in here fails the test if it is consulted at all.
    /// </summary>
    [Fact]
    public void Canonical_preview_carries_an_extended_render_through_without_the_cmm()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        using var renderCmm = new LittleCmsEngine();
        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, renderCmm);

        PresentationScene scene = CanonicalPreviewConverter.Convert(
            frame, new UnusableColorManagement(), referenceWhiteScale: 1f);

        ReadOnlySpan<Half> rgba = scene.LinearExtendedSrgbRgba.AsSpan();
        float[] rendered = frame.Pixels.Data;
        for (int pixel = 0; pixel < rendered.Length / 3; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.Equal(
                    (float)(Half)rendered[(pixel * 3) + channel],
                    (float)rgba[(pixel * 4) + channel]);
            }
            Assert.Equal(1f, (float)rgba[(pixel * 4) + 3]);
        }

        Assert.Contains(rendered, value => value > 1f);
    }

    /// <summary>
    /// (b)'s master format works today, by construction rather than by new code: an extended
    /// frame routes to the float32 TIFF exporter (D-016), so the highlights above diffuse white
    /// reach the file intact and carry the carrier's exact profile with them.
    /// </summary>
    [Fact]
    public void Hdr_render_exports_as_float32_tiff_with_its_highlights_intact()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        using var cmm = new LittleCmsEngine();
        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);
        Assert.Contains(frame.Pixels.Data, value => value > 1f);

        string path = Path.Combine(
            Path.GetTempPath(), $"openrevelare-hdr-master-{Guid.NewGuid():N}.tif");
        try
        {
            TiffIO.ExportTiff(frame, path);
            WorkingFrame reloaded = TiffIO.LoadWorkingFrame(path, inputIsSrgb: false);

            Assert.Contains(reloaded.Pixels.Data, value => value > 1f);
            Assert.Equal(frame.Pixels.Data.Length, reloaded.Pixels.Data.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An engine that fails the test if anything asks it to do colour management.</summary>
    private sealed class UnusableColorManagement : IColorManagementEngine
    {
        public CmmBuildIdentity Build => throw Unexpected();
        public ProfileValidationResult Validate(ColorProfileRef profile) => throw Unexpected();
        public IColorTransformLease Lease(ColorTransformRequest request) => throw Unexpected();
        public CmmDiagnosticsSnapshot GetDiagnostics() => throw Unexpected();
        public void Dispose() { }

        private static InvalidOperationException Unexpected() =>
            new("The canonical carrier needs no colour management; converting it must be the identity.");
    }

    /// <summary>
    /// The peak has to survive a save/load round trip and a clone, or the roll silently reverts
    /// to SDR the next time it is opened. Absent means SDR, so pre-HDR projects are untouched.
    /// </summary>
    [Fact]
    public void Hdr_peak_survives_the_project_round_trip_and_a_clone()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", HdrPeakNits = 1000d };

        Assert.Equal(1000d, cal.Clone().HdrPeakNits);
        Assert.Equal(0d, new FrameParams().HdrPeakNits);

        string path = Path.Combine(
            Path.GetTempPath(), $"openrevelare-hdr-peak-{Guid.NewGuid():N}.orv.json");
        try
        {
            var saved = new Project.Data();
            saved.Frames.Add(new Project.Frame { SourcePath = "frame.tif", Params = cal });
            Project.Save(path, saved);

            Project.Data loaded = Project.Load(path);

            Assert.Equal(1000d, loaded.Frames[0].Params.HdrPeakNits);
            Assert.True(loaded.Frames[0].Params.ResolvedOutputTarget.IsExtended);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static WorkingFrame MakeWorkingFrame()
    {
        var pixels = new ImageBuffer(4, 2, [
            0.81f,  0.52f,   0.29f,
            0.63f,  0.31f,   0.12f,
            0.42f,  0.17f,   0.052f,
            0.24f,  0.075f,  0.015f,
            0.13f,  0.035f,  0.0035f,
            0.055f, 0.010f,  0.0008f,
            1.15f,  1.05f,   0.95f,
            0.008f, 0.0015f, 0.00012f,
        ])
        {
            SourceQuantisationStep = 1.0 / 65535.0,
        };
        var originalEncoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:output-target-negative-v1",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:output-target-negative-v1",
            "output-target synthetic negative",
            originalEncoding,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }

    private static void AssertSameFloatBits(float[] expected, float[] actual, string because)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]))
                continue;
            Assert.Fail($"{because}: component {i} differs, {expected[i]:R} against {actual[i]:R}.");
        }
    }
}
