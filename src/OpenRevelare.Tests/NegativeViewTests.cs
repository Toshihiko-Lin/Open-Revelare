using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The negative view's DISPLAY white balance — the gain that makes an un-inverted UniWB frame
/// look like film on a light table rather than a green cast.
///
/// The whole risk in this feature is that a viewing convenience leaks into the measurements, so
/// the tests are written around that boundary: the pixels the user LOOKS at are white balanced,
/// the pixels Stage 1 MEASURES are not, and the two negative render routes (the whole-frame view
/// and the sharp patch that blits over it) must agree — a patch carrying a different gain from
/// the preview underneath is exactly how this shows up as a colour flash on zoom.
/// </summary>
public class NegativeViewTests
{
    /// <summary>Gains are a per-channel multiply applied in the scene-linear domain, where that is
    /// the whole definition of a white balance — no clamp, and no cross-channel mixing.</summary>
    [Fact]
    public void Gains_multiply_each_channel_independently()
    {
        var data = new float[] { 0.25f, 0.5f, 0.75f, 1.5f, 2.0f, 0.1f };
        NegativeView.ApplyWhiteBalance(data, new[] { 2.0, 1.0, 0.5 });

        // The vector is renormalised onto constant luminance first (see the tests below), so the
        // gain that lands is g/luma(g), not g. Asserted against the same weights the method
        // derives — ACEScg's, the space the buffer is in when this runs.
        double lum = Luma(2.0, 1.0, 0.5);
        float gr = (float)(2.0 / lum), gg = (float)(1.0 / lum), gb = (float)(0.5 / lum);

        Assert.Equal(0.25f * gr, data[0], 6);
        Assert.Equal(0.5f * gg, data[1], 6);
        Assert.Equal(0.75f * gb, data[2], 6);
        // Above 1.0 is left above 1.0: the encode deals with highlights, the same way the
        // positive path treats them. A clamp here would flatten the film base's brightest edge —
        // the very thing the film-base sampler is aimed at.
        Assert.Equal(1.5f * gr, data[3], 6);
        Assert.Equal(2.0f * gg, data[4], 6);
        Assert.Equal(0.1f * gb, data[5], 6);
        // And the ratios between the channels are EXACTLY the caller's, which is the part of the
        // vector a white balance is entitled to. Renormalising must not disturb them.
        Assert.Equal((0.25f * 2.0) / (0.5f * 1.0), data[0] / data[1], 5);
        Assert.Equal((0.75f * 0.5) / (0.5f * 1.0), data[2] / data[1], 5);
    }

    // ── The display transform: a VIEWER, not the pipeline's display rendering ──
    //
    // The negative view used to encode through ColorPipeline.ToOutputSpace — step 4, which carries
    // the Cineon encode and CineonToDisplay. Both describe a CALIBRATED POSITIVE: ToCineon puts
    // linear 1.0 at code 1032 (the density ceiling, which only inversion puts there) and
    // CineonToDisplay renders against code 685, 347 codes lower. An un-inverted frame has had none
    // of that done to it, so the encode ran wherever the raw exposure sat and the picture blew
    // out — scene-linear 0.2 rendered 224/255, and 0.4 upwards pinned near white.

    /// <summary>
    /// The negative view is a plain viewer transform: the primaries conversion and the output
    /// space's encoding curve, and nothing else — bit for bit what any image viewer does with the
    /// file. That equivalence IS the specification, since the view exists to be compared against
    /// what the user already knows the scan looks like.
    /// </summary>
    [Fact]
    public void The_negative_display_transform_is_a_plain_viewer_transform()
    {
        var mine = new float[] { 0.30f, 0.18f, 0.09f, 0.02f, 0.55f, 0.80f };
        var viewer = (float[])mine.Clone();

        NegativeView.ToDisplay(mine, ColorSpaces.Srgb);
        OutputRender.Convert(viewer, ColorPipeline.Working, ColorSpaces.Srgb);
        OutputRender.Encode(viewer, ColorSpaces.Srgb);

        Assert.Equal(viewer, mine);
    }

