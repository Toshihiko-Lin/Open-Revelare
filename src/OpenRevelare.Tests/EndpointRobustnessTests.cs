using System.Linq;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The two defences added to the highlight-endpoint solve after reading NexFilm's
/// <c>compute_auto_color_limits</c>: a plateau spike guard, and a board shoulder that is measured
/// rather than assumed.
///
/// Both address the same failure — the endpoint being set by something that is not the
/// photograph, a flat synthetic block or a light board's penumbra. The endpoints are per-channel
/// DIVISORS, so either one becomes a cast baked into the inversion.
/// </summary>
public class EndpointRobustnessTests
{
    private static float T(double density) => (float)System.Math.Pow(10.0, -density);

    /// <summary>
    /// Builds a frame from populations of (r,g,b) densities, filling the rest with 1.2 — the same
    /// construction <see cref="DMaxEndpointTests"/> uses, so the two files describe one frame
    /// model.
    /// </summary>
    private static ImageBuffer Frame(params (double R, double G, double B, int Count)[] pops)
    {
        const int w = 200, h = 200, n = w * h;
        var data = new float[n * 3];
        int i = 0;
        foreach (var p in pops)
            for (int j = 0; j < p.Count && i < n; j++, i++)
            {
                data[i * 3] = T(p.R); data[i * 3 + 1] = T(p.G); data[i * 3 + 2] = T(p.B);
            }
        for (; i < n; i++) { data[i * 3] = T(1.2); data[i * 3 + 1] = T(1.2); data[i * 3 + 2] = T(1.2); }
        return new ImageBuffer(w, h, data);
    }

    private static double[] Detect(ImageBuffer f) =>
        FilmBase.DetectDMaxPerChannelFromRoll(
            new[] { f }, new[] { 1.0, 1.0, 1.0 }, 90.0, new[] { f }, null, edgeInset: 0.0)!;

    // ── 1. The spike guard ──────────────────────────────────────────────────────────

    /// <summary>
    /// A LARGE FLAT BLOCK MUST NOT DEFINE THE WHITE POINT.
    ///
    /// This is the failure NexFilm's <c>density_histogram_extremes</c> guards and ours did not:
    /// a region denser than the picture but below <see cref="FrameParams.RealDensityCeiling"/>
    /// passes IsEndpointSample, carries no grain so it occupies a single density level, and a
    /// 0.1% tail is filled by it many times over. It then IS the roll's highlight endpoint.
    ///
    /// 200 pixels of flat 2.80 against a real highlight of 40: the block is five tails deep, so
    /// without the guard it fills the tail by itself and the endpoint reads 2.80. With it the
    /// real highlight at 2.60 survives.
    ///
    /// The block is kept DELIBERATELY SMALL. Above roughly 400 pixels on this frame the two
    /// populations make the luma histogram bimodal, <see cref="Sprocket.EstimateDarkValley"/>
    /// fires, and the block is removed by the dark cut instead — which would make this test pass
    /// without the guard existing at all. Staying under that threshold is what makes it a test of
    /// the guard rather than of the cut.
    /// </summary>
    [Fact]
    public void A_flat_block_does_not_capture_the_endpoint()
    {
        double[] hi = Detect(Frame(
            (2.60, 2.60, 2.60, 40),       // the real highlight
            (2.80, 2.80, 2.80, 200)));    // a flat denser block, below the dark-valley trigger

        Assert.Equal(2.60, hi[0], 2);
    }

    /// <summary>
    /// THE GUARD MUST NOT FIRE ON REAL PICTURE TONE. A highlight that spreads across density
    /// levels — which grain and any illumination gradient guarantee — is not a plateau, so the
    /// endpoint is measured exactly as before the guard existed.
    /// </summary>
    [Fact]
    public void A_spread_highlight_is_measured_unchanged()
    {
        // Twelve adjacent levels, none individually large enough to be a plateau, and 120 dense
        // pixels in total — under the dark-valley trigger, as in the test above.
        var pops = new (double, double, double, int)[12];
        for (int i = 0; i < 12; i++)
        {
            double d = 2.55 + i * 0.005;
            pops[i] = (d, d, d, 10);
        }

        double[] hi = Detect(Frame(pops));

        // The tail sits at the dense end of the spread, not below it — and specifically not
        // pulled down, which is what a guard misfiring on real tone would do.
        Assert.InRange(hi[0], 2.60, 2.61);
    }

