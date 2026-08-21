using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The highlight endpoint has to satisfy two things at once, and they pull against each other:
/// the three channels must stay BALANCED (co-sited, from one physical highlight) and no channel
/// may CLIP (every pixel's density below its own channel's endpoint, which is its divisor).
/// </summary>
public class DMaxEndpointTests
{
    private static float T(double density) => (float)System.Math.Pow(10.0, -density);

    /// <summary>Builds a frame from populations of (r,g,b) densities, filling the rest with 1.2.</summary>
    private static ImageBuffer Frame(params (double R, double G, double B, int Count)[] pops)
    {
        const int w = 200, h = 200, n = w * h;
        var data = new float[n * 3];
        int i = 0;
        foreach (var p in pops)
            for (int j = 0; j < p.Count && i < n; j++, i++)
            {
                data[i * 3] = T(p.R); data[i * 3 + 1] = T(p.G); data[i * 3 + 2] = T(p.B);
            }
        for (; i < n; i++) { data[i * 3] = T(1.2); data[i * 3 + 1] = T(1.2); data[i * 3 + 2] = T(1.2); }
        return new ImageBuffer(w, h, data);
    }

    private static double[] Detect(ImageBuffer f) =>
        FilmBase.DetectDMaxPerChannelFromRoll(
            new[] { f }, new[] { 1.0, 1.0, 1.0 }, 90.0, new[] { f }, null, edgeInset: 0.0)!;

    /// <summary>
    /// A TINTED HIGHLIGHT MUST NOT CLIP ITS OWN CHANNEL.
    ///
    /// The co-sited triplet is measured on pixels ranked by TOTAL density. A sodium lamp, a
    /// sunset or red neon can be far denser in one channel while its total ranks below that tail,
    /// so before the uniform rescale that channel's density exceeded the endpoint it is divided
    /// by, and it clipped — one channel blown while the other two were fine.
    /// </summary>
    [Fact]
    public void No_channel_exceeds_its_endpoint()
    {
        // Neutral bright area defines the tail; the red-tinted population has a LOWER total
        // density (2.47 against 2.60) so it never reaches it, yet its red is the frame's densest.
        double[] hi = Detect(Frame((2.60, 2.60, 2.60, 40), (2.90, 2.30, 2.20, 80)));

        Assert.True(2.90 <= hi[0] + 1e-6, $"red must not clip: max 2.90 against endpoint {hi[0]}");
        Assert.True(2.60 <= hi[1] + 1e-6, $"green must not clip: max 2.60 against endpoint {hi[1]}");
        Assert.True(2.60 <= hi[2] + 1e-6, $"blue must not clip: max 2.60 against endpoint {hi[2]}");
    }

    /// <summary>
    /// THE RESCALE MUST NOT DISTURB THE COLOUR BALANCE. It is ONE factor on all three, so every
    /// ratio between them is preserved exactly.
    ///
    /// Taking three independent per-channel maxima instead would draw R, G and B from three
    /// different pixels and produce a triplet no negative ever made — a cast baked into the
    /// inversion that no Stage-2 control can remove. The two frames here share a co-sited
    /// highlight and differ only in whether a second population forces a rescale; their endpoint
    /// RATIOS must come out identical.
    /// </summary>
    [Fact]
    public void The_rescale_preserves_the_channel_balance()
    {
        var tinted = (2.60, 2.40, 2.10, 40);

        double[] noOvershoot = Detect(Frame(tinted));
        double[] withOvershoot = Detect(Frame(tinted, (2.30, 2.20, 2.55, 80)));

        Assert.Equal(noOvershoot[0] / noOvershoot[1], withOvershoot[0] / withOvershoot[1], 6);
        Assert.Equal(noOvershoot[2] / noOvershoot[1], withOvershoot[2] / withOvershoot[1], 6);

        // And the rescale actually happened — blue was the offender, so it now sits on its max.
        Assert.True(withOvershoot[2] > noOvershoot[2]);
        Assert.True(2.55 <= withOvershoot[2] + 1e-6);
    }

    /// <summary>A frame whose channels all sit under the co-sited triplet is left alone: the
    /// factor is never less than 1, so a well-behaved frame is not darkened for nothing.</summary>
    [Fact]
    public void A_frame_with_no_overshoot_is_not_rescaled()
    {
        double[] hi = Detect(Frame((2.60, 2.40, 2.10, 40)));

        Assert.Equal(2.60, hi[0], 4);
        Assert.Equal(2.40, hi[1], 4);
        Assert.Equal(2.10, hi[2], 4);
    }

    // ── The shared no-clip rescale, as 智能白平衡 uses it ────────────────────────────
    //
    // 智能白平衡 solves the highlight BALANCE with the Deep-WB net and then has to land on an
    // endpoint triple that still clears the frame. It reuses the detector's own two pieces —
    // MaxChannelDensityFromRoll and RescaleToClearChannelMax — rather than carrying a second copy
    // of the arithmetic, because the copy that drifted would clip silently.

