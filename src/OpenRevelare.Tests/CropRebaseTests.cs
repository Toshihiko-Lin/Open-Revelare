using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Broadcasting a crop across the frames a split scan was cut into.
///
/// The bug these pin: a crop is stored against the whole FILE, so on a strip cut into several
/// frames the source frame's rect names a patch of the SOURCE's negative. Handed to the siblings
/// unchanged it pointed all of them at that one patch, and every copy rendered the same
/// photograph — the split was undone by the sync. The frames must stay on their own negatives.
/// </summary>
public class CropRebaseTests
{
    // A strip of three frames stacked down a scan, each a third of the file — the shape
    // StripPlan.ToCropRects yields for a vertical strip cut into three.
    private static (double, double, double, double) Cell(int i) => (0.0, i / 3.0, 1.0, 1.0 / 3.0);

    private const double Tol = 1e-9;

    private static void AssertRect((double X, double Y, double W, double H) expected,
                                   (double X, double Y, double W, double H) actual)
    {
        Assert.Equal(expected.X, actual.X, Tol);
        Assert.Equal(expected.Y, actual.Y, Tol);
        Assert.Equal(expected.W, actual.W, Tol);
        Assert.Equal(expected.H, actual.H, Tol);
    }

    /// <summary>
    /// THE regression. The source is the strip's first frame, cropped to the middle half of its
    /// own negative; the target is the third frame. The rebased rect must land inside the THIRD
    /// cell — if it comes back naming the first cell's pixels, the copies have collapsed.
    /// </summary>
    [Fact]
    public void Crop_lands_on_the_targets_own_negative_not_the_sources()
    {
        // Middle half of cell 0, which spans y∈[0, 1/3): y∈[1/12, 3/12).
        var crop = (0.25, 1.0 / 12.0, 0.5, 1.0 / 6.0);

        var moved = CropRebase.Rebase(crop, Cell(0), Cell(2));

        // Cell 2 spans y∈[2/3, 1). The rebased rect must sit strictly inside it.
        Assert.InRange(moved.Y, 2.0 / 3.0, 1.0);
        Assert.InRange(moved.Y + moved.H, 2.0 / 3.0, 1.0 + Tol);
        // And must NOT be the rect it started as — that is exactly the collapse.
        Assert.NotEqual(crop.Item2, moved.Y, precision: 6);
    }

    /// <summary>The same fraction of the negative, on cells of the same size — so the rect is the
    /// source's, shifted by exactly the distance between the two cells and nothing else.</summary>
    [Fact]
    public void Equal_cells_shift_the_crop_without_resizing_it()
    {
        var crop = (0.25, 1.0 / 12.0, 0.5, 1.0 / 6.0);

        var moved = CropRebase.Rebase(crop, Cell(0), Cell(2));

        AssertRect((0.25, 1.0 / 12.0 + 2.0 / 3.0, 0.5, 1.0 / 6.0), moved);
    }

    /// <summary>A frame that owns its whole negative hands the whole negative to the target —
    /// "no crop within the cell" has to survive the trip as "no crop within the cell".</summary>
    [Fact]
    public void A_full_cell_crop_becomes_the_targets_full_cell()
    {
        var moved = CropRebase.Rebase(Cell(1), Cell(1), Cell(2));

        AssertRect(Cell(2), moved);
    }

    /// <summary>Ordinary frames — no cells — keep the verbatim copy they always had. Anything
    /// else would move a crop on a roll where the file IS the frame.</summary>
    [Fact]
    public void Frames_without_cells_copy_verbatim()
    {
        var crop = (0.1, 0.2, 0.5, 0.6);

        AssertRect(crop, CropRebase.Rebase(crop, null, null));
        AssertRect(crop, CropRebase.Rebase(crop, Cell(0), null));
        AssertRect(crop, CropRebase.Rebase(crop, null, Cell(0)));
    }

    /// <summary>Two frames of the same negative — a plain virtual copy — have nothing to move.</summary>
    [Fact]
    public void Identical_cells_are_a_no_op()
    {
        var crop = (0.25, 0.05, 0.5, 0.2);

        AssertRect(crop, CropRebase.Rebase(crop, Cell(1), Cell(1)));
    }

