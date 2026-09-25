using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Solving the contact sheet's column count for a requested PAGE proportion.
///
/// What makes this worth pinning is that the proportion is a property of the finished sheet, not
/// of the thumbnail grid: the margins, header and identification strip are a slab of paper the
/// grid knows nothing about, and on a short roll they are enough to turn a square-looking grid
/// into a portrait page. So the search plans every column count and measures the composed size.
///
/// These call the real planner (no Avalonia app is started — the composer's geometry is arithmetic
/// and its font object constructs fine outside one).
/// </summary>
public class SheetAspectTests
{
    private static List<ImageBuffer> Roll(int n, int w = 300, int h = 200)
    {
        var l = new List<ImageBuffer>(n);
        for (int i = 0; i < n; i++) l.Add(new ImageBuffer(w, h));
        return l;
    }

    private static double PageAspect(IReadOnlyList<ImageBuffer> roll, SheetAspect a, SheetOrientation o)
    {
        var opt = new SheetComposer.Options { Aspect = a, Orientation = o };
        Avalonia.PixelSize s = SheetComposer.SizeFor(roll, 2048, opt);
        return (double)s.Width / s.Height;
    }

    /// <summary>
    /// Asking for a wider shape never returns a narrower page.
    ///
    /// The obvious assertion — each target lands nearer its own shape than the other two — is not
    /// available, and the reason is worth recording: the candidates are one per column count, so
    /// they are sparse. A 36-frame roll of 3:2 frames can only be 6×6 (1.06), 7×6 (1.17), 8×5
    /// (1.45) or 9×4 (1.77); nothing sits near 4:3 at all, and the nearest candidate to it is also
    /// the nearest to 3:2. Monotonicity is what survives that sparseness, and it is what the
    /// option actually promises.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    public void Wider_targets_never_give_narrower_pages(int n)
    {
        var roll = Roll(n);
        double square = PageAspect(roll, SheetAspect.Square, SheetOrientation.Landscape);
        double fourThree = PageAspect(roll, SheetAspect.FourThree, SheetOrientation.Landscape);
        double threeTwo = PageAspect(roll, SheetAspect.ThreeTwo, SheetOrientation.Landscape);

        Assert.True(square <= fourThree + 1e-9, $"n={n}: 1:1 {square:F3} wider than 4:3 {fourThree:F3}");
        Assert.True(fourThree <= threeTwo + 1e-9, $"n={n}: 4:3 {fourThree:F3} wider than 3:2 {threeTwo:F3}");
    }

    /// <summary>How far a page sits outside the square…4:3 band; 0 inside it.</summary>
    private static double BandMiss(double got)
    {
        if (got < 1.0) return Math.Log(1.0 / got);
        if (got > 4.0 / 3.0) return Math.Log(got / (4.0 / 3.0));
        return 0;
    }

    /// <summary>
    /// Auto's whole job: land in the square…4:3 band, or as near it as the roll allows.
    ///
    /// Stated as "no other target's answer is nearer the band", not as a hard range, because the
    /// candidates are one per column count and can straddle it: 12 portrait 3:2 frames give 6×2
    /// (1.41) or 5×3 (0.91), and 1.41 is genuinely the closer of the two. A fixed tolerance would
    /// be a fudge factor tuned to that case; this compares against real alternatives instead.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(72)]
    public void Auto_gets_as_near_the_square_to_four_three_band_as_the_roll_allows(int n)
    {
        foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
        {
            var roll = Roll(n, w, h);
            double auto = BandMiss(PageAspect(roll, SheetAspect.Auto, SheetOrientation.Landscape));

            foreach (SheetAspect other in new[]
                     { SheetAspect.Square, SheetAspect.FourThree, SheetAspect.ThreeTwo })
            {
                double rival = BandMiss(PageAspect(roll, other, SheetOrientation.Landscape));
                // Slack for Score's same-shape tolerance, which may settle a near-tie on how much
                // the sheet had to be stretched rather than on a 0.1% difference in shape.
                Assert.True(auto <= rival + 0.01,
                            $"n={n} {w}x{h}: auto misses the band by {auto:F4}, {other} by {rival:F4}");
            }
            // And never absurd — a real failure lands at 0.5 or 2.0, not at 1.41.
            Assert.True(auto < Math.Log(1.15), $"n={n} {w}x{h}: auto misses the band by {auto:F4}");
        }
    }

