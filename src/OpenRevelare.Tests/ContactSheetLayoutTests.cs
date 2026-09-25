using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The grid planner's contract with the caller that solves the PAGE proportion. The aspect search
/// lives in the GUI (only it knows how much paper the header and info strip add), and it works by
/// planning the same roll at every column count — so what has to hold here is that a forced column
/// count is honoured exactly, and that the layout it returns still describes a grid that holds
/// every frame inside the size cap.
/// </summary>
public class ContactSheetLayoutTests
{
    private static List<ImageBuffer> Roll(int n, int w = 300, int h = 200)
    {
        var l = new List<ImageBuffer>(n);
        for (int i = 0; i < n; i++) l.Add(new ImageBuffer(w, h));
        return l;
    }

    /// <summary>Omitting the column count must not move the historic grid — every sheet printed
    /// before the aspect option existed came off ceil(sqrt(n)) columns.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(24, 5)]
    [InlineData(36, 6)]
    [InlineData(37, 7)]
    public void Default_grid_is_ceil_sqrt(int n, int expectCols)
    {
        ContactSheet.Layout l = ContactSheet.Plan(Roll(n), 2048, 20, 88);
        Assert.Equal(expectCols, l.Cols);
        Assert.Equal((int)Math.Ceiling((double)n / expectCols), l.Rows);
    }

    /// <summary>Every column count the search offers comes back verbatim, with enough rows for
    /// the whole roll — a plan that quietly re-picked its own columns would make the measured
    /// page aspect describe a different sheet from the one that gets drawn.</summary>
    [Fact]
    public void Forced_columns_are_honoured_and_hold_every_frame()
    {
        const int n = 36;
        for (int cols = 1; cols <= n; cols++)
        {
            ContactSheet.Layout l = ContactSheet.Plan(Roll(n), 2048, 20, 88, cols);
            Assert.Equal(cols, l.Cols);
            Assert.True(l.Rows * l.Cols >= n, $"{cols} columns lost frames");
            Assert.True((l.Rows - 1) * l.Cols < n, $"{cols} columns bought a blank row");
        }
    }

    /// <summary>Neither side may run past the cap, at any column count. The tall end is the one
    /// that used to be free to: a two-column grid of 36 frames is metres deep before the
    /// height clamp brings it back.</summary>
    [Fact]
    public void Neither_side_exceeds_the_cap()
    {
        foreach (int n in new[] { 4, 24, 36, 37 })
        for (int cols = 1; cols <= n; cols++)
        {
            ContactSheet.Layout l = ContactSheet.Plan(Roll(n), 2048, 20, 88, cols);
            Assert.True(l.Width <= 2048, $"n={n} cols={cols} width {l.Width}");
            Assert.True(l.Height <= 2048, $"n={n} cols={cols} height {l.Height}");
        }
    }

    /// <summary>Out-of-range column counts are clamped rather than producing a grid with zero
    /// columns (a divide by zero in <see cref="ContactSheet.Layout.Origin"/>) or more columns
    /// than frames (a whole trailing row of blanks).</summary>
    [Fact]
    public void Column_count_is_clamped_to_the_roll()
    {
        Assert.Equal(1, ContactSheet.Plan(Roll(8), 2048, 20, 88, 0).Cols);
        Assert.Equal(8, ContactSheet.Plan(Roll(8), 2048, 20, 88, 99).Cols);
    }

    /// <summary>Cells must tile the grid exactly — the page-aspect search measures
    /// <see cref="ContactSheet.Layout.Width"/>/<see cref="ContactSheet.Layout.Height"/>, so a
    /// cell that landed outside them would be drawn off the canvas.</summary>
    [Fact]
    public void Every_cell_lands_inside_the_grid()
    {
        ContactSheet.Layout l = ContactSheet.Plan(Roll(37), 2048, 20, 88, 8);
        for (int i = 0; i < l.Count; i++)
        {
            (int x, int y) = l.Origin(i);
            Assert.InRange(x + l.ThumbW, 0, l.Width);
            Assert.InRange(y + l.ThumbH, 0, l.Height);
        }
    }

    /// <summary>An incomplete final row starts at the left like a physical contact print; blank
    /// positions remain visible at the right instead of turning into decorative centring.</summary>
    [Fact]
    public void Incomplete_last_row_stays_left_aligned()
    {
        ContactSheet.Layout l = ContactSheet.Plan(Roll(24), 2048, 20, 88, 5);
        int lastCount = l.Count - (l.Rows - 1) * l.Cols;
        int first = l.Count - lastCount;
        (int x, _) = l.Origin(first);

        Assert.Equal(0, x);
    }
}
