using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The exposure meter and the D_max solve that follows it — the pair that replaced "place the
/// 99.9th percentile at a fixed code" with "place the picture's average where a meter would".
/// </summary>
public class ExposureMeterTests
{
    /// <summary>
    /// A DELIBERATELY BLOWN MINORITY MUST NOT DARKEN THE SUBJECT — the reason the meter takes a
    /// median rather than a mean.
    ///
    /// A photograph routinely sacrifices part of itself to expose the subject: a window, a bright
    /// sky, a backlit edge. A mean counts those pixels at full weight, so the meter reads high and
    /// D_max is solved to bring the average back down, darkening the SUBJECT. Measured on this
    /// frame shape, the mean read +0.73 stops at 30% blown and +1.21 at 50%; the median reads zero
    /// while the blown region stays a minority.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(0.45)]
    public void A_blown_minority_does_not_move_the_reading(double blownFraction)
    {
        const double dpc = FrameParams.CineonDensityPerCode;
        static float LinAtCode(double code) =>
            (float)Math.Pow(10.0, (code - FrameParams.CineonWhiteCode) * dpc);

        // Subject sits exactly on the meter's reference; the blown region is far above it.
        float subject = LinAtCode(ExposureMeter.ReferenceCode);
        float blown = LinAtCode(700.0);

        const int n = 2000;
        int blownCount = (int)(n * blownFraction);
        var data = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            float v = i < blownCount ? blown : subject;
            data[i * 3] = v; data[i * 3 + 1] = v; data[i * 3 + 2] = v;
        }

