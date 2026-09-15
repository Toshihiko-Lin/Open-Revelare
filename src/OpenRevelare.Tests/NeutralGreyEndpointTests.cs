using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// A neutral patch fixes the RATIOS of the three highlight spans and the named code spends the
/// shared factor; the Deep-WB step must hold that factor exactly. These pin both: the card lands
/// on 470 in every channel, and the step's invariant.
/// </summary>
public class NeutralGreyEndpointTests
{
    // A C-41-ish roll: orange base, a grey card ~0.7 above base with a cast on it.
    private static readonly double[] DMin = { 0.09, 0.29, 0.54 };
    private static readonly double[] Grey = { 0.09 + 0.62, 0.29 + 0.71, 0.54 + 0.78 };

    private static double[] CodesOf(double[] dMax)
    {
        var ep = DensityEndpoints.FromMeasured(dMax, FrameParams.OutputRange, DMin);
        return new[] { ep.CodeOf(0, Grey[0]), ep.CodeOf(1, Grey[1]), ep.CodeOf(2, Grey[2]) };
    }

    [Fact]
    public void AnchorToCineon_LandsTheCardExactlyOn470_InEveryChannel()
    {
        double[] dMax = DensityEndpoints.HighlightFromNeutralAtCode(Grey, DMin, FrameParams.CineonGreyCode);
        foreach (double code in CodesOf(dMax)) Assert.Equal(470.0, code, 6);
        // Sanity on the magnitude: the factor is 1.874/0.750 ≈ 2.50 on the span, so a card 0.6–0.8
        // above base gives spans in the range roll calibration measures.
        for (int c = 0; c < 3; c++)
        {
            double span = dMax[c] - DMin[c];
            Assert.InRange(span, 1.4, 2.2);
            Assert.Equal((Grey[c] - DMin[c]) * FrameParams.OutputRange / 0.750, span, 6);
        }
    }

    [Fact]
    public void StandardGrey_RendersAt18Percent()
    {
        // The constant is only right if the display rendering agrees: 470 through CineonToDisplay
        // is 18% of the 90% diffuse white (code 685 = 1.0 before the shoulder), i.e. 0.192 linear
        // before the base normalisation and 0.183 after — below the shoulder's knee, so untouched
        // by it. Code 95 is display black.
        var data = new[] { (float)(FrameParams.CineonGreyCode / 1023.0), (float)(95.0 / 1023.0) };
        ColorPipeline.CineonToDisplay(data);
        Assert.Equal(0.0f, data[1], 3);
        Assert.InRange(data[0], 0.178, 0.188);
    }

    [Fact]
    public void MidGrey_IsTheStandardGreyCode()
    {
        Assert.Equal(FrameParams.CineonGreyCode / 1023.0, LogEncoding.MidGrey, 5);
    }

    [Fact]
    public void GreyNotDenserThanBlackEnd_IsRejected()
    {
        var onBase = new[] { DMin[0], DMin[1] + 0.3, DMin[2] + 0.3 };
        Assert.Throws<ArgumentException>(
            () => DensityEndpoints.HighlightFromNeutralAtCode(onBase, DMin, FrameParams.CineonGreyCode));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DensityEndpoints.HighlightFromNeutralAtCode(Grey, DMin, 40.0));
    }

    // ── Deep-WB span step ────────────────────────────────────────────────────────

    private static readonly double[] XHigh = { 1.9, 2.0, 2.2 };

    [Fact]
    public void SpanStep_HoldsSlopeMeanExactly_AndMovesOnlyRatios()
    {
        var span = new[] { 1.96, 2.01, 2.21 };
        double target = (1 / span[0] + 1 / span[1] + 1 / span[2]) / 3.0;
        // Net wants red brighter, blue darker.
        double[] next = WhiteBalance.HighlightSpanStep(span, XHigh, new[] { 0.05, 0.0, -0.05 },
                                                       ColorPipeline.ResponseGamma, target);
        Assert.Equal(target, (1 / next[0] + 1 / next[1] + 1 / next[2]) / 3.0, 12);
        // Red brighter ⇒ its white comes earlier ⇒ smaller span relative to green; blue the reverse.
        Assert.True(next[0] / next[1] < span[0] / span[1]);
        Assert.True(next[2] / next[1] > span[2] / span[1]);
    }

    [Fact]
    public void SpanStep_IsTheNewtonStep_ForTheRenderTheNetSees()
    {
        // Under D_adj = R·x/s − R and lin = 10^(D_adj/γ), a chroma log-gain g at the highlight is
        // delivered exactly by one step when the system is linear in the small; check that the
        // rendered log-linear at x moves by γ⁻¹·ΔD_adj = g (up to the renormalisation, which is
        // common to all channels and cancels in the difference).
        var span = new[] { 2.0, 2.0, 2.0 };
        double target = 0.5;
        double g = 0.02;
        double[] next = WhiteBalance.HighlightSpanStep(span, new[] { 1.8, 1.8, 1.8 }, new[] { g, 0.0, -g },
                                                       ColorPipeline.ResponseGamma, target);
        double R = FrameParams.OutputRange, gamma = ColorPipeline.ResponseGamma;
        double LogLin(double s) => (R * 1.8 / s - R) / gamma;
        double moved = LogLin(next[0]) - LogLin(next[1]);
        double wanted = g - 0.0;
        Assert.Equal(wanted, moved, 3);
    }

    [Fact]
    public void SpanStep_IsAFixedPoint_WhenTheNetIsSatisfied()
    {
        var span = new[] { 1.96, 2.01, 2.21 };
        double target = (1 / span[0] + 1 / span[1] + 1 / span[2]) / 3.0;
        // Equal gains in every channel are brightness, not colour: nothing moves.
        double[] next = WhiteBalance.HighlightSpanStep(span, XHigh, new[] { 0.3, 0.3, 0.3 },
                                                       ColorPipeline.ResponseGamma, target);
        for (int c = 0; c < 3; c++) Assert.Equal(span[c], next[c], 9);
    }

    [Fact]
    public void SpanStep_SurvivesAWildNetOutput()
    {
        var span = new[] { 2.0, 2.0, 2.0 };
        double[] next = WhiteBalance.HighlightSpanStep(span, XHigh, new[] { 5.0, -5.0, 0.0 },
                                                       ColorPipeline.ResponseGamma, 0.5);
        foreach (double s in next) Assert.True(double.IsFinite(s) && s > 0);
        Assert.Equal(0.5, (1 / next[0] + 1 / next[1] + 1 / next[2]) / 3.0, 12);
    }
}