    /// <summary>The classic sheet is not sacrificed to a rounder page: 36 frames stay on a full
    /// 6×6 rather than being nudged to 7 columns with a gap in the last row.</summary>
    [Fact]
    public void Auto_prefers_a_full_grid_among_equally_good_shapes()
    {
        var opt = new SheetComposer.Options { Aspect = SheetAspect.Auto };
        ContactSheet.Layout l = SheetComposer.Plan(Roll(36), 2048, opt);

        Assert.Equal(6, l.Cols);
        Assert.Equal(6, l.Rows);
        Assert.Equal(0, l.Rows * l.Cols - 36);
    }

    /// <summary>A short final strip is preferable to shrinking all 24 frames just to make a
    /// rectangular multiplication table. This is both denser and faithful to physical sheets,
    /// whose final strip simply stops after the final exposure.</summary>
    [Fact]
    public void Four_three_prefers_the_denser_five_strip_contact_print()
    {
        var opt = new SheetComposer.Options
        {
            Aspect = SheetAspect.FourThree,
            Orientation = SheetOrientation.Landscape,
        };
        ContactSheet.Layout l = SheetComposer.Plan(Roll(24), 2048, opt);

        Assert.Equal(5, l.Cols);
        Assert.Equal(5, l.Rows);
        Assert.Equal(1, l.Rows * l.Cols - 24);
    }

    /// <summary>Whichever way round it is read, the page comes out on the requested side of
    /// square. Auto included — a band still has a side.</summary>
    [Theory]
    [InlineData(SheetAspect.Auto)]
    [InlineData(SheetAspect.FourThree)]
    [InlineData(SheetAspect.ThreeTwo)]
    public void Portrait_turns_the_page_over(SheetAspect want)
    {
        var roll = Roll(36);
        double wide = PageAspect(roll, want, SheetOrientation.Landscape);
        double tall = PageAspect(roll, want, SheetOrientation.Portrait);

        Assert.True(wide > 1.0, $"{want} landscape: expected a wide page, got {wide:F3}");
        Assert.True(tall < 1.0, $"{want} portrait: expected a tall page, got {tall:F3}");
    }

    /// <summary>
    /// A POINT target turned over gives the reciprocal shape — 4:3 wide and 4:3 tall are the same
    /// proportion read two ways, which is the whole meaning of the toggle.
    ///
    /// Auto is excluded on purpose: it scores a BAND, so which point inside the band wins depends
    /// on which column counts happen to be full, and the two ends need not be reciprocals of each
    /// other. <see cref="Portrait_turns_the_page_over"/> covers the side for it.
    /// </summary>
    [Theory]
    [InlineData(SheetAspect.FourThree)]
    [InlineData(SheetAspect.ThreeTwo)]
    public void A_point_target_turned_over_is_its_reciprocal(SheetAspect want)
    {
        var roll = Roll(36);
        double wide = PageAspect(roll, want, SheetOrientation.Landscape);
        double tall = PageAspect(roll, want, SheetOrientation.Portrait);

        // 6% covers the rounding of an integer grid; the shapes themselves are ~35% apart.
        Assert.True(Math.Abs(Math.Log(wide * tall)) < 0.06,
                    $"{want}: {wide:F3} and {tall:F3} are not reciprocal shapes");
    }

