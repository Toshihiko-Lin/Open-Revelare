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
    private static ImageBuffer[] MixedRoll() =>
        [new(60, 40), new(40, 60), new(80, 20), new(20, 80), new(40, 40)];

    [Theory]
    [InlineData(320)]
    [InlineData(1000)]
    [InlineData(2048)]
    public void Mixed_frames_keep_aspect_order_and_bottom_alignment(int limit)
    {
        ImageBuffer[] images = MixedRoll();
        for (int cols = 1; cols <= images.Length; cols++)
        {
            var layout = ContactSheet.Plan(images, limit, 8, 26, cols);
            Assert.InRange(layout.Width, 1, limit);
            Assert.InRange(layout.Height, 1, limit);
            for (int i = 0; i < images.Length; i++)
            {
                var cell = layout.Frame(i);
                double scale = (double)Math.Max(cell.Width, cell.Height) /
                               Math.Max(images[i].Width, images[i].Height);
                Assert.InRange(Math.Abs(cell.Width - images[i].Width * scale), 0, 1);
                Assert.InRange(Math.Abs(cell.Height - images[i].Height * scale), 0, 1);
                Assert.Equal(layout.RowBottom(i / cols), cell.Y + cell.Height);
                Assert.InRange(cell.X + cell.Width, 1, layout.Width);
                Assert.InRange(cell.Y + cell.Height, 1, layout.Height);
                if (i % cols == 0) Assert.Equal(0, cell.X);
                else
                {
                    var previous = layout.Frame(i - 1);
                    Assert.True(cell.X >= previous.X + previous.Width + layout.GapX);
                    if (i / cols == layout.Rows - 1 && images.Length % cols != 0)
                        Assert.Equal(previous.X + previous.Width + layout.GapX, cell.X);
                }
                if ((i + 1) % cols == 0) Assert.Equal(layout.Width, cell.X + cell.Width);
                if (i >= cols)
                    Assert.True(cell.Y >= layout.RowBottom(i / cols - 1) + layout.GapY);
            }
        }
    }

    [Theory]
    [InlineData(320)]
    [InlineData(1000)]
    [InlineData(4096)]
    public void Full_mixed_rows_align_both_edges_with_even_gaps(int limit)
    {
        ImageBuffer[] images =
            [new(60, 40), new(40, 60), new(80, 20), new(60, 40), new(45, 60), new(80, 20),
             new(40, 60), new(80, 20)];
        var layout = ContactSheet.Plan(images, limit, 8, 26, 3);
        for (int row = 0; row < 2; row++)
        {
            var first = layout.Frame(row * 3);
            var middle = layout.Frame(row * 3 + 1);
            var last = layout.Frame(row * 3 + 2);
            Assert.Equal(0, first.X);
            Assert.Equal(layout.Width, last.X + last.Width);
            int gap1 = middle.X - first.X - first.Width;
            int gap2 = last.X - middle.X - middle.Width;
            Assert.InRange(Math.Abs(gap1 - gap2), 0, 1);
            Assert.True(gap1 >= layout.GapX && gap2 >= layout.GapX);
        }
        var finalFirst = layout.Frame(6);
        var finalLast = layout.Frame(7);
        Assert.Equal(0, finalFirst.X);
        Assert.Equal(layout.GapX, finalLast.X - finalFirst.X - finalFirst.Width);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(1000)]
    [InlineData(4096)]
    public void Extreme_mixed_rows_still_align_both_edges(int limit)
    {
        ImageBuffer[] images =
            [new(10, 100), new(10, 100), new(10, 100),
             new(100, 10), new(100, 10), new(100, 10), new(40, 60)];
        var layout = ContactSheet.Plan(images, limit, 8, 26, 3);
        Assert.Equal(0, layout.Frame(0).X);
        int gap1 = layout.Frame(1).X - layout.Frame(0).X - layout.Frame(0).Width;
        int gap2 = layout.Frame(2).X - layout.Frame(1).X - layout.Frame(1).Width;
        Assert.True(gap1 >= layout.GapX);
        Assert.InRange(Math.Abs(gap1 - gap2), 0, 1);
        Assert.Equal(layout.Width, layout.Frame(2).X + layout.Frame(2).Width);
        Assert.Equal(layout.Width, layout.Frame(5).X + layout.Frame(5).Width);
        Assert.Equal(0, layout.Frame(6).X);
    }

    [Fact]
    public void Mixed_raster_and_hdr_overlay_touch_only_actual_frame_rectangles()
    {
        ImageBuffer[] images = MixedRoll();
        foreach (var image in images) Array.Fill(image.Data, 0.5f);
        var layout = ContactSheet.Plan(images, 320, 8, 26, 3);
        var sheet = ContactSheet.Build(images, layout, [0.25f, 0.25f, 0.25f]);
        foreach (var image in images) Array.Fill(image.Data, 2f);
        var hdr = ContactSheet.WithExtendedCells(sheet, ColorSpaces.Srgb, images, layout, 0, 0);
        for (int y = 0; y < sheet.Height; y++)
        for (int x = 0; x < sheet.Width; x++)
        {
            bool picture = layout.Cells!.Any(c => x >= c.X && x < c.X + c.Width &&
                                                  y >= c.Y && y < c.Y + c.Height);
            int p = (y * sheet.Width + x) * 3;
            Assert.Equal(picture ? 0.5f : 0.25f, sheet.Data[p]);
            if (picture) Assert.Equal(2f, hdr.Data[p]);
            else Assert.InRange(hdr.Data[p], 0, 0.25f);
        }
    }

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