    /// <summary>
    /// The extracted rescale is the SAME function the detector applies to its own answer: run it
    /// by hand on the co-sited triple and the detector's output must match to the last bit.
    /// This is what pins the refactor — the detector's behaviour did not change when the block
    /// became a shared method.
    /// </summary>
    [Fact]
    public void The_shared_rescale_reproduces_the_detectors_own_answer()
    {
        ImageBuffer f = Frame((2.60, 2.40, 2.10, 40), (2.30, 2.20, 2.55, 80));

        double[] detected = Detect(f);
        double[] chanMax = FilmBase.MaxChannelDensityFromRoll(
            new[] { f }, new[] { 1.0, 1.0, 1.0 }, new[] { f }, null, edgeInset: 0.0);
        // The co-sited triple before the lift, taken from the frame with the overshoot removed.
        double[] coSited = Detect(Frame((2.60, 2.40, 2.10, 40)));
        double[] rescaled = FilmBase.RescaleToClearChannelMax(coSited, chanMax);

        Assert.Equal(detected[0], rescaled[0], 6);
        Assert.Equal(detected[1], rescaled[1], 6);
        Assert.Equal(detected[2], rescaled[2], 6);
    }

    /// <summary>
    /// A triple the net walked BELOW the frame's per-channel maxima is lifted back until it
    /// clears them — the guarantee 智能白平衡 used to drop entirely once its loop had moved the
    /// channels apart from where the calibration left them.
    /// </summary>
    [Fact]
    public void A_solved_triple_below_the_channel_maxima_is_lifted_clear()
    {
        var chanMax = new[] { 2.90, 2.60, 2.60 };
        var solved = new[] { 2.20, 2.00, 1.95 };          // net's balance, placed too low

        double[] safe = FilmBase.RescaleToClearChannelMax(solved, chanMax);

        Assert.True(chanMax[0] <= safe[0] + 1e-9, $"red clips: {chanMax[0]} vs {safe[0]}");
        Assert.True(chanMax[1] <= safe[1] + 1e-9, $"green clips: {chanMax[1]} vs {safe[1]}");
        Assert.True(chanMax[2] <= safe[2] + 1e-9, $"blue clips: {chanMax[2]} vs {safe[2]}");

        // ONE factor: the balance the net solved survives the lift untouched.
        Assert.Equal(solved[0] / solved[1], safe[0] / safe[1], 9);
        Assert.Equal(solved[2] / solved[1], safe[2] / safe[1], 9);
    }

    /// <summary>
    /// THE CHROMA-ONLY STEP MUST NOT MOVE THE BRIGHTNESS.
    ///
    /// This is the arithmetic of 智能白平衡's inner loop in miniature. The delta is zero-sum across
    /// channels, which would be chroma-only if it were ADDED; applied multiplicatively against
    /// three unequal endpoints it is not, because mean(ep·(1-d)) = mean(ep) - mean(ep·d) and
    /// mean(ep·d) vanishes only when the endpoints are equal. The loop therefore leaked brightness
    /// every round and compounded it over 50 — the overexposure this test exists to catch.
    ///
    /// The renormalisation pins the mean endpoint and touches no ratio, so the step becomes what
    /// it always claimed to be.
    /// </summary>
    [Fact]
    public void The_chroma_only_step_leaves_the_mean_endpoint_where_it_was()
    {
        var ep = new[] { 1.20, 1.00, 0.85 };              // deliberately unequal — equal hides it
        double meanStart = (ep[0] + ep[1] + ep[2]) / 3.0;

        for (int it = 0; it < 50; it++)
        {
            var d = new[] { 0.02, -0.005, -0.015 };       // zero-sum chroma delta
            Assert.Equal(0.0, d[0] + d[1] + d[2], 12);

            double meanBefore = (ep[0] + ep[1] + ep[2]) / 3.0;
            var before = (double[])ep.Clone();
            for (int c = 0; c < 3; c++) ep[c] = System.Math.Max(ep[c] * (1.0 - d[c]), 1e-3);

            double meanAfter = (ep[0] + ep[1] + ep[2]) / 3.0;
            double renorm = meanBefore / meanAfter;
            for (int c = 0; c < 3; c++) ep[c] = System.Math.Max(ep[c] * renorm, 1e-3);

            // Brightness pinned every round, not merely on average over the run.
            Assert.Equal(meanBefore, (ep[0] + ep[1] + ep[2]) / 3.0, 9);
            // And the round still did its job: the requested chroma move survived the renorm.
            Assert.True(ep[0] / ep[1] < before[0] / before[1]);
        }

        Assert.Equal(meanStart, (ep[0] + ep[1] + ep[2]) / 3.0, 9);
    }
}
