using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The size-limited JPEG export's search (<see cref="JpegIO.FitToSize"/>): quality gives way
/// first, pixels only when the floor quality is still too big, and what comes back says which
/// of the two it had to do. Driven through a synthetic encoder whose size is a known function
/// of edge and quality, so the tests assert the search rather than ImageSharp's entropy coder.
/// </summary>
public sealed class JpegSizeFitTests
{
    private const int W = 6000, H = 4000;

    /// <summary>Bytes ≈ pixels × (quality / 100) × 0.5 — monotone in both, like a real encoder.</summary>
    private static Func<int, int, byte[]> Model(List<(int Edge, int Quality)>? log = null) => (maxEdge, q) =>
    {
        int factor = Resample.BoxFactor(W, H, maxEdge);
        long pixels = (long)(W / factor) * (H / factor);
        log?.Add((Math.Max(W / factor, H / factor), q));
        return new byte[(long)(pixels * q / 100d * 0.5d)];
    };

    [Fact]
    public void A_file_already_under_the_ceiling_is_written_as_asked()
    {
        var log = new List<(int, int)>();
        var (file, fit) = JpegIO.FitToSize(W, H, 95, maxBytes: 100_000_000, Model(log));

        Assert.Single(log);
        Assert.Equal(95, fit.Quality);
        Assert.Equal(W, fit.LongEdge);
        Assert.False(fit.QualityReduced);
        Assert.False(fit.Shrunk);
        Assert.Equal(file.LongLength, fit.Bytes);
    }

    [Fact]
    public void Quality_drops_to_the_highest_value_that_fits_before_any_pixel_is_given_up()
    {
        // Full size at q=100 is 12 MB; the ceiling admits q ≤ 60 at full size.
        long ceiling = (long)(W * (long)H * 0.60 * 0.5);
        var (file, fit) = JpegIO.FitToSize(W, H, 95, ceiling, Model());

        Assert.Equal(60, fit.Quality);
        Assert.Equal(W, fit.LongEdge);
        Assert.True(fit.QualityReduced);
        Assert.False(fit.Shrunk);
        Assert.True(file.LongLength <= ceiling);
        // One quality step higher would not have fit: the search found the boundary.
        Assert.True(Model()(W, 61).LongLength > ceiling);
    }

    [Fact]
    public void The_picture_shrinks_only_when_the_floor_quality_is_still_too_big_and_then_quality_is_searched_again()
    {
        // Full size at the floor (q=40) is 4.8 MB. A 3 MB ceiling forces one box step (2×,
        // 3000×2000 = 6 MP), where q=100 is 3 MB exactly — so the requested 95 fits again.
        long ceiling = 3_000_000;
        var (file, fit) = JpegIO.FitToSize(W, H, 95, ceiling, Model());

        Assert.True(fit.Shrunk);
        Assert.Equal(3000, fit.LongEdge);
        Assert.Equal(95, fit.Quality);
        Assert.False(fit.QualityReduced);
        Assert.True(file.LongLength <= ceiling);
    }

    [Fact]
    public void Shrinking_walks_the_same_integer_ladder_as_the_long_edge_option()
    {
        // 2× (6 MP) at the floor is 1.2 MB; a 1 MB ceiling needs 3× (2000×1333).
        var (_, fit) = JpegIO.FitToSize(W, H, 95, maxBytes: 1_000_000, Model());
        Assert.Equal(2000, fit.LongEdge);
    }

    [Fact]
    public void An_impossible_ceiling_fails_loudly_instead_of_writing_a_postage_stamp()
    {
        Assert.Throws<InvalidOperationException>(() => JpegIO.FitToSize(W, H, 95, maxBytes: 1, Model()));
    }

    [Fact]
    public void A_zero_or_negative_ceiling_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegIO.FitToSize(W, H, 95, maxBytes: 0, Model()));
    }
}
