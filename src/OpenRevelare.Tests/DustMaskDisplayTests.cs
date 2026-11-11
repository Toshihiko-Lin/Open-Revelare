using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustMaskDisplayTests
{
    [Fact]
    public void Subpixel_dust_keeps_its_coverage_instead_of_filling_a_preview_pixel()
    {
        var mask = new DustMask(4, 4, (0, 0, 1, 1), [new(1, 1, 2)]);
        var coverage = DustMaskDisplay.Coverage(mask, 2, 2);
        Assert.Equal(new[] { .25f, 0, 0, 0 }, coverage);
        var rgba = new byte[16];
        DustMaskDisplay.WritePixel(rgba, 0, coverage[0]);
        Assert.InRange(rgba[3], 1, 111);
        Assert.Equal(1, mask.PixelCount);
        Assert.Equal(new DustMask.Run(1, 1, 2), Assert.Single(mask.EnumerateRuns()));
    }

    [Fact]
    public void Adjacent_runs_accumulate_area_and_preserve_split_bounds()
    {
        var mask = new DustMask(2, 2, (.25, .25, .5, .5), [new(0, 0, 2), new(1, 0, 2)]);
        var coverage = DustMaskDisplay.Coverage(mask, 4, 4);
        Assert.Equal(4, coverage.Sum());
        Assert.Equal(1, coverage[5]);
        Assert.Equal(1, coverage[10]);
        Assert.All(DustMaskDisplay.Coverage(mask.RelativeTo((.25, .25, .5, .5)), 4, 4),
            value => Assert.Equal(1, value));
        var full = new DustMask(4, 4, (0, 0, 1, 1), Enumerable.Range(0, 4).Select(y => new DustMask.Run(y, 0, 4)));
        Assert.All(DustMaskDisplay.Coverage(full, 2, 2), value => Assert.Equal(1, value));
    }

    [Fact]
    public void Mask_is_clipped_to_the_preview_margin()
    {
        var mask = new DustMask(2, 1, (0, 0, 1, 1), [new(0, 0, 1)]);
        Assert.Equal(new[] { 1f, 1f, 0, 0 },
            DustMaskDisplay.Coverage(mask.RelativeTo((.25, 0, .5, 1)), 4, 1));
    }

    [Fact]
    public void Restored_candidates_leave_no_colored_ghost_and_selected_mask_stays_translucent()
    {
        var key = new DustReviewProjection.GeometryKey(8, 6, null, null, null, 0, 1, false, false, 0, null);
        var runs = new[] { new PureNegComponent.CandidateRun(0, new(2, 2, 4)) };
        var projection = new DustReviewProjection(key, 8, 6, (0, 0, 1, 1), runs);
        var empty = new DustMask(8, 6, (0, 0, 1, 1), []);
        Assert.All(projection.MakeOverlay(empty), value => Assert.Equal(0, value));
        var selected = new DustMask(8, 6, (0, 0, 1, 1), runs.Select(run => run.Run));
        var rgba = projection.MakeOverlay(selected);
        Assert.Equal(2, Enumerable.Range(0, rgba.Length / 4).Count(i => rgba[i * 4 + 3] > 0));
        Assert.All(Enumerable.Range(0, rgba.Length / 4), i => Assert.InRange(rgba[i * 4 + 3], 0, 112));
    }
}
