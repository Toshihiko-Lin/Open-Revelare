using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// What the shadow-quantisation guard must and must NOT do to an endpoint estimate.
///
/// The guard rejects samples whose density is mostly quantisation — near black one 8-bit code is
/// 0.301 D, and the lowest code always reports the HIGHEST density, so those samples drag an
/// endpoint up without carrying any information. That is a narrow claim, and these tests pin its
/// LIMITS as much as its effect: it is a safety net for data that has run into its own bit depth,
/// not a general improvement, and it must be invisible everywhere else.
///
/// The synthetic negatives here carry a KNOWN endpoint, so the estimate can be checked against
/// truth rather than against a previous run.
/// </summary>
public class QuantisationGuardTests
{
    /// <summary>
    /// A negative whose scene density ramps from the film base to <paramref name="trueDMax"/>,
    /// quantised to <paramref name="bits"/>. <paramref name="crushCodes"/> models what a scanner
    /// does to a channel it under-exposes: every code at or below it is written as 0, and that
    /// information is gone from the file.
    /// </summary>
    private static ImageBuffer Negative(int bits, double[] trueDMax, int crushCodes = 0,
                                        int w = 300, int h = 300)
    {
        var img = new ImageBuffer(w, h);
        double codeMax = (1 << bits) - 1;
        for (int p = 0; p < w * h; p++)
        {
            double u = (double)p / (w * h);
            for (int c = 0; c < 3; c++)
            {
                double code = Math.Round(Math.Pow(10.0, -u * trueDMax[c]) * codeMax);
                if (c == 2 && code <= crushCodes) code = 0;
                img.Data[p * 3 + c] = (float)(code / codeMax);
            }
        }
        img.SourceQuantisationStep = 1.0 / codeMax;
        return img;
    }

    private static double[] Endpoint(ImageBuffer img) =>
        FilmBase.DetectDMaxPerChannelFromRoll(
            new[] { img }, new[] { 1.0, 1.0, 1.0 }, 90.0, new[] { img }, null, edgeInset: 0.0)!;

    private static double MaxError(double[] got, double[] truth)
    {
        double e = 0;
        for (int c = 0; c < 3; c++) e = Math.Max(e, Math.Abs(got[c] - truth[c]));
        return e;
    }

    /// <summary>
    /// AN ORDINARY 8-BIT SCAN IS LEFT ALONE. At normal film densities the darkest sample still
    /// sits several codes above zero, clear of the quantisation cliff, so the guard must not
    /// reject anything and the endpoint must come back close to truth.
    ///
    /// This is the test that would fail first if someone tightened the threshold too far: the
    /// guard's licence is limited to data that has actually run into its bit depth.
    /// </summary>
    [Fact]
    public void An_ordinary_8bit_negative_is_estimated_accurately()
    {
        var truth = new[] { 1.5, 1.7, 1.9 };
        Assert.True(MaxError(Endpoint(Negative(8, truth)), truth) < 0.1);
    }

    /// <summary>
    /// SIXTEEN-BIT IS EXACT, crushed or not. Its step is fine enough that the uncertainty never
    /// approaches the threshold, so the guard is a no-op by construction — which is why the test
    /// for the step lives on the transfer curve and not on the bit count.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(800)]
    public void A_16bit_negative_is_unaffected(int crushCodes)
    {
        var truth = new[] { 1.5, 1.7, 1.9 };
        Assert.True(MaxError(Endpoint(Negative(16, truth, crushCodes)), truth) < 0.05);
    }

    /// <summary>
    /// THE GUARD DOES NOT FABRICATE WHAT THE FILE LOST. With the blue channel crushed deeply the
    /// estimate is wrong no matter what — the samples simply are not there. Pinned so nobody reads
    /// the guard as a repair for a bad scan: the fix for this file is to rescan it.
    /// </summary>
    [Fact]
    public void A_deeply_crushed_channel_cannot_be_recovered()
    {
        var truth = new[] { 1.5, 1.7, 1.9 };
        double[] got = Endpoint(Negative(8, truth, crushCodes: 8));
        Assert.True(Math.Abs(got[2] - truth[2]) > 0.2,
            $"blue came back at {got[2]:F3} against a truth of {truth[2]:F3} — if this now passes, "
            + "the guard is doing more than rejecting unresolved samples and the claim needs revisiting");
    }

    /// <summary>
    /// THE HEALTHY CHANNELS MUST NOT MOVE when another one is crushed. The endpoints are
    /// per-channel divisors, so a guard that quietly shifted red or green while cleaning up blue
    /// would trade one cast for another.
    /// </summary>
    [Fact]
    public void Crushing_blue_does_not_disturb_red_and_green()
    {
        var truth = new[] { 1.5, 1.7, 1.9 };
        double[] clean = Endpoint(Negative(8, truth));
        double[] crushed = Endpoint(Negative(8, truth, crushCodes: 3));

        Assert.Equal(clean[0], crushed[0], 2);
        Assert.Equal(clean[1], crushed[1], 2);
    }
}