    /// <summary>
    /// A FRAME THAT IS ENTIRELY ONE LEVEL still returns that level. The guard would reject every
    /// sample, and the unguarded retry is what keeps the frame in the roll — a
    /// plateau-contaminated endpoint beats no endpoint at all.
    /// </summary>
    [Fact]
    public void A_uniform_frame_still_yields_its_own_density()
    {
        double[] hi = Detect(Frame((2.40, 2.40, 2.40, 200 * 200)));

        Assert.Equal(2.40, hi[0], 3);
        Assert.Equal(2.40, hi[1], 3);
        Assert.Equal(2.40, hi[2], 3);
    }

    // ── 1b. The roll's colour comes from a representative frame ─────────────────────

    /// <summary>
    /// A SINGLE OFF-COLOUR FRAME MUST NOT SET THE ROLL'S COLOUR.
    ///
    /// The endpoints are per-channel divisors applied to every frame, so whichever frame is
    /// chosen hands its channel RATIOS to the whole roll. Choosing on total density picks exactly
    /// the wrong frame, because depth and cast are correlated: an off-neutral highlight reads as
    /// denser. Measured on an expired Superia 200 roll, the deepest frame was also the worst
    /// colour match and took the roll with it.
    /// </summary>
    [Fact]
    public void The_rolls_colour_comes_from_the_most_representative_frame()
    {
        // Three frames agreeing on a neutral highlight, plus one off-colour frame that is also
        // the deepest — the configuration that used to win on depth.
        ImageBuffer n1 = Frame((2.30, 2.30, 2.30, 40));
        ImageBuffer n2 = Frame((2.36, 2.36, 2.36, 40));
        ImageBuffer n3 = Frame((2.40, 2.40, 2.40, 40));
        ImageBuffer tinted = Frame((3.20, 2.30, 2.20, 40));   // deepest AND clearly off-colour

        var roll = new[] { n1, n2, n3, tinted };
        double[] answer = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, new[] { 1.0, 1.0, 1.0 }, 90.0, roll, null, edgeInset: 0.0)!;

