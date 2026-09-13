using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The shape of the light-board cluster in <see cref="Sprocket.EstimateSprocketThreshold"/>'s
/// histogram, and where its foot is taken to be.
///
/// The estimator walks down from the board's peak to the bin where the histogram stops falling,
/// and everything below that walk — film top, valley, the rejection tests — is measured from
/// where it lands. A board is not always one hump: with the green channel clipped and red and
/// blue drifting across the panel (18-KODAK GOLD200), it is two humps with a populated fold
/// between them, and a walk that stops in the fold reports an empty gap and a boardless roll.
/// These tests pin the two halves of the rule that fixes that: a populated fold close under the
/// peak is crossed, and a fold in front of a TALLER hump is not, because that is a photograph's
/// highlight mode sitting on its body.
/// </summary>
public class SprocketThresholdTests
{
    private const int W = 200, H = 200;

    /// <summary>Deterministic uniform noise in [-1, 1) — the tests must not depend on a seed.</summary>
    private sealed class Lcg
    {
        private uint _s = 12345;
        public double Next() { _s = _s * 1664525u + 1013904223u; return _s / 2147483648.0 - 1.0; }
    }

    private static ImageBuffer Build(System.Func<int, int, Lcg, float> luma)
    {
        var rng = new Lcg();
        var data = new float[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float v = luma(x, y, rng);
                int i = (y * W + x) * 3;
                data[i] = v; data[i + 1] = v; data[i + 2] = v;
            }
        return new ImageBuffer(W, H, data);
    }

    /// <summary>
    /// A BOARD WITH A GRADIENT IS STILL ONE BOARD.
    ///
    /// A strip of bare panel down the left edge whose brightness is bimodal — a third of it at
    /// 0.58, the rest at 0.62, spread so the two humps overlap and the fold between them is
    /// populated. The
    /// film base sits at 0.20 with the picture below it; the gap from 0.21 to 0.55 is empty. The
    /// cut must land in that gap, not in the fold: a walk that stopped in the fold used to leave
    /// film top, foot and valley all in the board and report NoBoard.
    /// </summary>
    [Fact]
    public void A_bimodal_board_is_cut_below_the_whole_board()
    {
        ImageBuffer f = Build((x, y, rng) =>
        {
            if (x < 24) return (float)((x < 8 ? 0.58 : 0.62) + 0.012 * rng.Next());    // panel, two humps
            if (x < 40 || y < 20 || y >= H - 20) return (float)(0.20 + 0.006 * rng.Next()); // base
            return (float)(0.10 + 0.02 * rng.Next());                                    // picture
        });

        double thr = Sprocket.EstimateSprocketThreshold(f);

        Assert.True(thr < Sprocket.NoBoard, "the board was not found at all");
        // Anywhere in the gap: above the base's brightest pixel (0.206), below the panel's
        // dimmest (0.568). The synthetic gap is perfectly empty, so the walk runs down its zero
        // bins to the base's edge and the cut sits just above it; a real scan's hole-edge tail
        // stops it higher up.
        Assert.InRange(thr, 0.21, 0.55);
    }

    /// <summary>
    /// A HIGHLIGHT MODE ON A TALLER BODY IS STILL A PHOTOGRAPH.
    ///
    /// Same fold, opposite hump: a small bright patch (0.55-0.61) whose lower edge runs straight
    /// into the picture body, a much taller population that thins smoothly from 0.56 down to
    /// 0.24 — built from its histogram, so the flank has no fold of its own — with a separated
    /// block of shadow at 0.05 below, so there IS an empty gap lower down for a careless walk to
    /// find. Crossing the fold here would carry the foot down that flank to the gap, and every
    /// rejection test then passes — the "board" is the whole picture, edge-connected and
    /// brighter than 0.55 — so the frame would be cut at 0.06 and painted almost entirely white.
    /// The walk must stop in the fold, as it always did.
    /// </summary>
    [Fact]
    public void A_highlight_mode_on_a_taller_body_is_not_a_board()
    {
        const double a = 9.36, span = 0.32;               // body density ∝ e^(a·v): 20× thinner at the bottom
        double tail = System.Math.Exp(-a * span);
        int body = 0, patch = 0;
        ImageBuffer f = Build((x, y, _) =>
        {
            if (x >= 60 && x < 140 && y >= 60 && y < 140) return 0.05f;                          // shadow block
            if (x >= 150 && x < 190 && y >= 20 && y < 50) return (float)(0.55 + 0.06 * ((patch++ + 0.5) / 1200.0)); // highlight
            double u = (body++ + 0.5) / (W * H - 6400 - 1200);
            return (float)(0.56 + System.Math.Log(tail + u * (1 - tail)) / a);                    // body, inverse CDF
        });

        Assert.Equal(Sprocket.NoBoard, Sprocket.EstimateSprocketThreshold(f));
    }
}