    /// <summary>
    /// THE REGRESSION, at the value that showed it worst. Step 4 rendered scene-linear 0.2 at
    /// 224/255 and everything from 0.4 up between 246 and 253 — the top of the range crushed flat,
    /// which is what "too bright" looked like. A viewer transform keeps that range separated.
    /// </summary>
    [Fact]
    public void Mid_and_high_negative_values_are_not_crushed_against_white()
    {
        var d = new float[] { 0.2f, 0.2f, 0.2f, 0.4f, 0.4f, 0.4f, 0.8f, 0.8f, 0.8f };
        NegativeView.ToDisplay(d, ColorSpaces.Srgb);

        // 0.2 is a mid tone, not a near-white: the old path put it at 0.878.
        Assert.InRange(d[0], 0.40, 0.56);
        // 0.4 and 0.8 stay clearly apart — the old path had them 0.964 vs 0.989, four levels of
        // 255 between them, which is a flat white where there should be two stops of separation.
        Assert.True(d[6] - d[3] > 0.15,
                    $"0.4 and 0.8 must stay separated; got {d[3]:F3} and {d[6]:F3}");
    }

    /// <summary>
    /// And the negative view must NOT be the pipeline's step 4 — stated directly, because the bug
    /// was a call to the wrong one of two functions with compatible signatures.
    /// </summary>
    [Fact]
    public void The_negative_display_transform_is_not_step_four()
    {
        var viewer = new float[] { 0.2f, 0.2f, 0.2f };
        var stepFour = (float[])viewer.Clone();

        NegativeView.ToDisplay(viewer, ColorSpaces.Srgb);
        ColorPipeline.ToOutputSpace(stepFour, ColorSpaces.Srgb);

        Assert.True(stepFour[0] - viewer[0] > 0.3,
                    "step 4 renders an un-inverted frame far hotter; if these have converged, " +
                    "the negative view has been put back on the display rendering");
    }

    // ── Brightness: a viewing white balance must not double as an exposure ──────
    //
    // The gains arrive green-normalised from RawDecode.CameraWhiteBalance, and that was once
    // mistaken for a brightness safeguard. It is not: pinning GREEN at 1.0 leaves red and blue
    // free to rise, and as-shot coefficients run about 2.2 / 1.0 / 1.5 under daylight, so a raw
    // negative came up roughly a third of a stop hot the moment the view was opened. Green
    // carries 0.67 of the luminance in ACEScg, not all of it. The tests below pin the level.

    /// <summary>
    /// THE FIX: a neutral pixel keeps its luminance exactly. This is the invariant that separates
    /// "white balance" from "white balance plus an exposure nobody asked for" — the view has no
    /// exposure control, so any level change it makes is unaccountable.
    /// </summary>
    [Theory]
    [InlineData(2.19, 1.0, 1.53)]      // a typical daylight as-shot record
    [InlineData(1.45, 1.0, 2.40)]      // tungsten — the cast leans the other way
    [InlineData(3.10, 1.0, 1.20)]      // a strong cast, where the old error was largest
    [InlineData(0.40, 1.0, 0.80)]      // sub-unit gains, i.e. the darkening direction
    public void A_neutral_pixel_keeps_its_luminance(double r, double g, double b)
    {
        var data = new float[] { 0.4f, 0.4f, 0.4f };
        NegativeView.ApplyWhiteBalance(data, new[] { r, g, b });

        Assert.Equal(Luma(0.4, 0.4, 0.4), Luma(data[0], data[1], data[2]), 5);
    }

    /// <summary>
    /// The specific regression, stated as the user saw it: opening the negative view on a raw
    /// frame made the picture brighter. Green-normalised camera gains are the input that did it.
    /// </summary>
    [Fact]
    public void Camera_gains_do_not_brighten_the_picture()
    {
        var frame = Ramp(16, 16);
        var data = (float[])frame.Data.Clone();
        double before = MeanLuma(data);

        NegativeView.ApplyWhiteBalance(data, new[] { 2.19, 1.0, 1.53 });

        // Bounded in STOPS, because that is the unit the complaint was made in. The old
        // green-normalised multiply put this at +0.37 EV — clearly visible. A ramp gives each
        // channel a different value, so a per-channel gain still perturbs the frame MEAN a little
        // even when it is exactly luminance-neutral per pixel (that exactness is pinned on neutral
        // pixels above); what must not survive is a systematic lift. 0.01 EV is a hundredth of a
        // stop, far below anything visible and ~37x smaller than the bug.
        double ev = Math.Log2(MeanLuma(data) / before);
        Assert.True(Math.Abs(ev) < 0.01, $"negative view moved the level by {ev:F4} EV");
    }