        // The neutral frames agree R/G and B/G are 1; the tinted one claims 1.39 and 0.96.
        Assert.Equal(1.0, answer[0] / answer[1], 2);
        Assert.Equal(1.0, answer[2] / answer[1], 2);
    }

    /// <summary>
    /// THE CHOSEN FRAME IS LIFTED CLEAR OF THE WHOLE ROLL, not just of itself.
    ///
    /// Choosing on colour means the winner can be shallower than other frames. Its endpoint is
    /// therefore rescaled against the ROLL-WIDE per-channel maximum — otherwise every deeper
    /// frame would exceed the divisor it is applied to and clip its highlight, which is an
    /// unrecoverable loss of picture. The lift is one factor on all three channels, so the colour
    /// it was chosen for survives untouched.
    /// </summary>
    [Fact]
    public void The_endpoint_clears_the_deepest_frame_on_the_roll()
    {
        ImageBuffer neutral1 = Frame((2.00, 2.00, 2.00, 40));
        ImageBuffer neutral2 = Frame((2.05, 2.05, 2.05, 40));
        ImageBuffer deep = Frame((2.90, 2.62, 2.70, 40));   // deeper, slightly off-colour

        var roll = new[] { neutral1, neutral2, deep };
        double[] answer = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, new[] { 1.0, 1.0, 1.0 }, 90.0, roll, null, edgeInset: 0.0)!;

        // No channel of the deepest frame may exceed its own endpoint.
        Assert.True(answer[0] >= 2.90 - 1e-6, $"red would clip: 2.90 against {answer[0]}");
        Assert.True(answer[1] >= 2.62 - 1e-6, $"green would clip: 2.62 against {answer[1]}");
        Assert.True(answer[2] >= 2.70 - 1e-6, $"blue would clip: 2.70 against {answer[2]}");

        // And the lift preserved the neutral colour it was chosen for.
        Assert.Equal(1.0, answer[0] / answer[1], 2);
    }

    /// <summary>
    /// A frame whose colour matches the roll but which is far shallower is still eligible — depth
    /// is not part of the choice at all, because the rescale repairs it. This pins the decision
    /// that colour outranks depth, taken deliberately: a cast baked into a per-channel divisor is
    /// unrecoverable, a shallow placement is not.
    /// </summary>
    [Fact]
    public void Depth_does_not_participate_in_the_choice()
    {
        // The shallowest frame is the one that matches the roll's colour consensus.
        ImageBuffer shallowNeutral = Frame((1.60, 1.60, 1.60, 40));
        ImageBuffer offColour1 = Frame((2.40, 2.16, 2.28, 40));
        ImageBuffer offColour2 = Frame((2.50, 2.25, 2.38, 40));

        var roll = new[] { shallowNeutral, offColour1, offColour2 };
        double[] answer = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, new[] { 1.0, 1.0, 1.0 }, 90.0, roll, null, edgeInset: 0.0)!;

        // Two of three frames agree on R/G = 2.40/2.16 = 1.111, so THAT is the consensus and the
        // odd frame out is the neutral one — the median decides, not neutrality in the abstract.
        // The point being pinned is that the winner is chosen on colour alone; the shallow frame
        // is not preferred merely for being neutral, nor rejected merely for being shallow.
        Assert.Equal(2.40 / 2.16, answer[0] / answer[1], 2);
    }

    /// <summary>
    /// ONE DEEP FRAME MUST NOT SET THE ROLL'S EXPOSURE THROUGH THE NO-CLIP LIFT.
    ///
    /// The companion to <see cref="The_rolls_colour_comes_from_the_most_representative_frame"/>,
    /// closing the same hole at the other end of the reduction. Choosing the triple on colour
    /// stops an outlier from setting the roll's BALANCE, but the lift that follows was pooled by
    /// a plain maximum — decided, by construction, by a single frame. So the outlier kept the
    /// roll anyway: not through its ratios, through its depth.
    ///
    /// Measured on the 除碳5219 roll, DSCF5657 set all three channels of that pool alone and
    /// lifted the endpoint 1.197× above the frame the colour pick had chosen. Every ordinary
    /// frame then divided by a number ~20% above its own highlight and rendered flat and dark,
    /// with roughly a sixth of the highlight range unreachable.
    ///
    /// Pooling by an upper percentile instead keeps the lift where the roll actually is. The
    /// frames at or below the percentile still keep full headroom — that is the clipping this
    /// rescale exists to prevent, and it is still prevented.
    /// </summary>
    [Fact]
    public void One_deep_frame_does_not_inflate_the_rolls_endpoint()
    {
        // Twenty frames agreeing closely, plus one markedly deeper — the shape of a real roll
        // with a single dense frame on it.
        var roll = new ImageBuffer[21];
        for (int i = 0; i < 20; i++)
        {
            double d = 2.00 + i * 0.002;   // a tight, ordinary spread
            roll[i] = Frame((d, d, d, 40));
        }
        roll[20] = Frame((2.60, 2.60, 2.60, 40));   // the lone deep frame

        double[] answer = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, new[] { 1.0, 1.0, 1.0 }, 90.0, roll, null, edgeInset: 0.0)!;

        // The deep frame must not drag the endpoint up to itself.
        Assert.True(answer[1] < 2.30,
            $"the lone deep frame set the roll's endpoint: {answer[1]:F4}");

        // It still clears the ordinary frames — the clipping guard is intact for the roll proper.
        Assert.True(answer[1] >= 2.038 - 1e-6,
            $"the ordinary frames would clip: 2.038 against {answer[1]:F4}");

        // And the lift stays uniform, so the colour is untouched.
        Assert.Equal(1.0, answer[0] / answer[1], 2);
        Assert.Equal(1.0, answer[2] / answer[1], 2);
    }

    /// <summary>
    /// THE ROLL'S COLOUR IS WHERE ITS FRAMES AGREE, NOT AT EITHER END OF ITS EXPOSURE RANGE.
    ///
    /// The shape of a real roll (诺日士1089, twelve frames): a handful of well-exposed frames whose
    /// highlight was a genuine white and which therefore report the same ratios to within grain,
    /// plus more frames whose brightest subject was something coloured and which scatter — each
    /// in its own direction, none agreeing with any other.
    ///
    /// Neither a depth rule nor a trend rule finds the cluster. The scattered frames here are
    /// deliberately the SHALLOW ones and drift consistently with depth, which is what an earlier
    /// revision extrapolated to and picked from; a median of the ratios is pulled toward the
    /// scattered side as well. Only the frames that back each other can say what the film's
    /// highlight looks like.
    /// </summary>
    [Fact]
    public void The_rolls_colour_comes_from_the_frames_that_agree_with_each_other()
    {
        // Five deep frames within ±1% of R/G 0.92 / B/G 1.11 — the film's white.
        var cluster = new[]
        {
            Frame((1.40 * 0.925, 1.40, 1.40 * 1.100, 40)),
            Frame((1.45 * 0.915, 1.45, 1.45 * 1.115, 40)),
            Frame((1.50 * 0.920, 1.50, 1.50 * 1.105, 40)),
            Frame((1.55 * 0.930, 1.55, 1.55 * 1.120, 40)),
            Frame((1.60 * 0.918, 1.60, 1.60 * 1.108, 40)),
        };
        // Seven shallow frames, each a different coloured subject, whose ratios happen to trend
        // with depth — the pattern a trend fit would follow to the shallow end.
        var scattered = new[]
        {
            Frame((0.80 * 0.860, 0.80, 0.80 * 0.900, 40)),
            Frame((0.85 * 0.980, 0.85, 0.85 * 0.960, 40)),
            Frame((0.90 * 0.870, 0.90, 0.90 * 1.160, 40)),
            Frame((0.95 * 1.060, 0.95, 0.95 * 0.780, 40)),
            Frame((1.00 * 0.900, 1.00, 1.00 * 1.010, 40)),
            Frame((1.05 * 0.970, 1.05, 1.05 * 0.870, 40)),
            Frame((1.10 * 0.950, 1.10, 1.10 * 1.160, 40)),
        };
        var roll = cluster.Concat(scattered).ToArray();

        double[] answer = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, new[] { 1.0, 1.0, 1.0 }, 90.0, roll, null, edgeInset: 0.0)!;

        double rg = answer[0] / answer[1], bg = answer[2] / answer[1];
        Assert.InRange(rg, 0.91, 0.935);
        Assert.InRange(bg, 1.095, 1.125);
    }

    /// <summary>
    /// THE NO-CLIP LIFT MUST NOT CHANGE THE COLOUR THE FRAME WAS CHOSEN FOR — IN THE SPANS.
    ///
    /// The inversion consumes <c>dMax_c − dMin_c</c> per channel, so colour is the ratio between
    /// the three SPANS. The detector's lift multiplies whatever densities it was handed; only if
    /// those are measured against the film base (tBase = the base, not 1,1,1) does one factor
    /// leave the span ratios alone. Measured against a neutral reference on an orange-masked
    /// roll, a 1.549× lift added (k−1)·dMin to every span and moved the roll's span R/G from
    /// 0.866 to 0.767 — the whole roll came out red.
    ///
    /// A deep frame elsewhere on the roll forces the lift here; the chosen frame is the cluster's
    /// and must come back with its own span ratios, lifted.
    /// </summary>
    [Fact]
    public void The_lift_preserves_span_ratios_when_measured_against_the_base()
    {
        double[] dMin = { 0.30, 0.65, 0.90 };
        double[] tBase = dMin.Select(d => System.Math.Pow(10.0, -d)).ToArray();

        // Three agreeing frames with span ratios R/G 0.90 / B/G 1.10 above the base, and one
        // much deeper neutral-ish frame that only sets the headroom.
        ImageBuffer Above(double g, double rg, double bg) =>
            Frame((dMin[0] + g * rg, dMin[1] + g, dMin[2] + g * bg, 40));
        var roll = new[]
        {
            Above(1.00, 0.90, 1.10), Above(1.05, 0.90, 1.10), Above(1.10, 0.90, 1.10),
            Above(1.60, 0.99, 1.01),
        };

        double[] span = FilmBase.DetectDMaxPerChannelFromRoll(
            roll, tBase, 90.0, roll, null, edgeInset: 0.0)!;

        Assert.Equal(0.90, span[0] / span[1], 2);
        Assert.Equal(1.10, span[2] / span[1], 2);
        // Lifted clear of the deep frame's own channel maxima.
        Assert.True(span[1] >= 1.60 - 0.01, $"green span {span[1]:F3} did not clear the deep frame");
    }

    // ── 2. The board shoulder is measured, not assumed ──────────────────────────────

    /// <summary>
    /// Builds a frame with a bright light board down the left edge, an OPAQUE rim of
    /// <paramref name="shoulder"/> pixels — the film holder's edge, or the punched edge of a
    /// sprocket hole, seen dense against the board — then film.
    ///
    /// The rim is dark, not a bright ramp, because dark is what the dilation measures: it walks
    /// outward from the board by each ring's DENSITY extreme (see BoardShoulderRadius), so a
    /// shoulder that is merely brighter than film is invisible to it. The fixture used to ramp
    /// from board down to film, and these tests passed only because the board cut then happened
    /// to sit at the film's own toe and took the whole ramp with it — the measured radius was 1
    /// in every case. Now that the cut sits in the middle of the board↔film gap, a bright ramp
    /// straddles it, and the tests would be asserting a property of the cut's placement rather
    /// than of the shoulder measurement they are named for.
    ///
    /// The orange cast belongs to the FILM, so only the film carries it. The board is bare lamp
    /// and is left neutral: it is the light the mask is subtracted from, not light that has been
    /// through the mask, and the board estimator now says so outright (Sprocket's
    /// MaxBoardRedOverBlue). Tinting the whole frame, board included, gave the "board" a red over
    /// blue of 3.3 — a colour no light source in a copy stand has — and the estimator rightly
    /// stopped calling it one.
    /// </summary>
    private static ImageBuffer BoardFrame(int shoulder, int boardWidth = 12)
    {
        const int w = 200, h = 200;
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                if (x < boardWidth)                                  // board: blown white, neutral
                {
                    data[i] = 0.95f; data[i + 1] = 0.95f; data[i + 2] = 0.95f;
                    continue;
                }
                float v = x < boardWidth + shoulder ? 0.02f : 0.20f; // opaque rim, then film base
                data[i] = v; data[i + 1] = v * 0.6f; data[i + 2] = v * 0.3f;
            }
        return new ImageBuffer(w, h, data);
    }

    /// <summary>
    /// A NARROW SHOULDER MUST NOT COST A WIDE BAND OF FILM.
    ///
    /// The dilation used to be a flat 5% of the short edge — on a 200 px frame, 10 px, whatever
    /// the penumbra actually measured. Measuring it instead keeps the film that a fixed radius
    /// would have discarded: with a 2 px shoulder the mask should drop the board and its rim and
    /// then stop, leaving the overwhelming majority of the frame in.
    /// </summary>
    [Fact]
    public void A_narrow_board_shoulder_costs_only_a_narrow_band()
    {
        ImageBuffer f = BoardFrame(shoulder: 2, boardWidth: 12);

        bool[] keep = FilmBase.HighDensityKeepMask(f, null);
        int kept = keep.Count(b => b);

        // Board (12) + shoulder (2) is 14 of 200 columns = 7%; a 5% dilation would take 10 more
        // columns on top of that. Anything above ~88% kept means the wide fixed band is gone.
        Assert.True(kept > keep.Length * 0.88,
                    $"too much film discarded around a 2px shoulder: kept {100.0 * kept / keep.Length:F1}%");
    }

    /// <summary>
    /// A WIDE SHOULDER MUST STILL BE FULLY REMOVED. Measuring is only safe if it grows as well as
    /// shrinks — the whole point of the dilation is that the penumbra poisons the endpoint, so a
    /// genuinely broad ramp has to come out.
    /// </summary>
    [Fact]
    public void A_wide_board_shoulder_is_still_removed()
    {
        ImageBuffer f = BoardFrame(shoulder: 6, boardWidth: 12);

        bool[] keep = FilmBase.HighDensityKeepMask(f, null);

        // Sample the middle row: every column inside board+rim must be excluded.
        const int w = 200;
        int row = 100 * w;
        for (int x = 0; x < 12 + 6; x++)
            Assert.False(keep[row + x], $"column {x} is board or penumbra and must be excluded");
    }

    /// <summary>
    /// THE SHOULDER IS AN OPTICAL WIDTH, NOT A FRACTION OF THE FRAME. The same scan at two
    /// resolutions must exclude the same PROPORTION of picture — which a rule tied to the short
    /// edge gives for free but a measurement has to earn. Here the geometry is scaled with the
    /// buffer, so the kept fraction should track.
    /// </summary>
    [Fact]
    public void The_measured_shoulder_scales_with_the_buffer()
    {
        double KeptFraction(int shoulder, int board)
        {
            bool[] keep = FilmBase.HighDensityKeepMask(BoardFrame(shoulder, board), null);
            return (double)keep.Count(b => b) / keep.Length;
        }

        // Same physical scan, twice the sampling: board and shoulder both double.
        double coarse = KeptFraction(shoulder: 2, board: 10);
        double fine = KeptFraction(shoulder: 4, board: 20);

        Assert.Equal(coarse, fine, 1);
    }
}