    /// <summary>
    /// The requested side of square is honoured at every export size, not just the default one.
    ///
    /// THE regression here: the emptiness tie-break used to compare how far two candidates missed
    /// the target rather than whether they were the same shape, so at a 1200 px sheet a full 6×6
    /// landscape page (1.06, missing a portrait request by 0.058) displaced the 5×8 portrait page
    /// that honoured it (0.72, missing by 0.041) — 0.017 apart read as a tie. It reproduced only
    /// at some sizes, because the surround is not a perfectly linear share of the page.
    /// </summary>
    [Theory]
    [InlineData(SheetAspect.FourThree)]
    [InlineData(SheetAspect.ThreeTwo)]
    public void The_requested_side_of_square_holds_at_every_size(SheetAspect want)
    {
        foreach (int maxLong in new[] { 800, 1000, 1200, 1400, 1600, 2048, 3000 })
        foreach (int n in new[] { 24, 36, 37 })
        {
            var roll = Roll(n);
            foreach (SheetOrientation o in new[] { SheetOrientation.Landscape, SheetOrientation.Portrait })
            {
                var opt = new SheetComposer.Options { Aspect = want, Orientation = o };
                Avalonia.PixelSize s = SheetComposer.SizeFor(roll, maxLong, opt);
                double got = (double)s.Width / s.Height;
                if (o == SheetOrientation.Landscape)
                    Assert.True(got > 1.0, $"{want} 横 n={n} @{maxLong}: got a tall page {got:F3}");
                else
                    Assert.True(got < 1.0, $"{want} 竖 n={n} @{maxLong}: got a wide page {got:F3}");
            }
        }
    }

    /// <summary>
    /// Auto is a BAND, and square is one of its edges — so a square page is a legitimate answer
    /// for either orientation and the side test above does not apply to it. What must hold is
    /// that the band itself turns over: square…4:3 wide, 3:4…square tall.
    /// </summary>
    [Theory]
    [InlineData(SheetOrientation.Landscape)]
    [InlineData(SheetOrientation.Portrait)]
    public void Auto_stays_inside_its_band_whichever_way_round_it_is_read(SheetOrientation o)
    {
        double lo = o == SheetOrientation.Portrait ? 0.75 : 1.0;
        double hi = o == SheetOrientation.Portrait ? 1.0 : 4.0 / 3.0;

        foreach (int maxLong in new[] { 800, 1000, 1200, 1400, 1600, 2048, 3000 })
        foreach (int n in new[] { 24, 36, 37, 72 })
        {
            var opt = new SheetComposer.Options { Aspect = SheetAspect.Auto, Orientation = o };
            Avalonia.PixelSize s = SheetComposer.SizeFor(Roll(n), maxLong, opt);
            double got = (double)s.Width / s.Height;
            // A hair of slack for the integer gap step the fit lands on.
            Assert.InRange(got, lo / 1.005, hi * 1.005);
        }
    }

    /// <summary>A square page is its own reciprocal, so orientation genuinely cannot move it —
    /// which is why the dialog greys the choice out rather than leaving it live and inert.</summary>
    [Fact]
    public void Square_is_the_same_page_either_way_round()
    {
        var roll = Roll(24);
        var wide = SheetComposer.Plan(roll, 2048,
            new SheetComposer.Options { Aspect = SheetAspect.Square, Orientation = SheetOrientation.Landscape });
        var tall = SheetComposer.Plan(roll, 2048,
            new SheetComposer.Options { Aspect = SheetAspect.Square, Orientation = SheetOrientation.Portrait });

        Assert.Equal(wide.Cols, tall.Cols);
        Assert.Equal(wide.Rows, tall.Rows);
    }

