using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The positive white end has to come from the only part of the strip that is clear film by
/// construction: the perforations. These tests build a synthetic 135 strip with two hole rows and
/// a deliberately brighter, coloured patch of picture, so the two candidates are distinguishable
/// in one frame.
/// </summary>
public class PositivePerforationTests
{
    private const int W = 1200, H = 700;
    private const double PxPerMm = 12.0;

    /// <summary>
    /// A horizontal 135 strip: 35 mm of film at <see cref="PxPerMm"/>, two perforation rows on the
    /// 28.17 mm centres, a 24 mm image area, and optionally a brighter COLOURED block inside the
    /// picture. The block is smaller than 1% of the frame so it does not move the detector's 99th
    /// percentile, but it is brighter than the holes, which is exactly the trap the old
    /// picture-based estimator fell into.
    /// </summary>
    internal static ImageBuffer Strip(double holeLevel,
                                     (double R, double G, double B)? brightBlock = null,
                                     double bridgeLevel = 0.004)
    {
        var d = new float[W * H * 3];
        void Put(int x, int y, (double R, double G, double B) t)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return;
            int i = (y * W + x) * 3;
            d[i] = (float)t.R; d[i + 1] = (float)t.G; d[i + 2] = (float)t.B;
        }

        double centre = H / 2.0;
        double filmHalf = 35.0 / 2 * PxPerMm;
        double imageHalf = 24.0 / 2 * PxPerMm;
        double rowOffset = 28.17 / 2 * PxPerMm;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dy = Math.Abs(y - centre);
                (double R, double G, double B) t = dy <= filmHalf
                    ? dy <= imageHalf ? (0.15, 0.15, 0.15)   // picture: neutral midtone
                                      : (bridgeLevel, bridgeLevel, bridgeLevel) // unexposed rebate
                    : (0.004, 0.004, 0.004);                 // dark surround
                Put(x, y, t);
            }

        // A brighter, strongly coloured patch of picture content. It is inside the image area, so
        // the perforation mask must never see it.
        if (brightBlock is { } block)
        {
            for (int y = 280; y < 370; y++)
                for (int x = 750; x < 840; x++)
                    Put(x, y, block);
        }

        // Two rows of twelve holes on the 4.75 mm pitch.
        foreach (double rowCentre in new[] { centre - rowOffset, centre + rowOffset })
            for (int k = 0; k < 12; k++)
            {
                double xc = 140 + k * 4.75 * PxPerMm;
                for (int y = (int)Math.Round(rowCentre - 1.0 * PxPerMm);
                     y <= (int)Math.Round(rowCentre + 1.0 * PxPerMm); y++)
                    for (int x = (int)Math.Round(xc - 1.4 * PxPerMm);
                         x <= (int)Math.Round(xc + 1.4 * PxPerMm); x++)
                        Put(x, y, (holeLevel, holeLevel, holeLevel));
            }
        return new ImageBuffer(W, H, d);
    }

    /// <summary>A frame with no perforation structure at all: the fallback case.</summary>
    private static ImageBuffer NoPerforations(double level = 0.5)
    {
        var d = new float[W * H * 3];
        Array.Fill(d, (float)level);
        return new ImageBuffer(W, H, d);
    }

    /// <summary>Turn the horizontal strip upright, exercising the detector's vertical branch.</summary>
    private static ImageBuffer Transpose(ImageBuffer src)
    {
        int nw = src.Height, nh = src.Width;
        var d = new float[nw * nh * 3];
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int si = (y * src.Width + x) * 3;
                int di = (x * nw + y) * 3;
                d[di] = src.Data[si]; d[di + 1] = src.Data[si + 1]; d[di + 2] = src.Data[si + 2];
            }
        return new ImageBuffer(nw, nh, d);
    }

    [Fact]
    public void Ruler_AndWhiteEnd_WorkForAVerticalStrip()
    {
        ImageBuffer frame = Transpose(Strip(0.60));

        var ruler = FilmPerforations.TryGetRuler(frame);
        Assert.NotNull(ruler);
        Assert.False(ruler!.Value.Horizontal);
        Assert.InRange(ruler.Value.ScalePxPerMm, 11.5, 12.5);
        Assert.InRange(ruler.Value.AcrossPxPerMm, 11.5, 12.5);
        Assert.InRange(ruler.Value.CentrePx, frame.Width / 2.0 - 2, frame.Width / 2.0 + 2);

        double[]? white = FilmBase.EstimateWhiteEndFromPerforations(new[] { frame });
        Assert.NotNull(white);
        foreach (double v in white!) Assert.InRange(v, 0.59, 0.61);
    }

    [Fact]
    public void Ruler_IsFoundOnASyntheticStrip()
    {
        var ruler = FilmPerforations.TryGetRuler(Strip(0.60));
        Assert.NotNull(ruler);
        PerforationRuler r = ruler!.Value;
        Assert.True(r.Horizontal);
        Assert.InRange(r.ScalePxPerMm, 11.5, 12.5);
        Assert.InRange(r.AcrossPxPerMm, 11.5, 12.5);
        Assert.InRange(r.CentrePx, H / 2.0 - 2, H / 2.0 + 2);
        Assert.InRange(r.Angle, -0.2, 0.2);
    }

    /// <summary>
    /// THE GUARD FOR THIS CHANGE. The frame's brightest patch is coloured picture content, while
    /// the holes are a darker neutral. The white end must be the holes: a neutral triple at their
    /// level, not the brighter coloured block. A global bright-tail sampler (the old picture
    /// method, or a perforation mask that leaked outside the hole bands) returns the block and
    /// fails here.
    /// </summary>
    [Fact]
    public void WhiteEnd_ComesFromThePerforations_NotTheBrighterPicture()
    {
        ImageBuffer frame = Strip(0.60, brightBlock: (1.00, 0.90, 0.70));

        // The fixture really does contain a brighter coloured candidate: the old estimator, which
        // looks at the picture's bright tail, must see it. Without this half the test would still
        // pass if the block were not bright at all.
        double[] old = FilmBase.EstimatePositiveWhiteEndFromRoll(new[] { frame })!;
        Assert.True(old[0] > 0.90, $"fixture must put a bright block in the picture, got {Fmt(old)}");
        Assert.True(old.Max() / Math.Max(1e-9, old.Min()) > 1.15,
            $"fixture's picture candidate must be coloured, got {Fmt(old)}");

        double[]? white = FilmBase.EstimateWhiteEndFromPerforations(new[] { frame });

        Assert.NotNull(white);
        // Neutral, at the hole level (0.60), and nowhere near the block.
        foreach (double v in white!)
            Assert.InRange(v, 0.59, 0.61);
        Assert.True(white.Max() / white.Min() < 1.02,
            $"white end must be the neutral hole light, got {Fmt(white)}");
        Assert.True(white[0] < old[0] - 0.10,
            $"white end took the brighter picture block: holes would be 0.60, got {Fmt(white)} vs old {Fmt(old)}");
    }

    /// <summary>
    /// No holes in view (a 3:2 crop, or a holder that hid the edges): the perforation estimator
    /// must decline instead of throwing, and the old picture path must still answer. This is the
    /// fallback the view model takes.
    /// </summary>
    [Fact]
    public void WhiteEnd_WithoutPerforations_ReturnsNullAndLeavesThePicturePathUsable()
    {
        ImageBuffer plain = NoPerforations(0.5);

        Assert.Null(FilmBase.EstimateWhiteEndFromPerforations(new[] { plain }));
        Assert.Null(FilmBase.EstimateBlackSpanFromPerforations(new[] { plain }));

        double[]? picture = FilmBase.EstimatePositiveWhiteEndFromRoll(new[] { plain });
        Assert.NotNull(picture);
        Assert.All(picture!, v => Assert.InRange(v, 0.49, 0.51));
    }

    /// <summary>
    /// The roll answer is a median, so one overexposed or hole-less first frame cannot decide it.
    /// This is the "leader frame drags the whole roll" failure the old per-frame clamp had; a
    /// first-frame implementation (rather than a pooled one) fails here.
    /// </summary>
    [Fact]
    public void WhiteEnd_RollMedian_IgnoresAnOverexposedFirstFrame()
    {
        var frames = new[]
        {
            Strip(1.5),   // clipped leader: its own measurement is 1.0
            Strip(0.60), Strip(0.60), Strip(0.60), Strip(0.60),
        };

        double[] roll = FilmBase.EstimateWhiteEndFromPerforations(frames)!;
        foreach (double v in roll) Assert.InRange(v, 0.59, 0.61);
    }

    [Fact]
    public void WhiteEnd_RollMedian_IgnoresAFrameWithoutPerforations()
    {
        var frames = new[]
        {
            NoPerforations(),
            Strip(0.60), Strip(0.60), Strip(0.60), Strip(0.60),
        };

        double[] roll = FilmBase.EstimateWhiteEndFromPerforations(frames)!;
        foreach (double v in roll) Assert.InRange(v, 0.59, 0.61);
    }

    /// <summary>
    /// A hole may clip the sensor; the white end is still a transmittance and may not exceed
    /// unity. Removing either clamp in FilmPerforations.MeasureWhiteEnd or
    /// FilmBase.ClampToPhysicalBase lets this fixture return 2.0.
    /// </summary>
    [Fact]
    public void WhiteEnd_ClippedPerforations_StayInsideThePhysicalRange()
    {
        double[]? white = FilmBase.EstimateWhiteEndFromPerforations(new[] { Strip(2.0) });

        Assert.NotNull(white);
        Assert.All(white!, v => Assert.True(v > 0 && v <= 1.0,
            $"white end must be in (0, 1], got {Fmt(white)}"));
        Assert.All(white!, v => Assert.True(-Math.Log10(v) >= 0, "D_min must not go negative"));
    }

    /// <summary>An all-clipped hole is legal: white is the sensor's ceiling, exactly 1.0.</summary>
    [Fact]
    public void WhiteEnd_FullyClippedPerforations_AreNeutralFullTransmission()
    {
        double[]? white = FilmBase.EstimateWhiteEndFromPerforations(new[] { Strip(3.0) });

        Assert.NotNull(white);
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, white!);
    }

    /// <summary>
    /// THE GUARD FOR THE BLACK END. The picture's bright block is still there, but the black end
    /// must come from the unexposed film BETWEEN the holes: a neutral span of log10(0.60/0.004) =
    /// 2.176 D below the white end. Taking the darkest picture content (0.15) would give 0.602 D;
    /// letting the hole lights into the complement gives a span near zero. Neither can pass.
    /// </summary>
    [Fact]
    public void BlackEnd_ComesFromTheUnexposedBridge_NotThePicture()
    {
        ImageBuffer frame = Strip(0.60, brightBlock: (1.00, 0.90, 0.70));

        double[]? span = FilmBase.EstimateBlackSpanFromPerforations(new[] { frame });
        Assert.NotNull(span);
        foreach (double v in span!) Assert.InRange(v, 2.15, 2.20);
        Assert.True(span.Max() / span.Min() < 1.02,
            $"unexposed bridge must be neutral, got {Fmt(span)}");

        double[] white = FilmBase.EstimateWhiteEndFromPerforations(new[] { frame })!;
        for (int c = 0; c < 3; c++)
        {
            double dMin = -Math.Log10(white[c]);
            double dMax = dMin + span[c];
            Assert.True(dMax > dMin, "D_max must be deeper than D_min");
            double t = Math.Pow(10, -dMax);
            Assert.True(t > 0 && t <= 1.0, $"black transmittance must be in (0, 1], got {t}");
            Assert.InRange(t, 0.0039, 0.0041);
        }
    }

    /// <summary>
    /// A band whose bridge is lifted by the light table (t = 0.20 against holes at 0.60) is not an
    /// unexposed black: span 0.477 D, far below the 2 D floor. The perforation estimator must
    /// refuse it and leave the old scene detector usable, rather than report 0.20 as D_max.
    /// </summary>
    [Fact]
    public void BlackEnd_ContaminatedBridge_IsRefusedInsteadOfInvented()
    {
        ImageBuffer frame = Strip(0.60, bridgeLevel: 0.20);

        Assert.Null(FilmPerforations.MeasureBlackSpan(frame));
        Assert.Null(FilmBase.EstimateBlackSpanFromPerforations(new[] { frame }));

        // The fallback path in the view model is the old scene detector; it must still answer.
        double[] white = FilmBase.EstimateWhiteEndFromPerforations(new[] { frame })!;
        HighlightEndpointEstimate? old = FilmBase.DetectDMaxPerChannelFromRollDetailed(
            new[] { frame }, white, 90.0, new[] { frame }, null,
            FilmBase.TightBorderInsetForMeasurement, protectIndependentChannelExtrema: true);
        Assert.NotNull(old);
    }

    /// <summary>One contaminated frame does not vote; the valid frames keep the roll's span.</summary>
    [Fact]
    public void BlackEnd_RollPooling_SkipsAContaminatedFrame()
    {
        var frames = new[]
        {
            Strip(0.60, bridgeLevel: 0.20),
            Strip(0.60), Strip(0.60), Strip(0.60),
        };

        double[]? span = FilmBase.EstimateBlackSpanFromPerforations(frames);
        Assert.NotNull(span);
        foreach (double v in span!) Assert.InRange(v, 2.15, 2.20);
    }

    private static string Fmt(double[] v) =>
        "[" + string.Join(", ", v.Select(x => x.ToString("F4"))) + "]";
}
