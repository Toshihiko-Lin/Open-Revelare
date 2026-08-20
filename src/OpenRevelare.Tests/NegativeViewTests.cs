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

    /// <summary>A frame with a value in every channel, so a per-channel gain cannot be masked by
    /// a flat or symmetric picture.</summary>
    private static ImageBuffer Ramp(int w, int h)
    {
        var img = new ImageBuffer(w, h);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = 0.05f + (i % 89) / 120.0f;
        return img;
    }
}