    /// <summary>
    /// The named proportion is DELIVERED, not merely approached.
    ///
    /// The column count alone cannot do this — it is one candidate per integer, and on the tall
    /// side they are far apart — so the residual is spent on the gaps between thumbnails. Before
    /// that, a portrait 4:3 and a portrait 3:2 both had to settle for the same 5×8 grid on a
    /// 36-frame roll and came out as literally the same file.
    ///
    /// The residual is spent on the page MARGIN, so neither gap moves and the grid keeps the
    /// density it was designed with (see <see cref="Neither_gap_moves_with_the_requested_shape"/>).
    /// Margin also works in both directions, unlike a gap, which is what removes the lower bound
    /// on roll length that the earlier gap-based fits had.
    /// </summary>
    [Theory]
    [InlineData(SheetAspect.FourThree, 4.0 / 3.0)]
    [InlineData(SheetAspect.ThreeTwo, 1.5)]
    [InlineData(SheetAspect.Square, 1.0)]
    public void The_named_proportion_is_actually_delivered(SheetAspect want, double landscapeTarget)
    {
        foreach (SheetOrientation o in new[] { SheetOrientation.Landscape, SheetOrientation.Portrait })
        {
            double target = o == SheetOrientation.Portrait ? 1.0 / landscapeTarget : landscapeTarget;
            foreach (int n in new[] { 2, 4, 12, 21, 24, 36, 37, 72 })
            foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
            {
                double got = PageAspect(Roll(n, w, h), want, o);
                // 3% covers integer rounding of the gap; the fit lands inside 0.1% at the median.
                Assert.True(Math.Abs(Math.Log(got / target)) < Math.Log(1.03),
                            $"{want}/{o} n={n} {w}x{h}: asked {target:F3}, got {got:F3}");
            }
        }
    }

    /// <summary>
    /// 4:3 and 3:2 are different requests and must give different sheets — the complaint that
    /// prompted the gap fit, and worst in portrait, where the two coincided 63% of the time.
    /// </summary>
    [Theory]
    [InlineData(SheetOrientation.Landscape)]
    [InlineData(SheetOrientation.Portrait)]
    public void Four_three_and_three_two_are_different_sheets(SheetOrientation o)
    {
        foreach (int n in new[] { 4, 12, 24, 36, 37, 72 })
        foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
        {
            var roll = Roll(n, w, h);
            double a = PageAspect(roll, SheetAspect.FourThree, o);
            double b = PageAspect(roll, SheetAspect.ThreeTwo, o);
            // The shapes are ~12% apart; anything under 5% means one of them was not honoured.
            Assert.True(Math.Abs(Math.Log(a / b)) > Math.Log(1.05),
                        $"{o} n={n} {w}x{h}: 4:3 gave {a:F3}, 3:2 gave {b:F3}");
        }
    }

    /// <summary>
    /// A short roll reaches its shape like any other, and pays for it in paper rather than in a
    /// mangled grid: the frames keep their size and the gaps keep their spacing.
    /// </summary>
    [Fact]
    public void A_short_roll_pays_in_margin_not_in_layout()
    {
        foreach (int n in new[] { 2, 3, 4, 5 })
        {
            var opt = new SheetComposer.Options
            {
                Aspect = SheetAspect.ThreeTwo, Orientation = SheetOrientation.Portrait,
            };
            var roll = Roll(n);
            ContactSheet.Layout l = SheetComposer.Plan(roll, 2048, opt);
            Avalonia.PixelSize page = SheetComposer.SizeFor(roll, 2048, opt);

            Assert.True(l.ThumbW > 0 && l.ThumbH > 0, $"n={n}: thumbnails vanished");
            // Still an honest sheet: the grid is a real share of the page, not a stamp on a poster.
            double gridArea = (double)l.Width * l.Height / ((double)page.Width * page.Height);
            Assert.True(gridArea > 0.15, $"n={n}: grid is only {gridArea:P0} of the page");
        }
    }

