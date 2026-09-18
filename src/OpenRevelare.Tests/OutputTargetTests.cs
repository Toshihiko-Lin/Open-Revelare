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
    /// The sprocket/light-board mask is fill, not picture: it is written as the top of the Cineon
    /// domain, which the SDR shoulder squeezes to paper white but the extended shoulder would carry
    /// to the peak. On an extended target it must sit at the carrier's diffuse white — exactly 1.0,
    /// unmoved by exposure — while every other pixel renders as if the mask were not there.
    /// </summary>
    [Fact]
    public void Sprocket_mask_is_diffuse_white_not_a_highlight_on_an_extended_target()
    {
        // The fixture's seventh pixel is the only one whose luma clears the default threshold —
        // bright on the NEGATIVE, so it is a shadow in the picture and paper white under the mask.
        const int masked = 6;
        var withMask = new FrameParams
        {
            OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d,
            SprocketEnabled = true, ExposureEv = 1.0,
        };
        FrameParams withoutMask = withMask.Clone();
        withoutMask.SprocketEnabled = false;
        using var cmm = new LittleCmsEngine();

        float[] masked_ = Pipeline.Render(
            MakeWorkingFrame(), withMask, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;
        float[] unmasked = Pipeline.Render(
            MakeWorkingFrame(), withoutMask, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;

        Assert.Contains(unmasked, value => value > 1f);
        for (int c = 0; c < 3; c++)
        {
            Assert.NotEqual(1f, unmasked[masked * 3 + c]);
            Assert.Equal(1f, masked_[masked * 3 + c]);
        }
        for (int i = 0; i < masked_.Length; i++)
        {
            if (i / 3 == masked) continue;
            Assert.Equal(
                BitConverter.SingleToInt32Bits(unmasked[i]),
                BitConverter.SingleToInt32Bits(masked_[i]));
        }
    }

    /// <summary>
    /// The straighten rotation's corners are the same kind of fill and get the same treatment; a
    /// picture pixel next to them is still allowed above diffuse white.
    /// </summary>
    [Fact]
    public void Rotation_corners_are_diffuse_white_on_an_extended_target()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d, Rotation = 12.0 };
        using var cmm = new LittleCmsEngine();

        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        // Which output pixels the rotation uncovers, by the rotation's own rule.
        ImageBuffer corners = Geometry.ApplyRotation(new ImageBuffer(4, 2), cal.Rotation, fill: 1.0f);
        int cornerCount = 0;
        for (int p = 0; p < corners.PixelCount; p++)
        {
            if (corners.Data[p * 3] != 1f) continue;
            cornerCount++;
            for (int c = 0; c < 3; c++) Assert.Equal(1f, frame.Pixels.Data[p * 3 + c]);
        }
        Assert.True(cornerCount > 0, "a 12° turn of a 4×2 frame must uncover at least one corner");
        Assert.True(cornerCount < corners.PixelCount, "and must leave some picture");
        Assert.Contains(frame.Pixels.Data, value => value > 1f);
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
    /// D-032 (superseding D-023's refusal): levels, contrast, highlights/shadows, curves and
    /// saturation are defined against a display range, and on an extended target they are given
    /// one (D-036 says which: the span-defined pair reach the roll's headroom, the anchored ones
    /// keep SDR's anchor). Each of them renders, takes effect, and stays inside the headroom —
    /// the shoulder's ceiling is enforced after them, not defeated by them.
    /// </summary>
    [Theory]
    [InlineData("Contrast")]
    [InlineData("Saturation")]
    [InlineData("Highlights")]
    [InlineData("Levels")]
    [InlineData("Curve")]
    public void Hdr_roll_applies_display_referred_stage2_adjustments_against_its_own_range(string adjustment)
    {
        var neutral = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        FrameParams graded = neutral.Clone();
        switch (adjustment)
        {
            case "Contrast": graded.Contrast = 0.25; break;
            case "Saturation": graded.Saturation = 0.3; break;
            case "Highlights": graded.Highlights = -0.4; break;
            case "Levels": graded.BlackPoint = 0.05; graded.WhitePoint = 0.9; break;
            case "Curve":
                graded.CurvePointsM = [(0.0, 0.0), (0.5, 0.4), (1.0, 1.0)];
                break;
        }
        using var cmm = new LittleCmsEngine();

        RenderedFrame plain = Pipeline.Render(
            MakeWorkingFrame(), neutral, ColorPipelineVersion.ManagedV2, cmm);
        RenderedFrame withGrade = Pipeline.Render(
            MakeWorkingFrame(), graded, ColorPipelineVersion.ManagedV2, cmm);

        float headroom = graded.ResolvedOutputTarget.HighlightHeadroom;
        Assert.NotEqual(plain.Pixels.Data, withGrade.Pixels.Data);
        Assert.All(withGrade.Pixels.Data, value => Assert.True(
            float.IsFinite(value) && value <= headroom,
            $"{adjustment} escaped the headroom: {value:R} > {headroom:R}."));
        Assert.Equal(ColorReference.SceneReferred, withGrade.Encoding.Reference);
        Assert.Equal(NumericRange.Extended, withGrade.Encoding.Range);
    }

    /// <summary>
    /// The point of defining the ops against the roll's range rather than against SDR white: the
    /// highlights slider reaches the highlights ABOVE SDR white. A pixel the neutral render put
    /// past 1.0 must come down when highlights are pulled, and the picture below white must not
    /// be crushed to get there — the two ranges are graded as one.
    /// </summary>
    [Fact]
    public void Hdr_highlights_slider_reaches_the_range_above_sdr_white()
    {
        var neutral = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        FrameParams pulled = neutral.Clone();
        pulled.Highlights = -0.6;
        using var cmm = new LittleCmsEngine();

        float[] plain = Pipeline.Render(
            MakeWorkingFrame(), neutral, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;
        float[] withPull = Pipeline.Render(
            MakeWorkingFrame(), pulled, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;

        int aboveWhite = 0;
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] <= 1f) continue;
            aboveWhite++;
            Assert.True(withPull[i] < plain[i],
                $"component {i} at {plain[i]:R} (above SDR white) did not come down: {withPull[i]:R}.");
        }
        Assert.True(aboveWhite > 0, "fixture has no highlight above SDR white to test with");
        Assert.Contains(plain.Zip(withPull), pair => pair.First < 1f && pair.Second > 0f);
    }

    /// <summary>
    /// D-036: contrast and levels are anchored to SDR white on an extended target, so below the
    /// shoulder's knee — where the extended render and its SDR rendition are the same pixels —
    /// they grade an HDR roll exactly as they grade its SDR rendition. Under D-032 the pivot sat
    /// at diffuse white and the whole picture below it was pushed down per channel.
    /// </summary>
    [Theory]
    [InlineData("Contrast")]
    [InlineData("Levels")]
    [InlineData("Saturation")]
    public void Hdr_anchored_ops_grade_the_sdr_range_as_the_sdr_rendition_does(string adjustment)
    {
        var neutral = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        FrameParams graded = neutral.Clone();
        switch (adjustment)
        {
            case "Contrast": graded.Contrast = 0.5; break;
            case "Levels": graded.BlackPoint = 0.05; graded.WhitePoint = 0.9; break;
            case "Saturation": graded.Saturation = 0.4; break;
        }
        using var cmm = new LittleCmsEngine();

        float[] plainHdr = Pipeline.Render(
            MakeNeutralWorkingFrame(), neutral, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;
        float[] hdr = Pipeline.Render(
            MakeNeutralWorkingFrame(), graded, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;
        float[] sdr = Pipeline.Render(
            MakeNeutralWorkingFrame(), graded.SdrRendition(), ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;

        int compared = 0;
        for (int p = 0; p < plainHdr.Length; p += 3)
        {
            // Only where the two renderings were the same pixel to begin with: at or below the
            // knee, and inside the gamut — the SDR terminal clips a D-005 negative, the extended
            // carrier keeps it.
            bool comparable = true;
            for (int c = 0; c < 3; c++)
                comparable &= plainHdr[p + c] >= 0f && plainHdr[p + c] <= HighlightRolloff.Knee;
            if (!comparable) continue;
            compared++;
            for (int c = 0; c < 3; c++)
            {
                float sdrLinear = Srgb.SrgbToLinear(sdr[p + c]);
                Assert.True(MathF.Abs(hdr[p + c] - sdrLinear) <= 2e-3f,
                    $"{adjustment}: component {p + c} is {hdr[p + c]:R} on the extended target but {sdrLinear:R} in the SDR rendition.");
            }
        }
        Assert.True(compared > 0, "fixture has no pixel below the knee to compare");
    }

    /// <summary>
    /// D-036: on an extended target nothing floors the chain's output (D-005 keeps the carrier's
    /// real out-of-gamut negatives), so the affine ops themselves must not manufacture one — a
    /// component that arrives non-negative leaves non-negative, as the SDR clamp would have left
    /// it. Under D-032 a hard contrast turned every dark chromatic pixel into a colour outside
    /// the gamut, which a wide-gamut panel showed as saturated red and blue.
    /// </summary>
    [Fact]
    public void Hdr_contrast_does_not_push_a_non_negative_component_negative()
    {
        var neutral = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        FrameParams hard = neutral.Clone();
        hard.Contrast = 1.0;
        using var cmm = new LittleCmsEngine();

        float[] plain = Pipeline.Render(
            MakeWorkingFrame(), neutral, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;
        float[] contrasty = Pipeline.Render(
            MakeWorkingFrame(), hard, ColorPipelineVersion.ManagedV2, cmm).Pixels.Data;

        Assert.NotEqual(plain, contrasty);
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] >= 0f)
                Assert.True(contrasty[i] >= 0f, $"component {i}: {plain[i]:R} became {contrasty[i]:R}.");
        }
    }

    /// <summary>
    /// D-036: the ceiling guard bounds a colour, not three channels. A pixel over the ceiling is
    /// scaled as a whole so its largest component sits exactly on it and its ratios are kept —
    /// clamping per channel changed the hue of every highlight an exposure push carried past the
    /// ceiling. NaN and colours under the ceiling are untouched, and a D-005 negative only moves
    /// toward zero.
    /// </summary>
    [Fact]
    public void Bound_above_scales_the_whole_colour_and_keeps_its_ratios()
    {
        const float ceiling = 4.93f;
        float[] data =
        [
            6f, 3f, 1.5f,          // over: scaled to the ceiling, ratios 4:2:1 kept
            2f, 1f, 0.5f,          // under: untouched
            6f, -1f, 2f,           // over with a D-005 negative: negative scaled toward zero
            float.NaN, 9f, 9f,     // NaN stays NaN; its neighbours are still bounded
            ceiling, ceiling, 0f,  // exactly on the ceiling: untouched
        ];

        HighlightRolloff.BoundAbove(data, ceiling);

        Assert.Equal(ceiling, data[0]);
        Assert.Equal(ceiling / 2f, data[1], 1e-6f);
        Assert.Equal(ceiling / 4f, data[2], 1e-6f);
        Assert.Equal([2f, 1f, 0.5f], data[3..6]);
        Assert.Equal(ceiling, data[6]);
        Assert.Equal(-1f * ceiling / 6f, data[7], 1e-6f);
        Assert.Equal(2f * ceiling / 6f, data[8], 1e-6f);
        Assert.True(float.IsNaN(data[9]));
        Assert.Equal([ceiling, ceiling], data[10..12]);
        Assert.Equal([ceiling, ceiling, 0f], data[12..15]);
        Assert.All(data.Where(float.IsFinite), v => Assert.True(v <= ceiling));
    }

    /// <summary>
    /// The ops run in an encoded domain that has to be open at both ends: overshoot above the
    /// headroom has to survive to the ceiling, and D-005's out-of-gamut negatives have to survive
    /// the round trip. On [0,1] it is sRGB's own curve, bit for bit.
    /// </summary>
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.002f)]
    [InlineData(0.18f)]
    [InlineData(1.0f)]
    [InlineData(4.93f)]
    [InlineData(18.0f)]
    [InlineData(-0.002f)]
    [InlineData(-0.35f)]
    public void Extended_stage2_encoding_round_trips_beyond_the_unit_range(float linear)
    {
        float encoded = Srgb.LinearToSrgbExtended(linear);
        float back = Srgb.SrgbToLinearExtended(encoded);

        Assert.True(MathF.Abs(back - linear) <= 1e-5f * MathF.Max(1f, MathF.Abs(linear)),
            $"{linear:R} came back as {back:R}.");
        Assert.Equal(MathF.Sign(linear), MathF.Sign(encoded));
        if (linear is >= 0f and <= 1f)
            Assert.Equal(Srgb.LinearToSrgb(linear), encoded);
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
    public void Extended_target_renders_a_print_stock_as_its_colour_with_the_hdr_tone()
    {
        // D-021 said the extended target ignores the print LUT; D-034 replaced that with "the
        // print's colour, the HDR tone". So the two renders now differ — and the print's own
        // shoulder is no longer the ceiling: the brightest fixture rows reach above 1.0.
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

        Assert.NotEqual(withoutLut, withLut);
        Assert.True(withLut[^3..].Max() > 1.0f, "the print's highlights must open above SDR white under HDR");
        Assert.All(withLut, v => Assert.True(v <= target.HighlightHeadroom + 1e-4f));
    }

    [Fact]
    public void Shoulder_is_the_identity_at_and_below_the_knee_for_every_member_of_the_family()
    {
        // This is what makes an HDR render and its SDR sibling the same photograph in the shadows
        // and mid-tones: below the knee the curve is the identity no matter where it is aimed.
        foreach (float asymptote in new[] { 1f, 2f, 4.926f, 20f })
        {
            float[] data = [0f, 0.18f, 0.49f, HighlightRolloff.Knee, -0.2f];
            float[] expected = (float[])data.Clone();

            HighlightRolloff.Apply(data, asymptote);

            AssertSameFloatBits(expected, data, $"identity at and below the knee (asymptote {asymptote:R})");
        }
    }

    /// <summary>
    /// The established SDR rendering is the <c>asymptote = 1</c> member of this family, not a
    /// separate implementation. If that ever stopped holding, every existing project would
    /// re-render — which is why the end-to-end bit-exactness gate above exists too.
    /// </summary>
    [Fact]
    public void Sdr_rendering_is_the_asymptote_one_member_of_the_family()
    {
        // The pre-parameterisation shoulder, verbatim: knee 0.5, Reinhard toward 1.0.
        static float Established(float v)
        {
            const float knee = 0.5f;
            if (v <= knee) return v;
            const float headroom = 1.0f - knee;
            float d = v - knee;
            return knee + headroom * d / (d + headroom);
        }

        foreach (float v in new[] { 0f, 0.25f, 0.5f, 0.5001f, 0.75f, 1f, 2f, 8f, 64f })
        {
            Assert.Equal(
                BitConverter.SingleToInt32Bits(Established(v)),
                BitConverter.SingleToInt32Bits(HighlightRolloff.Of(v, 1f)));
        }
    }

    [Fact]
    public void Shoulder_is_monotonic_and_approaches_but_never_passes_its_asymptote()
    {
        const float asymptote = 4f;
        float[] data = [0.5001f, 0.75f, 1f, 2f, 4f, 8f, 64f, 4096f, float.PositiveInfinity];

        HighlightRolloff.Apply(data, asymptote);

        for (int i = 1; i < data.Length; i++)
            Assert.True(data[i] > data[i - 1], $"index {i} broke monotonicity: {data[i - 1]} -> {data[i]}");
        Assert.All(data[..^1], value => Assert.True(
            value < asymptote, $"{value:R} reached the asymptote it may only approach."));
        Assert.Equal(asymptote, data[^1]);
    }

    /// <summary>
    /// The join at the knee must be C¹, not merely continuous. A shoulder that met the identity in
    /// value but not in slope would crease exactly where the highlights start, which is the part
    /// of the picture this whole feature exists to show.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(4.926f)]
    public void Shoulder_meets_the_identity_with_matching_slope_at_the_knee(float asymptote)
    {
        const float epsilon = 1e-4f;
        float probe = HighlightRolloff.Knee + epsilon;

        float rolled = HighlightRolloff.Of(probe, asymptote);

        // A C0-only join would depart from the identity linearly in epsilon; a C1 join departs
        // quadratically. The bound sits between the two by orders of magnitude.
        Assert.True(
            Math.Abs(rolled - probe) < 1e-7f,
            $"slope mismatch at the knee: {rolled:R} against {probe:R}");
    }

    [Fact]
    public void Shoulder_passes_non_finite_and_below_knee_values_through_untouched()
    {
        float[] data = [float.NaN, float.NegativeInfinity, -3f, 0f];

        HighlightRolloff.Apply(data, asymptote: 2f);

        Assert.True(float.IsNaN(data[0]));
        Assert.True(float.IsNegativeInfinity(data[1]));
        Assert.Equal(-3f, data[2]);
        Assert.Equal(0f, data[3]);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    [InlineData(float.NaN)]
    public void Shoulder_rejects_an_asymptote_at_or_below_the_knee(float asymptote) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HighlightRolloff.Apply([2f], asymptote));

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

    /// <summary>
    /// The established histogram clamps at one. Fed a scene-referred render it would pile every
    /// highlight into the last bin and read as a clipped picture; the extended histogram gives
    /// values above SDR white their own zone, in stops, and leaves the SDR zone shaped like the
    /// SDR histogram of the same picture.
    /// </summary>
    [Fact]
    public void Extended_histogram_puts_values_above_sdr_white_in_their_own_zone()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d };
        using var cmm = new LittleCmsEngine();
        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        OpenRevelare.Gui.Controls.HistogramData histogram =
            OpenRevelare.Gui.Controls.HistogramData.FromFrame(frame, cal.ResolvedOutputTarget.HighlightHeadroom);

        Assert.True(histogram.IsExtended);
        Assert.Equal(192, histogram.SdrBinCount);
        // 1000/203 = 4.93x = 2.3 stops fits Lightroom's fixed four-stop axis.
        Assert.Equal(4f, histogram.ExtendedStops);
        // The target's headroom rides along for the soft proof (D-028).
        Assert.Equal(cal.ResolvedOutputTarget.HighlightHeadroom, histogram.TargetHeadroom);
        int aboveWhite = frame.Pixels.Data.Count(value => value > 1f);
        Assert.True(aboveWhite > 0, "fixture must contain highlights above SDR white");
        float extendedZone = histogram.R[192..].Sum() + histogram.G[192..].Sum() + histogram.B[192..].Sum();
        Assert.Equal(aboveWhite, (int)extendedZone);
        // Nothing lands in the last bin unless it is at or beyond the axis end, which BoundAbove forbids.
        Assert.Equal(0f, histogram.R[255] + histogram.G[255] + histogram.B[255]);
    }

    [Fact]
    public void Sdr_histogram_is_unchanged_by_the_frame_overload()
    {
        var cal = new FrameParams { OutputSpace = "sRGB", PrintLut = "" };
        using var cmm = new LittleCmsEngine();
        RenderedFrame frame = Pipeline.Render(
            MakeWorkingFrame(), cal, ColorPipelineVersion.ManagedV2, cmm);

        OpenRevelare.Gui.Controls.HistogramData viaFrame =
            OpenRevelare.Gui.Controls.HistogramData.FromFrame(frame, cal.ResolvedOutputTarget.HighlightHeadroom);
        OpenRevelare.Gui.Controls.HistogramData viaBuffer =
            OpenRevelare.Gui.Controls.HistogramData.FromBuffer(frame.Pixels.Data);

        Assert.False(viaFrame.IsExtended);
        Assert.Equal(256, viaFrame.SdrBinCount);
        Assert.Equal(1f, viaFrame.TargetHeadroom);
        Assert.Equal(viaBuffer.R, viaFrame.R);
        Assert.Equal(viaBuffer.G, viaFrame.G);
        Assert.Equal(viaBuffer.B, viaFrame.B);
        Assert.Equal(viaBuffer.L, viaFrame.L);
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

    /// <summary>
    /// A near-neutral negative: every pixel renders inside the gamut, and the brighter half of it
    /// (dense on the positive) lands below the shoulder's knee, where the extended render and the
    /// SDR rendition are the same pixels. The saturated default fixture has neither.
    /// </summary>
    private static WorkingFrame MakeNeutralWorkingFrame() => MakeWorkingFrame([
        0.50f, 0.50f, 0.50f,
        0.30f, 0.30f, 0.30f,
        0.15f, 0.15f, 0.15f,
        0.08f, 0.08f, 0.08f,
        0.04f, 0.04f, 0.04f,
        0.02f, 0.02f, 0.02f,
        0.30f, 0.26f, 0.22f,
        0.10f, 0.12f, 0.14f,
    ]);

    private static WorkingFrame MakeWorkingFrame(float[]? data = null)
    {
        var pixels = new ImageBuffer(4, 2, data ?? [
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
