using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Path A hands the inversion a chroma-compensation matrix that Path B never has, and the one
/// thing that matrix must not do is move the two calibrated ends off neutral. It did: applied to
/// the RAW density chroma — which carries the orange mask and the layers' unequal contrast, not
/// scene colour — it compressed the white point's own cast and rendered a calibrated white as
/// R 1.59 / G 1.00 / B 0.72 at a typical narrow-band amp. The whole roll came out yellow-red and
/// no D-max measurement could fix it, because the render was not honouring the endpoints being
/// measured. These pin the contract the endpoint model states: dMin_c → black and dMax_c → white
/// for EVERY channel, matrix or no matrix.
/// </summary>
public class PathAChromaMatrixTests
{
    // A C-41 negative under narrow-band light: orange base, blue densest at both ends.
    private static readonly double[] DMin = { 0.09, 0.29, 0.54 };
    private static readonly double[] DMax = { 1.25, 1.55, 1.85 };

    private static float T(double density) => (float)System.Math.Pow(10.0, -density);

    private static FrameParams Params() => new()
    {
        DMinPerChannel = (double[])DMin.Clone(),
        DMaxPerChannel = (double[])DMax.Clone(),
    };

    /// <summary>C = B·diag(1/amp)·Bᵀ over the sum-zero plane — the shape ChromaAxisCompensationMatrix
    /// produces, built directly so the test does not depend on measuring it off an image.</summary>
    private static double[,] Compensation(double ampYb, double ampRg)
    {
        double s6 = System.Math.Sqrt(6.0), s2 = System.Math.Sqrt(2.0);
        double[] yb = { 1 / s6, 1 / s6, -2 / s6 };
        double[] rg = { 1 / s2, -1 / s2, 0 };
        var c = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = yb[i] * (1 / ampYb) * yb[j] + rg[i] * (1 / ampRg) * rg[j];
        return c;
    }

    /// <summary>One-pixel frame at the given per-channel densities, rendered through Invert.</summary>
    private static float[] Render(double[] density, FrameParams cal, double[,]? matrix, double[]? amp = null)
    {
        var img = new ImageBuffer(1, 1, new[] { T(density[0]), T(density[1]), T(density[2]) });
        return Inversion.Invert(img, cal, amp, matrix).Data;
    }

    private static void AssertNeutral(float[] px, double expected, string what)
    {
        // Relative, and loose enough for the 16-bit density LUT: the test transmittances are not
        // exactly representable at 16 bits, and the rounding is a few 1e-4 in linear light.
        for (int c = 0; c < 3; c++)
            Assert.True(System.Math.Abs(px[c] - expected) < 3e-3 * expected,
                $"{what}: channel {c} rendered {px[c]:F5}, expected {expected:F5} (all three must agree)");
    }

    [Theory]
    [InlineData(1.3, 1.3)]
    [InlineData(1.6, 1.6)]
    [InlineData(2.0, 1.4)]
    public void Calibrated_white_stays_neutral_under_a_chroma_matrix(double ampYb, double ampRg)
    {
        float[] px = Render(DMax, Params(), Compensation(ampYb, ampRg));
        // dMax_c → adjusted density 0 → linear 1.0, for every channel.
        AssertNeutral(px, 1.0, $"white, amp {ampYb}/{ampRg}");
    }

    [Theory]
    [InlineData(1.3, 1.3)]
    [InlineData(1.6, 1.6)]
    [InlineData(2.0, 1.4)]
    public void Calibrated_black_stays_neutral_under_a_chroma_matrix(double ampYb, double ampRg)
    {
        float[] px = Render(DMin, Params(), Compensation(ampYb, ampRg));
        AssertNeutral(px, System.Math.Pow(10.0, -FrameParams.OutputRange), $"black, amp {ampYb}/{ampRg}");
    }

    /// <summary>
    /// Every grey between the ends is neutral too: a tone half-way along each channel's own span
    /// is what a neutral scene grey is on a two-point-calibrated negative, and the matrix has no
    /// business touching it.
    /// </summary>
    [Fact]
    public void Mid_grey_stays_neutral_under_a_chroma_matrix()
    {
        var mid = new double[3];
        for (int c = 0; c < 3; c++) mid[c] = (DMin[c] + DMax[c]) / 2.0;
        float[] px = Render(mid, Params(), Compensation(1.6, 1.6));
        AssertNeutral(px, System.Math.Pow(10.0, -FrameParams.OutputRange / 2.0), "mid grey");
    }

    /// <summary>The per-channel amp path (CLI without a matrix) has the same obligation.</summary>
    [Fact]
    public void Calibrated_ends_stay_neutral_under_per_channel_amp()
    {
        var amp = new[] { 1.4, 1.7, 2.1 };
        AssertNeutral(Render(DMax, Params(), null, amp), 1.0, "white, amp");
        AssertNeutral(Render(DMin, Params(), null, amp), System.Math.Pow(10.0, -FrameParams.OutputRange), "black, amp");
    }

    /// <summary>
    /// And the matrix still does its job on scene colour: a coloured tone keeps its luminance
    /// (mean adjusted density) and has its chroma shrunk by the matrix, relative to the same tone
    /// rendered without one.
    /// </summary>
    [Fact]
    public void Scene_chroma_is_compressed_while_luminance_is_kept()
    {
        // A tone off the neutral axis: red layer denser than its grey-equivalent, blue lighter.
        var tone = new[] { 0.95, 0.92, 0.85 };
        float[] plain = Render(tone, Params(), null);
        float[] comp = Render(tone, Params(), Compensation(1.6, 1.6));

        static double[] LogOf(float[] px) => new[]
        {
            System.Math.Log10(px[0]), System.Math.Log10(px[1]), System.Math.Log10(px[2]),
        };
        double[] lp = LogOf(plain), lc = LogOf(comp);
        double meanP = (lp[0] + lp[1] + lp[2]) / 3, meanC = (lc[0] + lc[1] + lc[2]) / 3;
        Assert.Equal(meanP, meanC, 4);

        static double ChromaNorm(double[] l, double mean)
        {
            double a = l[0] - mean, b = l[1] - mean, c = l[2] - mean;
            return System.Math.Sqrt(a * a + b * b + c * c);
        }
        double np = ChromaNorm(lp, meanP), nc = ChromaNorm(lc, meanC);
        Assert.True(np > 1e-3, "the test tone must actually be coloured");
        Assert.Equal(np / 1.6, nc, 4);   // isotropic 1/amp on both axes → chroma shrinks by exactly 1/amp
    }
}
