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
}