    /// <summary>
    /// Round trip: carry a crop out to a sibling and back, and the original must return. This is
    /// what makes the broadcast non-destructive — a user who syncs the roll and then syncs back
    /// from a different frame has not quietly walked every crop across the strip.
    /// </summary>
    [Fact]
    public void Rebasing_there_and_back_returns_the_original()
    {
        var crop = (0.2, 0.05, 0.6, 0.25);

        var there = CropRebase.Rebase(crop, Cell(0), Cell(1));
        var back = CropRebase.Rebase(there, Cell(1), Cell(0));

        AssertRect(crop, back);
    }

    /// <summary>
    /// Unequal cells — the real case, since the detector's dividers land on the gutters and the
    /// end frames get whatever is left. The crop keeps its FRACTION of the negative, so it
    /// rescales with the cell rather than keeping its absolute size.
    /// </summary>
    [Fact]
    public void Unequal_cells_carry_the_fraction_not_the_size()
    {
        var from = (0.0, 0.0, 1.0, 0.5);      // a tall cell
        var to = (0.0, 0.5, 1.0, 0.25);       // a target half its height
        var crop = (0.0, 0.25, 1.0, 0.25);    // bottom half of the source cell

        var moved = CropRebase.Rebase(crop, from, to);

        // Bottom half of the TARGET cell: y∈[0.625, 0.75).
        AssertRect((0.0, 0.625, 1.0, 0.125), moved);
    }

    /// <summary>
    /// A shape that overhangs the target cell is slid back inside rather than trimmed — the size
    /// and aspect the user chose are what they asked for, and a cell that merely sits near the
    /// file's edge should not silently shrink their crop.
    /// </summary>
    [Fact]
    public void An_overhanging_shape_slides_inside_keeping_its_size()
    {
        // Target cell is the strip's last, running to the very bottom of the file; a crop sitting
        // low in a taller source cell would spill past y=1.
        var from = (0.0, 0.0, 1.0, 0.5);
        var to = (0.0, 0.75, 1.0, 0.25);
        var crop = (0.0, 0.4, 1.0, 0.1);       // bottom fifth of the source cell

        var moved = CropRebase.Rebase(crop, from, to);

        Assert.Equal(0.05, moved.H, Tol);                 // size preserved
        Assert.True(moved.Y + moved.H <= 1.0 + Tol);      // and inside the file
    }

    /// <summary>Nothing ever leaves the file, whatever the cells were — the region decoder is
    /// handed these rects directly and cannot address pixels that are not there.</summary>
    [Fact]
    public void Result_always_stays_within_the_file()
    {
        var from = (0.0, 0.0, 0.1, 0.1);       // a tiny cell …
        var to = (0.5, 0.5, 0.5, 0.5);         // … onto a big one
        var crop = (0.05, 0.05, 0.5, 0.5);     // a crop far larger than its cell

        var moved = CropRebase.Rebase(crop, from, to);

        Assert.InRange(moved.X, 0.0, 1.0);
        Assert.InRange(moved.Y, 0.0, 1.0);
        Assert.InRange(moved.X + moved.W, 0.0, 1.0 + Tol);
        Assert.InRange(moved.Y + moved.H, 0.0, 1.0 + Tol);
    }

    /// <summary>A degenerate cell has no ratio to take, so the rect passes through rather than
    /// coming back as NaN — which would propagate into the decoder and the project file.</summary>
    [Fact]
    public void A_zero_sized_cell_does_not_produce_nan()
    {
        var crop = (0.1, 0.2, 0.3, 0.4);

        var moved = CropRebase.Rebase(crop, (0.0, 0.0, 0.0, 0.0), Cell(1));

        AssertRect(crop, moved);
    }

    /// <summary>
    /// The cells the split dialog actually produces tile the file, so a broadcast across a whole
    /// strip leaves every frame on a DIFFERENT negative. This is the property the bug violated:
    /// after the sync all three frames named the same pixels.
    /// </summary>
    [Fact]
    public void Broadcasting_across_a_strip_keeps_every_frame_distinct()
    {
        var crop = (0.25, 1.0 / 12.0, 0.5, 1.0 / 6.0);   // source is frame 0

        var results = new[]
        {
            CropRebase.Rebase(crop, Cell(0), Cell(0)),
            CropRebase.Rebase(crop, Cell(0), Cell(1)),
            CropRebase.Rebase(crop, Cell(0), Cell(2)),
        };

        Assert.Equal(3, results.Distinct().Count());
        // Each inside its own cell, in order down the file.
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(results[i].Y, i / 3.0, (i + 1) / 3.0);
            Assert.InRange(results[i].Y + results[i].H, i / 3.0, (i + 1) / 3.0 + Tol);
        }
    }
}
