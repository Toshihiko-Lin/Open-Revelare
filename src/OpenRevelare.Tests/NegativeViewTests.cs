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
    /// <summary>Gains are a per-channel multiply and nothing else — no clamp, no normalisation
    /// of the caller's numbers, applied in the scene-linear domain where that is the whole
    /// definition of a white balance.</summary>
    [Fact]
    public void Gains_multiply_each_channel_independently()
    {
        var data = new float[] { 0.25f, 0.5f, 0.75f, 1.5f, 2.0f, 0.1f };
        NegativeView.ApplyWhiteBalance(data, new[] { 2.0, 1.0, 0.5 });

        Assert.Equal(0.5f, data[0], 6);
        Assert.Equal(0.5f, data[1], 6);
        Assert.Equal(0.375f, data[2], 6);
        // Above 1.0 is left above 1.0: the encode deals with highlights, the same way the
        // positive path treats them. A clamp here would flatten the film base's brightest edge —
        // the very thing the film-base sampler is aimed at.
        Assert.Equal(3.0f, data[3], 6);
        Assert.Equal(2.0f, data[4], 6);
        Assert.Equal(0.05f, data[5], 6);
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
        ColorPipeline.ToOutputSpace(view.Data, cal.ResolvedOutputSpace);

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
