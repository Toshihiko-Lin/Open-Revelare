using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// D-028: a render that reaches above what the display can show is fitted into the display's
/// headroom at presentation time, with the SDR range untouched. These pin the curve's contract;
/// the composition root's use of it is covered by the status badge and by eye.
/// </summary>
public sealed class HighlightSoftProofTests
{
    // 1000-nit master (4.93×) on the dorm desktop with the SDR slider at Windows' default (2.16×).
    private const float Content = 1000f / 203f;
    private const float Display = 520f / 240f;

    [Theory]
    [InlineData(-0.2f)]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.949f)]
    [InlineData(1f)]
    public void At_and_below_diffuse_white_the_proof_is_the_identity(float value)
    {
        Assert.Equal(value, HighlightSoftProof.Of(value, Content, Display));
    }

    [Fact]
    public void The_content_ceiling_lands_exactly_on_the_display_ceiling()
    {
        Assert.Equal(Display, HighlightSoftProof.Of(Content, Content, Display), 1e-5f);
        // A half-precision round-up past the render's bound must not overshoot the panel.
        Assert.Equal(Display, HighlightSoftProof.Of(Content * 1.01f, Content, Display), 1e-5f);
    }

    [Fact]
    public void The_shoulder_leaves_the_knee_with_slope_one_and_stays_monotone_and_below_the_ceiling()
    {
        const float h = 1e-3f;
        float slope = (HighlightSoftProof.Of(1f + h, Content, Display) - 1f) / h;
        Assert.Equal(1f, slope, 0.02f);

        float previous = 1f;
        for (float v = 1f + 0.01f; v <= Content; v += 0.01f)
        {
            float mapped = HighlightSoftProof.Of(v, Content, Display);
            Assert.True(mapped > previous, $"not monotone at {v}");
            Assert.True(mapped <= Display + 1e-5f, $"over the display ceiling at {v}");
            Assert.True(mapped <= v, $"a soft proof never brightens: {v} → {mapped}");
            previous = mapped;
        }
    }

    [Theory]
    [InlineData(Content, Content)]          // display shows all of it
    [InlineData(Content, 10f)]              // display out-reaches it
    [InlineData(1f, Display)]               // SDR content has nothing to fit
    [InlineData(Content, 1f)]               // no headroom to fit into: clip instead (see class doc)
    [InlineData(Content, float.NaN)]
    [InlineData(float.PositiveInfinity, Display)]
    public void Nothing_is_needed_when_there_is_nothing_to_fit_or_nothing_to_fit_into(float content, float display)
    {
        Assert.False(HighlightSoftProof.IsNeeded(content, display));
    }

    [Fact]
    public void Fit_returns_the_same_scene_when_nothing_is_needed_and_a_new_one_otherwise()
    {
        PresentationScene scene = Scene(0.25f, 1f, 3f);

        Assert.Same(scene, HighlightSoftProof.Fit(scene, Content, 10f));
        Assert.Same(scene, HighlightSoftProof.Fit(scene, 1f, Display));

        PresentationScene fitted = HighlightSoftProof.Fit(scene, Content, Display);
        Assert.NotSame(scene, fitted);
        Assert.Equal(scene.Size, fitted.Size);
        Assert.Equal(scene.ReferenceWhiteScale, fitted.ReferenceWhiteScale);
    }

    [Fact]
    public void Fit_touches_only_colour_above_diffuse_white_and_carries_alpha_across()
    {
        PresentationScene scene = Scene(0.25f, 1f, 3f);
        PresentationScene fitted = HighlightSoftProof.Fit(scene, Content, Display);

        Assert.Equal((Half)0.25f, fitted.LinearExtendedSrgbRgba[0]);
        Assert.Equal((Half)1f, fitted.LinearExtendedSrgbRgba[1]);
        float expected = HighlightSoftProof.Of(3f, Content, Display);
        Assert.Equal(expected, (float)fitted.LinearExtendedSrgbRgba[2], 2e-3f);
        Assert.True((float)fitted.LinearExtendedSrgbRgba[2] < 3f);
        Assert.Equal((Half)1f, fitted.LinearExtendedSrgbRgba[3]);
    }

    private static PresentationScene Scene(float r, float g, float b) =>
        new(new[] { (Half)r, (Half)g, (Half)b, (Half)1f }, new PixelSize(1, 1), referenceWhiteScale: 1f);
}