    /// <summary>
    /// EVERY roll length reaches EVERY shape — there is no lower bound any more.
    ///
    /// There used to be one, and where it sat measured how good the fit was: spending the residual
    /// on gaps could only make a page taller, so a target had to be approached from the wide side
    /// and short rolls, which have too few candidate grids, fell short (the boundary was 21 frames
    /// with the row gap alone, 13 when the column gap moved too). Margin works in both directions
    /// and on every column count, so the boundary is gone rather than merely lowered.
    /// </summary>
    [Fact]
    public void Every_roll_length_reaches_every_target()
    {
        for (int n = 2; n <= 72; n++)
        foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
        foreach (SheetOrientation o in new[] { SheetOrientation.Landscape, SheetOrientation.Portrait })
        foreach (var (a, landscapeTarget) in new[]
                 { (SheetAspect.FourThree, 4.0 / 3.0), (SheetAspect.ThreeTwo, 1.5),
                   (SheetAspect.Square, 1.0) })
        {
            double target = o == SheetOrientation.Portrait ? 1.0 / landscapeTarget : landscapeTarget;
            double got = PageAspect(Roll(n, w, h), a, o);
            Assert.True(Math.Abs(Math.Log(got / target)) < Math.Log(1.02),
                        $"{a}/{o} n={n} {w}x{h}: asked {target:F3}, got {got:F3}");
        }
    }

    /// <summary>
    /// THE constraint on the page fit: NEITHER gap changes with the requested shape.
    ///
    /// The gap between columns is the rebate between negatives on a strip, and a sheet whose side
    /// spacing moves with the page proportion stops reading as a contact print. The row gap was
    /// allowed to stretch for a while instead, and that was wrong too — on the shapes it had to
    /// work hardest for it left bands between rows as tall as the frames themselves. Both are now
    /// design constants, and the proportion comes from the column count and the page margin.
    /// </summary>
    [Fact]
    public void Neither_gap_moves_with_the_requested_shape()
    {
        foreach (int maxLong in new[] { 1000, 1200, 2048 })
        {
            var cols = new HashSet<int>();
            var rows = new HashSet<int>();
            foreach (int n in new[] { 4, 12, 24, 36, 37, 72 })
            foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
            foreach (SheetAspect a in Enum.GetValues<SheetAspect>())
            foreach (SheetOrientation o in Enum.GetValues<SheetOrientation>())
            {
                ContactSheet.Layout l = SheetComposer.Plan(Roll(n, w, h), maxLong,
                    new SheetComposer.Options { Aspect = a, Orientation = o });
                cols.Add(l.GapX);
                rows.Add(l.GapY);
            }

            Assert.True(cols.Count == 1,
                        $"@{maxLong}: column gap varies with the shape — saw {string.Join(", ", cols.Order())}");
            Assert.True(rows.Count == 1,
                        $"@{maxLong}: row gap varies with the shape — saw {string.Join(", ", rows.Order())}");
        }
    }

    /// <summary>
    /// The margin the proportion buys is paper around the grid, never taken out of the grid: the
    /// thumbnails keep every pixel they would have had, whatever shape was asked for. That is the
    /// advantage over spending the residual on gaps, which shrank the frames once the rows stopped
    /// fitting the height cap.
    /// </summary>
    [Fact]
    public void Margin_never_costs_the_thumbnails_any_resolution()
    {
        foreach (int n in new[] { 4, 12, 24, 36, 37, 72 })
        foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
        {
            // The grid a column count yields is fixed by that column count alone — the requested
            // shape cannot reach into it.
            var byCols = new Dictionary<int, (int W, int H)>();
            foreach (SheetAspect a in Enum.GetValues<SheetAspect>())
            foreach (SheetOrientation o in Enum.GetValues<SheetOrientation>())
            {
                ContactSheet.Layout l = SheetComposer.Plan(Roll(n, w, h), 2048,
                    new SheetComposer.Options { Aspect = a, Orientation = o });
                if (byCols.TryGetValue(l.Cols, out var seen))
                    Assert.Equal(seen, (l.ThumbW, l.ThumbH));
                else
                    byCols[l.Cols] = (l.ThumbW, l.ThumbH);
            }
        }
    }

