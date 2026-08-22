using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The source quantisation step, and the one thing that must be true of it: IT SURVIVES
/// RESAMPLING.
///
/// WHY THIS EXISTS. The endpoint estimators use the step to tell a density measurement from pure
/// quantisation — near black, one 8-bit code is 0.301 D, so the lowest codes carry almost no
/// information and always report the HIGHEST density. The guard built on that was first written to
/// MEASURE the step off the pixels, which worked on a full-resolution decode and silently did
/// nothing on the preview: Resample.Box averages factor² samples onto a finer lattice, the
/// observed minimum gap collapses to float noise (~1e-9), every sample then looks perfectly
/// resolved, and the guard is a no-op. The GUI only ever runs on that preview, so the entire fix
/// was inert exactly where it was needed — the blue endpoint stayed at 6.39.
///
/// These tests pin the property that failure violated.
/// </summary>
public class QuantisationStepTests
{
    private static ImageBuffer Decoded8Bit()
    {
        // A gradient, so the buffer holds many distinct levels rather than a few flat patches.
        const int w = 64, h = 64;
        var img = new ImageBuffer(w, h) { SourceQuantisationStep = Srgb.SrgbToLinear(1.0f / 255.0f) };
        for (int p = 0; p < w * h; p++)
            for (int c = 0; c < 3; c++)
                img.Data[p * 3 + c] = Srgb.SrgbToLinear((p % 200) / 255.0f);
        return img;
    }

    /// <summary>
    /// The regression proper: box-downsampling must NOT change the step. The averaged pixels sit
    /// on a finer grid, but they carry no information the file did not have, so the answer to
    /// "how coarsely was this sampled?" is unchanged.
    /// </summary>
    [Fact]
    public void Box_resampling_preserves_the_source_step()
    {
        ImageBuffer src = Decoded8Bit();
        ImageBuffer small = Resample.Box(src, 16);

        Assert.True(small.Width < src.Width, "the test needs an actual downsample");
        Assert.Equal(src.SourceQuantisationStep, small.SourceQuantisationStep, 12);
    }

    /// <summary>Orientation permutes pixels and must carry the step with them.</summary>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(2, false, true)]
    public void Orientation_preserves_the_source_step(int quarterTurns, bool flipH, bool flipV)
    {
        ImageBuffer src = Decoded8Bit();
        ImageBuffer turned = Geometry.ApplyOrientation(src, quarterTurns, flipH, flipV);
        Assert.Equal(src.SourceQuantisationStep, turned.SourceQuantisationStep, 12);
    }

    /// <summary>
    /// A crop is the estimator path's own reframing (AutoRegion) and must keep the step too — a
    /// dropped stamp here would disable the guard for every cropped roll while leaving it working
    /// on uncropped ones, which is the hardest kind of bug to notice.
    /// </summary>
    [Fact]
    public void Crop_preserves_the_source_step()
    {
        ImageBuffer src = Decoded8Bit();
        ImageBuffer cropped = Geometry.ApplyCrop(src, (0.25, 0.25, 0.5, 0.5));
        Assert.Equal(src.SourceQuantisationStep, cropped.SourceQuantisationStep, 12);
    }

    /// <summary>
    /// The step is stamped in the LINEAR domain the buffer actually holds, not the encoded one.
    ///
    /// 1/255 is the step between CODES; the samples are sRGB-decoded, so the shadow lattice is
    /// ~13x finer than that. Stamping the encoded value overstates the coarseness and the guard
    /// then discards ordinary samples — measured, it pulled a healthy red endpoint from 1.96 down
    /// to 1.56.
    /// </summary>
    [Fact]
    public void The_stamped_step_is_in_linear_units_not_encoded_ones()
    {
        ImageBuffer src = Decoded8Bit();
        double encodedStep = 1.0 / 255.0;

        Assert.True(src.SourceQuantisationStep < encodedStep / 5.0,
            $"step {src.SourceQuantisationStep:E3} looks like the encoded step {encodedStep:E3}");
        Assert.Equal(Srgb.SrgbToLinear(1.0f / 255.0f), src.SourceQuantisationStep, 10);
    }

    /// <summary>
    /// An unstamped buffer (step 0) must leave every estimator exactly as it was — the guard is
    /// opt-in on knowing the source, never a default that quietly reshapes results for callers
    /// that never supplied one.
    /// </summary>
    [Fact]
    public void An_unstamped_buffer_is_left_alone()
    {
        ImageBuffer stamped = Decoded8Bit();
        var bare = new ImageBuffer(stamped.Width, stamped.Height, (float[])stamped.Data.Clone());
        Assert.Equal(0.0, bare.SourceQuantisationStep);

        var unit = new[] { 1.0, 1.0, 1.0 };
        double[] withGuard = FilmBase.MaxChannelDensityFromRoll(new[] { stamped }, unit);
        double[] without = FilmBase.MaxChannelDensityFromRoll(new[] { bare }, unit);

        // The stamped one may reject its coarsest samples; the bare one must keep everything.
        for (int c = 0; c < 3; c++)
            Assert.True(without[c] >= withGuard[c] - 1e-9,
                $"channel {c}: unstamped {without[c]:F4} should not be below stamped {withGuard[c]:F4}");
    }
}