        var (_, stops) = ExposureMeter.Measure(data);
        Assert.InRange(stops, -0.05, 0.05);
    }

    /// <summary>
    /// The counterpart: once the blown region is the MAJORITY the median follows it, and that is
    /// correct rather than a failure. A frame that is mostly sky has no exposure that serves both
    /// halves, and no statistic can invent one — the test pins the honest behaviour so nobody
    /// "fixes" it into a hidden highlight bias later.
    /// </summary>
    [Fact]
    public void A_blown_majority_does_move_the_reading()
    {
        const double dpc = FrameParams.CineonDensityPerCode;
        static float LinAtCode(double code) =>
            (float)Math.Pow(10.0, (code - FrameParams.CineonWhiteCode) * dpc);

        float subject = LinAtCode(ExposureMeter.ReferenceCode);
        float blown = LinAtCode(700.0);

        const int n = 2000;
        var data = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            float v = i < n * 0.7 ? blown : subject;
            data[i * 3] = v; data[i * 3 + 1] = v; data[i * 3 + 2] = v;
        }

        var (_, stops) = ExposureMeter.Measure(data);
        Assert.True(stops > 1.0, $"a 70% blown frame should read high, got {stops:F2}");
    }

    /// <summary>
    /// THE REFERENCE IS THE MID GREY, NOT THE DIFFUSE WHITE, and the distance between them is the
    /// standard's. Metering against 685 would read −2.32 stops on a correctly exposed frame and
    /// over-expose every roll by that much when the reading was chased back to zero.
    /// </summary>
    [Fact]
    public void Reference_is_the_mid_grey_two_and_a_third_stops_under_white()
    {
        Assert.Equal(336.0, ExposureMeter.ReferenceCode, 0);
        Assert.Equal(685.0, ExposureMeter.DiffuseWhiteCode, 0);

        double stops = (ExposureMeter.DiffuseWhiteCode - ExposureMeter.ReferenceCode)
                     * FrameParams.CineonDensityPerCode / 0.3010299956639812;
        Assert.Equal(2.32, stops, 2);
    }

    /// <summary>
    /// The meter reads the GEOMETRIC mean, so a small very bright region cannot drag the reading
    /// the way an arithmetic mean of linear light would. That is the property that makes it
    /// describe the picture rather than its brightest accident.
    /// </summary>
    [Fact]
    public void A_specular_highlight_barely_moves_the_reading()
    {
        var flat = new float[300 * 3];
        for (int i = 0; i < flat.Length; i++) flat[i] = 0.1f;
        var (baseline, _) = ExposureMeter.Measure(flat);

        // One pixel in 100 blown to full white.
        var spiked = (float[])flat.Clone();
        for (int c = 0; c < 3; c++) spiked[c] = 1.0f;
        var (withSpike, _) = ExposureMeter.Measure(spiked);

        // A linear mean would jump ~30 codes here; the log mean moves a few.
        Assert.True(withSpike - baseline < 12.0,
                    $"reading moved {withSpike - baseline:F1} codes on one blown pixel");
    }

    /// <summary>
    /// The solve is closed form and lands on the target in ONE step, for any starting endpoints.
    /// </summary>
    [Theory]
    [InlineData(587.9, 2.2, 0.09)]
    [InlineData(410.3, 2.2, 0.09)]
    [InlineData(597.6, 1.6, 0.09)]
    public void Solving_places_the_average_on_the_reference(double measured, double dMax, double dMin)
    {
        var max = new[] { dMax, dMax, dMax };
        var min = new[] { dMin, dMin, dMin };
        double[] solved = ExposureMeter.SolveDMaxForAverage(measured, max, min);

        // Re-derive where the average now lands, by the same affine map DensityEndpoints applies.
        var ep = DensityEndpoints.FromMeasured(solved, FrameParams.OutputRange, min);
        // The density the measured code corresponded to under the OLD endpoints.
        var was = DensityEndpoints.FromMeasured(max, FrameParams.OutputRange, min);
        double density = was.Invert(0, (measured - FrameParams.CineonWhiteCode)
                                     * FrameParams.CineonDensityPerCode);
        double nowCode = FrameParams.CineonWhiteCode
                       + ep.Apply(0, density) / FrameParams.CineonDensityPerCode;

        Assert.Equal(ExposureMeter.ReferenceCode, nowCode, 1);
    }

    /// <summary>
    /// The solve rescales all three spans by ONE ratio, so the roll's colour balance — which lives
    /// in the differences between the spans — survives. It is a placement, not a grade.
    /// </summary>
    [Fact]
    public void Solving_preserves_the_channel_balance()
    {
        var max = new[] { 2.2, 2.3, 2.8 };
        var min = new[] { 0.09, 0.29, 0.54 };
        double[] solved = ExposureMeter.SolveDMaxForAverage(500.0, max, min);

        double r0 = (max[0] - min[0]) / (max[1] - min[1]);
        double r1 = (solved[0] - min[0]) / (solved[1] - min[1]);
        Assert.Equal(r0, r1, 6);

        double s0 = (max[2] - min[2]) / (max[1] - min[1]);
        double s1 = (solved[2] - min[2]) / (solved[1] - min[1]);
        Assert.Equal(s0, s1, 6);
    }

    /// <summary>A frame with nothing above the black end carries no exposure to place; the
    /// endpoints must be left alone rather than solved against a degenerate ratio.</summary>
    [Fact]
    public void A_black_frame_leaves_the_endpoints_untouched()
    {
        var max = new[] { 2.2, 2.3, 2.8 };
        var min = new[] { 0.09, 0.29, 0.54 };
        double[] solved = ExposureMeter.SolveDMaxForAverage(FrameParams.CineonBlackCode, max, min);
        Assert.Equal(max, solved);

        var (code, stops) = ExposureMeter.Measure(new float[9]);
        Assert.Equal(FrameParams.CineonBlackCode, code, 3);
        Assert.True(double.IsNaN(stops));
    }

    /// <summary>
    /// A WIDE FILM-BASE BORDER MUST NOT MOVE THE READING. Bare base is the darkest thing in the
    /// positive, so sprocket rows and rebate are a block of near-minimum values inside the frame;
    /// left in, they drag the geometric mean down in proportion to how much of the scan is border.
    /// The error is self-reinforcing — a reading that is too dark solves D_max too low, which
    /// over-exposes the picture.
    /// </summary>
    [Fact]
    public void A_film_base_border_does_not_drag_the_reading()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);

        // 100 picture pixels at a mid tone.
        var picture = new float[100 * 3];
        for (int i = 0; i < picture.Length; i++) picture[i] = 0.2f;
        var (clean, _) = ExposureMeter.Measure(picture);

        // The same picture with an equally large border of bare base — and the base is NOT one
        // value: it is spread over the range an uneven scan produces.
        var rng = new Random(7);
        var bordered = new float[200 * 3];
        Array.Copy(picture, bordered, picture.Length);
        for (int p = 100; p < 200; p++)
        {
            // Base plus up to 0.10 density of variation — typical unevenness.
            float v = floor * (float)Math.Pow(10.0, rng.NextDouble() * 0.10);
            for (int c = 0; c < 3; c++) bordered[p * 3 + c] = v;
        }
        var (withBorder, _) = ExposureMeter.Measure(bordered);

        Assert.Equal(clean, withBorder, 1);
    }

    /// <summary>
    /// The cut clears the base's own spread, not merely the nominal floor D_min was calibrated
    /// to. Cutting at the floor would keep the base's brightest pixels — which is most of what a
    /// wide border contributes.
    /// </summary>
    [Fact]
    public void The_cut_clears_a_realistic_base_spread()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);

        // Base at the top of a 0.15-density spread is still base and must be excluded.
        var highBase = new float[30 * 3];
        for (int i = 0; i < highBase.Length; i++)
            highBase[i] = floor * (float)Math.Pow(10.0, 0.15);
        var (code, stops) = ExposureMeter.Measure(highBase);

        Assert.Equal(FrameParams.CineonBlackCode, code, 3);
        Assert.True(double.IsNaN(stops), "an all-base frame has no exposure to report");
    }
}