    /// <summary>Whatever shape is asked for, the sheet still holds the whole roll and still fits
    /// the cap it was given — the search may not buy a proportion with a lost frame.</summary>
    [Theory]
    [InlineData(SheetAspect.Auto)]
    [InlineData(SheetAspect.Square)]
    [InlineData(SheetAspect.FourThree)]
    [InlineData(SheetAspect.ThreeTwo)]
    public void Every_target_holds_the_roll_and_fits_the_cap(SheetAspect want)
    {
        foreach (SheetOrientation o in new[] { SheetOrientation.Landscape, SheetOrientation.Portrait })
        foreach (int n in new[] { 1, 2, 4, 12, 24, 36, 37 })
        {
            ContactSheet.Layout l = SheetComposer.Plan(Roll(n), 2048,
                new SheetComposer.Options { Aspect = want, Orientation = o });
            Assert.True(l.Rows * l.Cols >= n, $"{want}/{o} n={n}: grid holds {l.Rows * l.Cols}");
            Assert.True(l.Width <= 2048 && l.Height <= 2048,
                        $"{want}/{o} n={n}: grid {l.Width}x{l.Height} past the cap");
        }
    }

    /// <summary>
    /// The photographs, not the surround, are the visual subject. Across normal roll lengths,
    /// source orientations and every selectable page shape, actual image pixels remain the page's
    /// largest single element. This catches both an oversized footer and a planner that chooses
    /// rows/columns by nominal aspect while leaving a smaller photo grid, while allowing the lab
    /// record and brand lock-up enough size to be legible on a physical print.
    /// </summary>
    [Fact]
    public void Photographs_occupy_most_of_a_normal_sheet()
    {
        foreach (int n in new[] { 12, 21, 24, 36, 37, 72 })
        foreach (var (w, h) in new[] { (300, 200), (240, 240), (200, 300) })
        foreach (SheetAspect a in Enum.GetValues<SheetAspect>())
        foreach (SheetOrientation o in Enum.GetValues<SheetOrientation>())
        {
            var roll = Roll(n, w, h);
            var opt = new SheetComposer.Options { Aspect = a, Orientation = o };
            ContactSheet.Layout l = SheetComposer.Plan(roll, 2048, opt);
            Avalonia.PixelSize page = SheetComposer.SizeFor(roll, 2048, opt);
            double coverage = (double)n * l.ThumbW * l.ThumbH /
                              ((double)page.Width * page.Height);

            Assert.True(coverage >= 0.45,
                        $"{a}/{o} n={n} {w}x{h}: photographs cover only {coverage:P1}");
        }
    }

    /// <summary>A classic 36-frame roll should still read as six real strips, without the
    /// numbering and enlarged lab record turning the selected 4:3 paper into side wings.</summary>
    [Fact]
    public void Classic_six_by_six_sheet_keeps_side_surround_compact()
    {
        var roll = Roll(36);
        var opt = new SheetComposer.Options
        {
            Aspect = SheetAspect.FourThree,
            Orientation = SheetOrientation.Landscape,
        };
        ContactSheet.Layout l = SheetComposer.Plan(roll, 2048, opt);
        Avalonia.PixelSize page = SheetComposer.SizeFor(roll, 2048, opt);
        double sideSurround = (double)(page.Width - l.Width) / page.Width;

        Assert.Equal((6, 6), (l.Cols, l.Rows));
        Assert.True(sideSurround <= 0.14,
                    $"side surround is {sideSurround:P1} of the page ({page.Width} vs grid {l.Width})");
    }

    /// <summary>The metadata footer is large enough for four readable rows and the wordmark,
    /// but remains far below the old four-row form's 284 px height.</summary>
    [Fact]
    public void Metadata_footer_balances_legibility_and_photo_area()
    {
        Assert.InRange(SheetInfoBar.HeightFor(2048), 224, 240);
    }
}
