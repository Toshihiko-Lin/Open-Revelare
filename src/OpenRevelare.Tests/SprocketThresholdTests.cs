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

    private static ImageBuffer Build(System.Func<int, int, Lcg, float> luma) => Build(luma, 1, 1, 1);

    /// <summary>
    /// The same scene under a channel tint. The weights are applied to every pixel and must
    /// average to 1, so the LUMA histogram — the whole of what the estimator reads apart from the
    /// colour test — is bit for bit the grey version's. A tinted case therefore differs from its
    /// grey twin in exactly one thing, which is what makes the pair a test of the colour rule.
    /// </summary>
    private static ImageBuffer Build(System.Func<int, int, Lcg, float> luma, float r, float g, float b)
    {
        var rng = new Lcg();
        var data = new float[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float v = luma(x, y, rng);
                int i = (y * W + x) * 3;
                data[i] = v * r; data[i + 1] = v * g; data[i + 2] = v * b;
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

    /// <summary>
    /// A FULL-BLEED SCAN HAS NO HARDWARE IN IT AT ALL.
    ///
    /// A lab TIFF cropped to the picture: no rebate, no holes, no board, one population filling
    /// the frame. Its luma runs from a mode at 0.17 down a flank that never turns back up, so the
    /// walk off the top cluster runs all the way to the bottom bin, the "film" it lands on is the
    /// picture's own shadow tail and the cut comes out at the 0.1 floor — which masks and whitens
    /// all but the darkest few percent of the frame. Four frames of the 7344 PORTRA400 roll read
    /// exactly this way, and since the roll pass keeps the HIGHEST cut of its sample frames, the
    /// one frame the user happened to open was enough to whiten the roll.
    ///
    /// Nothing about the histogram's SHAPE says no here — the gap is real, the valley is deep,
    /// the lit region reaches every edge because it is the picture. What says no is how much of
    /// the frame the "board" claims: at 0.96 there is no film left for it to be the hardware
    /// around.
    /// </summary>
    [Fact]
    public void A_full_bleed_picture_is_not_a_board()
    {
        const double top = 0.34, pedestal = 0.10;   // triangular picture on [0, 0.34], mode 0.17
        int i = 0;
        ImageBuffer f = Build((x, y, _) =>
        {
            double u = (i++ + 0.5) / (W * H);
            // A thin uniform floor under the picture: it is what keeps the bottom bin populated,
            // and so what lets the walk treat it as film rather than stopping above it.
            if (u < pedestal) return (float)(top * (u / pedestal));
            double v = (u - pedestal) / (1 - pedestal);
            return (float)(v <= 0.5
                ? top * System.Math.Sqrt(v / 2)
                : top * (1 - System.Math.Sqrt((1 - v) / 2)));
        });

        Assert.Equal(Sprocket.NoBoard, Sprocket.EstimateSprocketThreshold(f));
    }

    /// <summary>
    /// A BOARD IS WHITE OR GREY, NEVER ORANGE.
    ///
    /// <see cref="A_bimodal_board_is_cut_below_the_whole_board"/>'s scene under a tint that leaves
    /// its luma histogram untouched and puts red 3.4× over blue — the signature of the orange mask,
    /// which is to say of film. Everything the shape tests look at is identical to the grey case
    /// that must keep detecting, so this is the colour rule on its own: a bare lamp is the light
    /// itself and cannot come out the colour of the film in front of it.
    /// </summary>
    [Fact]
    public void An_orange_cluster_is_film_not_a_board()
    {
        ImageBuffer f = Build((x, y, rng) =>
        {
            if (x < 24) return (float)((x < 8 ? 0.58 : 0.62) + 0.012 * rng.Next());
            if (x < 40 || y < 20 || y >= H - 20) return (float)(0.20 + 0.006 * rng.Next());
            return (float)(0.10 + 0.02 * rng.Next());
        }, 1.62f, 0.90f, 0.48f);

        Assert.Equal(Sprocket.NoBoard, Sprocket.EstimateSprocketThreshold(f));
    }
}
