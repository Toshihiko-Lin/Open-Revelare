using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class CalibrationConfidenceTests
{
    [Fact]
    public void Content_only_base_is_reported_as_inference_with_a_confidence_ceiling()
    {
        var frame = Solid(100, 100, 0.30f, 0.29f, 0.28f);

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(new[] { frame });

        Assert.Equal(FilmBaseEvidence.ContentInference, estimate.Evidence);
        Assert.InRange(estimate.Confidence, 0.0, 0.45);
        Assert.Equal(1, estimate.SupportingFrames);
    }

    [Fact]
    public void Repeated_consistent_edge_rebate_is_reported_as_physical_evidence()
    {
        ImageBuffer[] roll = Enumerable.Range(0, 8).Select(_ => EdgeRebate()).ToArray();

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(roll);

        Assert.Equal(FilmBaseEvidence.PhysicalEdgeSliver, estimate.Evidence);
        Assert.True(estimate.SupportingFrames >= 6);
        Assert.True(estimate.Confidence >= 0.75, $"confidence={estimate.Confidence:F3}");
        Assert.True(estimate.LogDispersion < 0.01);
    }

    [Fact]
    public void Neutral_edge_carrier_is_physical_only_when_monochrome_is_explicit()
    {
        ImageBuffer[] roll = Enumerable.Range(0, 8).Select(_ => NeutralEdgeCarrier()).ToArray();

        FilmBaseEstimate colour = FilmBase.EstimateTBaseFromRollDetailed(roll);
        FilmBaseEstimate monochrome = FilmBase.EstimateTBaseFromRollDetailed(
            roll, allowNeutralCarrier: true);

        Assert.Equal(FilmBaseEvidence.ContentInference, colour.Evidence);
        Assert.Equal(FilmBaseEvidence.PhysicalEdgeSliver, monochrome.Evidence);
        Assert.True(monochrome.Confidence >= 0.75, $"confidence={monochrome.Confidence:F3}");
        Assert.Equal(8, monochrome.SupportingFrames);
    }

    [Fact]
    public void Monochrome_edge_carrier_excludes_white_surround_above_board_cut()
    {
        ImageBuffer frame = NeutralCarrierInsideWhiteSurround();

        double[]? unbounded = FilmBase.EstimateTBaseFromEdgeSliver(
            frame, allowNeutralCarrier: true);
        double[]? bounded = FilmBase.EstimateTBaseFromEdgeSliver(
            frame, allowNeutralCarrier: true, upperLumaCut: 0.5);

        Assert.NotNull(unbounded);
        Assert.NotNull(bounded);
        Assert.All(unbounded!, x => Assert.InRange(x, 0.99, 1.01));
        Assert.All(bounded!, x => Assert.InRange(x, 0.19, 0.21));
    }

    [Fact]
    public void Repeated_board_bounded_mask_mode_is_reported_as_physical_evidence()
    {
        ImageBuffer[] roll = Enumerable.Range(0, 6).Select(_ => BoardAndBase()).ToArray();

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(
            roll, sprocketThreshold: 0.5);

        Assert.Equal(FilmBaseEvidence.PhysicalMode, estimate.Evidence);
        Assert.Equal(6, estimate.SupportingFrames);
        Assert.True(estimate.Confidence >= 0.75, $"confidence={estimate.Confidence:F3}");
    }

    [Fact]
    public void One_weak_mode_vote_does_not_override_many_consistent_edge_rebates()
    {
        // The rebate occupies less than EstimateTBaseByMode's mode floor, so the mode path sees
        // the deliberately varying scene tone while the topology-aware edge path sees the same
        // physical orange sliver in every frame. This is the actual ambiguity the arbitration is
        // intended to resolve; a wide rebate would quite correctly be found by both estimators.
        ImageBuffer[] roll =
        [
            BoardAndBase(),
            .. Enumerable.Range(0, 7).Select(SparseEdgeRebateWithVaryingContent),
        ];

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(
            roll, sprocketThreshold: 0.5);

        Assert.Equal(FilmBaseEvidence.PhysicalEdgeSliver, estimate.Evidence);
        Assert.True(estimate.SupportingFrames >= 6);
        Assert.True(estimate.Confidence >= 0.75, $"confidence={estimate.Confidence:F3}");
    }

    [Fact]
    public void Conflicting_detector_returns_are_not_reported_as_supporting_frames()
    {
        ImageBuffer[] roll =
        [
            .. Enumerable.Range(0, 6).Select(_ => Solid(120, 80, 0.24f, 0.18f, 0.07f)),
            .. Enumerable.Range(0, 5).Select(_ => Solid(120, 80, 0.10f, 0.09f, 0.08f)),
        ];

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(
            roll, sprocketThreshold: 0.5);

        Assert.Equal(FilmBaseEvidence.ContentInference, estimate.Evidence);
        Assert.Equal(6, estimate.SupportingFrames);
        Assert.Equal(11, estimate.TotalFrames);
        Assert.True(estimate.Confidence < 0.75, $"confidence={estimate.Confidence:F3}");
    }

    [Fact]
    public void Quality_weighted_log_chroma_medoid_can_outvote_more_low_quality_candidates()
    {
        double[][] candidates =
        [
            [0.90, 1.00, 1.10],
            [0.91, 1.00, 1.09],
            [1.35, 1.00, 0.75],
            [1.36, 1.00, 0.74],
            [1.34, 1.00, 0.76],
        ];
        double[] quality = [1.0, 1.0, 0.15, 0.15, 0.15];

        int chosen = FilmBase.PickQualityWeightedLogChromaMedoid(candidates, quality);

        Assert.InRange(chosen, 0, 1);
    }

    [Fact]
    public void Low_quality_candidate_cannot_win_only_by_sitting_at_cluster_centre()
    {
        double a = Math.Exp(0.2);
        double[][] candidates =
        [
            [1.0 / a, 1.0, 1.0],
            [a,       1.0, 1.0],
            [1.0,     1.0, 1.0],
        ];
        double[] quality = [1.0, 1.0, 0.05];

        int chosen = FilmBase.PickQualityWeightedLogChromaMedoid(candidates, quality);

        Assert.InRange(chosen, 0, 1);
    }

    [Fact]
    public void Adaptive_headroom_percentile_has_no_twelve_frame_step()
    {
        double atOne = FilmBase.AdaptiveHeadroomPercentile(1);
        double atEleven = FilmBase.AdaptiveHeadroomPercentile(11);
        double atTwelve = FilmBase.AdaptiveHeadroomPercentile(12);
        double atThirtySix = FilmBase.AdaptiveHeadroomPercentile(36);

        Assert.Equal(100.0, atOne, 10);
        Assert.True(atEleven > atTwelve && atTwelve > atThirtySix);
        Assert.True(atEleven - atTwelve < 0.25);
        Assert.InRange(atThirtySix, 95.0, 95.01);
    }

    [Fact]
    public void Effective_frames_respect_absolute_quality_not_only_relative_weights()
    {
        double perfect = FilmBase.QualityWeightedEffectiveFrames(
            Enumerable.Repeat(1.0, 20).ToArray());
        double uniformlyPoor = FilmBase.QualityWeightedEffectiveFrames(
            Enumerable.Repeat(0.05, 20).ToArray());
        double dominated = FilmBase.QualityWeightedEffectiveFrames(
            new[] { 1.0 }.Concat(Enumerable.Repeat(0.05, 19)).ToArray());

        Assert.Equal(20.0, perfect, 10);
        Assert.Equal(1.0, uniformlyPoor, 10);
        Assert.InRange(dominated, 1.0, 1.951);
    }

    [Fact]
    public void Weighted_percentile_interpolates_instead_of_hiding_a_frame_count_switch()
    {
        double[] values = [0.0, 10.0, 20.0, 30.0];
        double[] weights = [1.0, 1.0, 1.0, 1.0];

        double p60 = FilmBase.WeightedPercentile(values, weights, 60.0);
        double p61 = FilmBase.WeightedPercentile(values, weights, 61.0);

        Assert.InRange(p60, 18.9, 19.1);
        Assert.InRange(p61, 19.3, 19.5);
    }

    [Fact]
    public void Low_code_count_film_base_reports_source_quantisation_risk()
    {
        ImageBuffer frame = Solid(100, 100, 0.05f, 0.04f, 0.03f);
        frame.SourceQuantisationStep = 1.0 / 255.0;

        FilmBaseEstimate estimate = FilmBase.EstimateTBaseFromRollDetailed(new[] { frame });

        Assert.True(estimate.QuantizationRisk);
        Assert.Equal(FilmBaseEvidence.ContentInference, estimate.Evidence);
    }

    [Fact]
    public void Weighted_upper_percentile_does_not_bridge_repeated_population_to_one_outlier()
    {
        double[] values = Enumerable.Repeat(2.0, 20).Append(2.6).ToArray();
        double[] weights = Enumerable.Repeat(1.0, values.Length).ToArray();

        double result = FilmBase.WeightedPercentile(values, weights, 95.1);

        Assert.Equal(2.0, result, 10);
    }

    [Fact]
    public void Highlight_endpoint_just_below_opaque_density_guard_reports_clipping_risk()
    {
        // -log10(0.001001) = 2.999566: numerically below the 3.0 rejection guard, but close
        // enough that a scanner black floor / clipped opaque edge cannot be distinguished.
        ImageBuffer frame = Solid(100, 100, 0.001001f, 0.001001f, 0.001001f);

        HighlightEndpointEstimate? estimate =
            FilmBase.DetectDMaxPerChannelFromRollDetailed([frame], [1.0, 1.0, 1.0]);

        Assert.NotNull(estimate);
        Assert.True(estimate!.ClippingRisk);
        Assert.True(estimate.Density.All(x => x < FrameParams.RealDensityCeiling));
    }

    private static ImageBuffer Solid(int width, int height, float r, float g, float b)
    {
        var image = new ImageBuffer(width, height);
        for (int i = 0; i < image.Data.Length; i += 3)
        {
            image.Data[i] = r; image.Data[i + 1] = g; image.Data[i + 2] = b;
        }
        return image;
    }

    private static ImageBuffer EdgeRebate()
    {
        const int width = 120, height = 80, band = 4;
        var image = Solid(width, height, 0.055f, 0.050f, 0.045f);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < band; x++)
            {
                int i = (y * width + x) * 3;
                image.Data[i] = 0.22f;
                image.Data[i + 1] = 0.17f;
                image.Data[i + 2] = 0.060f;
            }
        return image;
    }

    private static ImageBuffer NeutralEdgeCarrier()
    {
        const int width = 120, height = 80, band = 4;
        var image = Solid(width, height, 0.035f, 0.035f, 0.035f);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < band; x++)
            {
                int i = (y * width + x) * 3;
                image.Data[i] = image.Data[i + 1] = image.Data[i + 2] = 0.20f;
            }
        return image;
    }

    private static ImageBuffer NeutralCarrierInsideWhiteSurround()
    {
        const int width = 160, height = 100;
        var image = Solid(width, height, 0.035f, 0.035f, 0.035f);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * width + x) * 3;
                float value = x < 4 ? 1.0f : 0.20f;
                image.Data[i] = image.Data[i + 1] = image.Data[i + 2] = value;
            }
        return image;
    }

    private static ImageBuffer SparseEdgeRebateWithVaryingContent(int frame)
    {
        const int width = 400, height = 400, rebatePixels = 120;
        float content = 0.030f + frame * 0.012f;
        var image = Solid(width, height, content, content * 0.96f, content * 0.92f);
        for (int x = 0; x < rebatePixels; x++)
        {
            int i = x * 3; // a short, one-pixel-high sliver on the top film edge
            image.Data[i] = 0.22f;
            image.Data[i + 1] = 0.17f;
            image.Data[i + 2] = 0.060f;
        }
        return image;
    }

    private static ImageBuffer BoardAndBase()
    {
        const int width = 120, height = 80;
        var image = Solid(width, height, 0.060f, 0.050f, 0.040f);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                if (x < 18)
                {
                    image.Data[i] = 0.90f;
                    image.Data[i + 1] = 0.90f;
                    image.Data[i + 2] = 0.90f;
                }
                else if (x < 54)
                {
                    image.Data[i] = 0.24f;
                    image.Data[i + 1] = 0.18f;
                    image.Data[i + 2] = 0.070f;
                }
            }
        return image;
    }
}
