using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The decoded slice a sharp patch is cut from. A region decode is ~94% whole-file unpack, so
/// the box is nearly free and the only thing that matters is how many later requests it answers
/// without decoding again.
/// </summary>
public sealed class RegionSlotBudgetTests
{
    private static (int X0, int Y0, int X1, int Y1) Expand(
        (int, int, int, int) need, int w, int h)
        => MainViewModel.ExpandToSlotBudget(need, w, h);

    [Fact]
    public void A_frame_inside_the_budget_is_taken_whole()
    {
        // 12 MP: under the ceiling, so nothing in this frame ever decodes twice.
        Assert.Equal((0, 0, 4000, 3000), Expand((1000, 1000, 1200, 1200), 4000, 3000));
    }

    [Fact]
    public void A_small_need_in_a_large_frame_grows_about_its_centre()
    {
        // 80 MP frame, a 1 MP need in the middle.
        var (x0, y0, x1, y1) = Expand((5000, 4000, 6000, 5000), 10336, 7760);
        long area = (long)(x1 - x0) * (y1 - y0);

        Assert.InRange(area, 15_000_000, 16_100_000);          // filled the budget
        Assert.True(x0 <= 5000 && y0 <= 4000 && x1 >= 6000 && y1 >= 5000);   // still covers it
        // Centred: the slack is shared between the two sides.
        Assert.True(Math.Abs((5000 - x0) - (x1 - 6000)) <= 2);
        Assert.True(Math.Abs((4000 - y0) - (y1 - 5000)) <= 2);
    }

    [Fact]
    public void A_need_against_an_edge_spends_the_freed_budget_on_the_other_side()
    {
        var (x0, _, x1, _) = Expand((0, 0, 1000, 1000), 10336, 7760);
        Assert.Equal(0, x0);
        // Nothing to the left, so the box runs further right than a centred one would.
        Assert.True(x1 > 1000);
    }

    [Fact]
    public void A_need_already_over_the_budget_is_passed_through_untouched()
    {
        var need = (0, 0, 6000, 5000);   // 30 MP
        Assert.Equal(need, Expand(need, 10336, 7760));
    }

    [Fact]
    public void The_box_never_leaves_the_frame()
    {
        var (x0, y0, x1, y1) = Expand((9000, 7000, 10336, 7760), 10336, 7760);
        Assert.True(x0 >= 0 && y0 >= 0 && x1 <= 10336 && y1 <= 7760);
    }
}

/// <summary>
/// How wide a sharp patch is rendered. Its edge is where real pixels meet the soft preview, and
/// that seam is what a pan or a zoom-out exposes — so the patch takes whatever of the render
/// budget the visible area left unused.
/// </summary>
public sealed class SharpPatchRoiTests
{
    private static OpenRevelare.Core.RegionRender.Roi Grow(
        double x, double y, double w, double h, long cost)
        => MainViewModel.GrowRoiIntoBudget(new OpenRevelare.Core.RegionRender.Roi(x, y, w, h), cost);

    [Fact]
    public void A_cheap_roi_is_widened_about_its_centre()
    {
        // 1 MP of an 8 MP budget: room to grow, but capped at 1.7x a side.
        var r = Grow(0.4, 0.4, 0.2, 0.2, 1_000_000);
        Assert.Equal(0.34, r.W, 3);          // 0.2 x 1.7
        Assert.Equal(0.34, r.H, 3);
        Assert.Equal(0.5, r.X + r.W / 2, 3); // same centre
        Assert.Equal(0.5, r.Y + r.H / 2, 3);
    }

    [Fact]
    public void An_roi_that_already_fills_the_budget_is_left_alone()
    {
        var r = Grow(0.1, 0.1, 0.8, 0.8, 8_000_000);
        Assert.Equal(0.8, r.W, 6);
        Assert.Equal(0.1, r.X, 6);
    }

    [Fact]
    public void Growth_is_bounded_by_the_budget_not_only_by_the_cap()
    {
        // 4 MP of 8 leaves sqrt(2) = 1.41x, below the 1.7x cap.
        var r = Grow(0.3, 0.3, 0.4, 0.4, 4_000_000);
        Assert.Equal(0.4 * Math.Sqrt(2), r.W, 3);
    }

    [Fact]
    public void A_patch_against_an_edge_stays_inside_the_frame()
    {
        var r = Grow(0.0, 0.0, 0.2, 0.2, 1_000_000);
        Assert.Equal(0.0, r.X, 6);
        Assert.Equal(0.0, r.Y, 6);
        Assert.True(r.X + r.W <= 1.0 + 1e-9);
    }

    [Fact]
    public void Growth_never_leaves_the_normalised_frame()
    {
        var r = Grow(0.1, 0.1, 0.8, 0.8, 100_000);
        Assert.True(r.W <= 1.0 && r.H <= 1.0);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.W <= 1.0 + 1e-9 && r.Y + r.H <= 1.0 + 1e-9);
    }
}