    /// <summary>
    /// A gain vector with no meaningful level — all zero, or negative enough that the weighted sum
    /// cancels — cannot be renormalised, so it is dropped rather than divided by. Inventing a
    /// scale here would put an arbitrary exposure under a tool for judging colour by eye, which is
    /// the same "unknown, not unit gain" rule the probe itself applies.
    /// </summary>
    [Theory]
    [InlineData(new double[] { 0.0, 0.0, 0.0 })]
    [InlineData(new double[] { -1.0, -1.0, -1.0 })]
    public void Gains_with_no_usable_level_are_ignored(double[] gains)
    {
        var data = new float[] { 0.25f, 0.5f, 0.75f };
        var before = (float[])data.Clone();
        NegativeView.ApplyWhiteBalance(data, gains);
        Assert.Equal(before, data);
    }

    /// <summary>ACEScg's luminance weights — the Y row of its RGB→XYZ matrix, derived here the
    /// same way the code derives them rather than copied as literals, so the two cannot drift.
    /// </summary>
    private static double Luma(double r, double g, double b)
    {
        double[,] m = ColorPipeline.Working.ToXyz();
        return m[1, 0] * r + m[1, 1] * g + m[1, 2] * b;
    }

    private static double MeanLuma(float[] data)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i + 2 < data.Length; i += 3, n++)
            sum += Luma(data[i], data[i + 1], data[i + 2]);
        return sum / n;
    }

    /// <summary>
    /// "No camera coefficients" must be a no-op, not a guess. A scanner TIFF and a body LibRaw
    /// has no as-shot record for both land here, and inventing gains would put an invented colour
    /// under a tool whose entire job is judging colour by eye.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(new double[] { 1.0, 1.0, 1.0 })]
    public void Absent_or_unit_gains_leave_the_pixels_alone(double[]? gains)
    {
        var data = new float[] { 0.25f, 0.5f, 0.75f };
        var before = (float[])data.Clone();
        NegativeView.ApplyWhiteBalance(data, gains);
        Assert.Equal(before, data);
    }

    /// <summary>A malformed gain vector is ignored rather than half-applied — a probe that
    /// returned nonsense must not be able to tint the view it was meant to correct.</summary>
    [Theory]
    [InlineData(new double[] { 2.0, 1.0 })]                       // too short
    [InlineData(new double[] { 2.0, 1.0, 0.5, 1.0 })]             // too long
    [InlineData(new double[] { double.NaN, 1.0, 0.5 })]
    [InlineData(new double[] { 2.0, 1.0, double.PositiveInfinity })]
    public void Malformed_gains_are_ignored(double[] gains)
    {
        var data = new float[] { 0.25f, 0.5f, 0.75f };
        var before = (float[])data.Clone();
        NegativeView.ApplyWhiteBalance(data, gains);
        Assert.Equal(before, data);
    }

    /// <summary>
    /// THE POINT OF THE FEATURE, at the real entry point: the film-base patch renders through
    /// <see cref="RegionRender.Render"/> with negative:true, and passing camera gains there has
    /// to change what comes out. Asserting on <see cref="NegativeView.ApplyWhiteBalance"/> alone
    /// would prove nothing — the bug this guards against is the parameter never being threaded
    /// to the renderer at all.
    /// </summary>
    [Fact]
    public void The_negative_patch_applies_the_camera_white_balance()
    {
        var frame = Ramp(16, 16);
        var roi = new RegionRender.Roi(0, 0, 1, 1);
        var cal = new FrameParams();

        var balanced = RegionRender.Render(frame, cal, roi, negative: true,
                                           negativeWb: new[] { 2.0, 1.0, 0.5 });
        var bare = RegionRender.Render(frame, cal, roi, negative: true);

        Assert.NotEqual(bare.Image.Data, balanced.Image.Data);
    }

    /// <summary>
    /// Null gains through the renderer must reproduce the OLD picture exactly. This is what makes
    /// the feature safe for a scanner TIFF roll: nothing was probed, so nothing changes.
    /// </summary>
    [Fact]
    public void The_negative_patch_without_gains_is_unchanged()
    {
        var frame = Ramp(16, 16);
        var roi = new RegionRender.Roi(0, 0, 1, 1);
        var cal = new FrameParams();

        var explicitNull = RegionRender.Render(frame, cal, roi, negative: true, negativeWb: null);
        var bare = RegionRender.Render(frame, cal, roi, negative: true);

        Assert.Equal(bare.Image.Data, explicitNull.Image.Data);
    }

    /// <summary>
    /// The POSITIVE path must not see these gains at all. It white balances in Stage 2 from the
    /// user's own temp/tint, and a display gain leaking in would double-balance the finished
    /// image — a viewing aid changing the picture that gets exported is the one outcome this
    /// feature must never have.
    /// </summary>
    [Fact]
    public void The_positive_patch_ignores_the_negative_white_balance()
    {
        var frame = Ramp(16, 16);
        var roi = new RegionRender.Roi(0, 0, 1, 1);
        var cal = new FrameParams();

        var withGains = RegionRender.Render(frame, cal, roi, negative: false,
                                            negativeWb: new[] { 2.0, 1.0, 0.5 });
        var without = RegionRender.Render(frame, cal, roi, negative: false);

        Assert.Equal(without.Image.Data, withGains.Image.Data);
    }

    /// <summary>
    /// The gain rides on a COPY. <c>ShowNegativeView</c> clones the preview buffer before
    /// balancing it precisely so that every Stage-1 sampler keeps reading UniWB pixels; if the
    /// renderer mutated its input instead, arming the film-base tool would permanently tint the
    /// buffer t_base is measured from and the roll's whole colour basis would drift by a camera
    /// white balance.
    /// </summary>
    [Fact]
    public void The_renderer_does_not_mutate_its_source()
    {
        var frame = Ramp(16, 16);
        var before = (float[])frame.Data.Clone();

        RegionRender.Render(frame, new FrameParams(), new RegionRender.Roi(0, 0, 1, 1),
                            negative: true, negativeWb: new[] { 2.0, 1.0, 0.5 });

        Assert.Equal(before, frame.Data);
    }

    // ── Framing: the negative and the positive are the same rectangle ───────────
    //
    // The negative view used to apply ORIENTATION ONLY — no straighten, no crop — so toggling it
    // moved the picture under a zoom and pan that stayed put: a straightened frame went crooked
    // again, a cropped one snapped out to the whole strip. The tests below pin the fix at the
    // level it has to hold: whatever the geometry, the two renders cover the same area of the
    // same frame, and only the pixel VALUES differ.

    /// <summary>
    /// A crop must move the negative with it. Before the fix this was the loudest case — the
    /// negative kept showing the whole scan while the positive showed the kept picture, so the
    /// toggle looked like a zoom-out.
    /// </summary>
    [Fact]
    public void The_negative_patch_follows_the_crop()
    {
        var frame = Ramp(64, 64);
        var roi = new RegionRender.Roi(0, 0, 1, 1);

        var uncropped = RegionRender.Render(frame, new FrameParams(), roi, negative: true);
        var cropped = RegionRender.Render(
            frame, new FrameParams { CropRect = (0.25, 0.25, 0.5, 0.5) }, roi, negative: true);

        // A half-by-half crop of a 64² frame is 32², not 64² — the picture is genuinely reframed
        // rather than merely re-sampled.
        Assert.Equal(64, uncropped.Image.Width);
        Assert.Equal(32, cropped.Image.Width);
        Assert.Equal(32, cropped.Image.Height);
    }

    /// <summary>The straighten angle must reach the negative too — the same rotation, about the
    /// same centre, that the positive is drawn through.</summary>
    [Fact]
    public void The_negative_patch_follows_the_straighten()
    {
        var frame = Ramp(64, 64);
        var roi = new RegionRender.Roi(0, 0, 1, 1);

        var straight = RegionRender.Render(frame, new FrameParams(), roi, negative: true);
        var turned = RegionRender.Render(frame, new FrameParams { Rotation = 5.0 }, roi,
                                         negative: true);

        // Rotation preserves the buffer's size and changes its content — a same-size, same-pixels
        // result is the old orientation-only shortcut coming back.
        Assert.Equal(straight.Image.Width, turned.Image.Width);
        Assert.Equal(straight.Image.Height, turned.Image.Height);
        Assert.NotEqual(straight.Image.Data, turned.Image.Data);
    }

    /// <summary>
    /// THE INVARIANT THE WHOLE CHANGE IS FOR: under any geometry, the negative and the positive
    /// come out the same shape and report the same realised rectangle. The user keeps one zoom and
    /// one pan across the toggle, so a disagreement here IS the picture jumping on screen.
    /// </summary>
    [Theory]
    [InlineData(0, false, false, 0.0, false)]
    [InlineData(1, false, false, 0.0, false)]     // quarter turn — axes swap
    [InlineData(0, true, false, 0.0, false)]      // mirror
    [InlineData(0, false, false, 5.0, false)]     // straighten
    [InlineData(0, false, false, 0.0, true)]      // crop
    [InlineData(1, true, true, -3.5, true)]       // all of it at once
    public void The_two_views_are_framed_identically(int turns, bool flipH, bool flipV,
                                                     double rotation, bool crop)
    {
        var frame = Ramp(48, 64);   // deliberately NOT square, so a swapped axis shows up
        var roi = new RegionRender.Roi(0.1, 0.2, 0.5, 0.5);
        var cal = new FrameParams
        {
            QuarterTurns = turns,
            FlipH = flipH,
            FlipV = flipV,
            Rotation = rotation,
            CropRect = crop ? (0.2, 0.1, 0.6, 0.7) : null,
        };

        var positive = RegionRender.Render(frame, cal, roi, negative: false);
        var negative = RegionRender.Render(frame, cal, roi, negative: true);

        Assert.Equal(positive.Image.Width, negative.Image.Width);
        Assert.Equal(positive.Image.Height, negative.Image.Height);
        Assert.Equal(positive.Realised.X, negative.Realised.X, 12);
        Assert.Equal(positive.Realised.Y, negative.Realised.Y, 12);
        Assert.Equal(positive.Realised.W, negative.Realised.W, 12);
        Assert.Equal(positive.Realised.H, negative.Realised.H, 12);
        // Same frame, same rectangle — and still two different pictures, which is the point of
        // having the view at all.
        Assert.NotEqual(positive.Image.Data, negative.Image.Data);
    }

    /// <summary>
    /// The bounds a caller reserves for a negative patch are the bounds the negative patch reads.
    /// These were once genuinely different functions, and a region decode sized by the wrong one
    /// hands back the wrong part of the file — silently, as a patch of somewhere else.
    /// </summary>
    [Fact]
    public void The_negative_reserves_the_same_source_bounds_as_the_positive()
    {
        var cal = new FrameParams
        {
            QuarterTurns = 1, FlipH = true, Rotation = 4.0, CropRect = (0.15, 0.2, 0.6, 0.6),
        };
        var roi = new RegionRender.Roi(0.3, 0.3, 0.4, 0.4);

        Assert.Equal(RegionRender.RequiredSourceBounds(48, 64, cal, roi),
                     RegionRender.RequiredSourceBoundsNegative(48, 64, cal, roi));
    }

    /// <summary>
    /// The whole-frame negative view and the sharp patch that blits over it are the SAME picture
    /// at two resolutions, so they must agree pixel for pixel — a difference is a visible flash
    /// the moment the user zooms past the patch threshold. The two are written independently (the
    /// view composes Geometry's operators in <c>MainViewModel.GeometryForNegative</c>; the patch
    /// runs <see cref="RegionRender"/>'s composed inverse map), which is exactly why this needs a
    /// test rather than an argument.
    ///
    /// Reproduces the view's chain here because it lives in the GUI assembly. Straighten is left
    /// out of the cases on purpose: the two paths interpolate the same taps with the same weights,
    /// but the view rotates ONCE over the whole buffer while the patch composes the rotation into
    /// its map, so they agree to bilinear rounding rather than exactly. The framing tests above
    /// already pin the rotation's geometry.
    /// </summary>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, false, false, false)]     // quarter turn
    [InlineData(0, true, true, true)]        // both mirrors + crop
    [InlineData(2, false, true, true)]       // half turn + mirror + crop
    public void The_view_and_the_patch_are_the_same_picture(int turns, bool flipH, bool flipV,
                                                            bool crop)
    {
        var frame = Ramp(48, 64);
        var cal = new FrameParams
        {
            QuarterTurns = turns,
            FlipH = flipH,
            FlipV = flipV,
            CropRect = crop ? (0.25, 0.125, 0.5, 0.75) : null,
        };

        // The VIEW's chain, as GeometryForNegative composes it.
        var view = new ImageBuffer(frame.Width, frame.Height, (float[])frame.Data.Clone());
        if (turns % 4 != 0 || flipH || flipV)
            view = Geometry.ApplyOrientation(view, turns, flipH, flipV);
        if (cal.CropRect is { } c) view = Geometry.ApplyCrop(view, c);
        NegativeView.ToDisplay(view.Data, cal.ResolvedOutputSpace);

        // The PATCH, asked for over the whole displayed frame.
        var patch = RegionRender.Render(frame, cal, new RegionRender.Roi(0, 0, 1, 1),
                                        negative: true);

        Assert.Equal(view.Width, patch.Image.Width);
        Assert.Equal(view.Height, patch.Image.Height);
        for (int i = 0; i < view.Data.Length; i++)
            Assert.Equal(view.Data[i], patch.Image.Data[i], 4);
    }

    /// <summary>A frame with a value in every channel, so a per-channel gain cannot be masked by
    /// a flat or symmetric picture.</summary>
    private static ImageBuffer Ramp(int w, int h)
    {
        var img = new ImageBuffer(w, h);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = 0.05f + (i % 89) / 120.0f;
        return img;
    }
}
