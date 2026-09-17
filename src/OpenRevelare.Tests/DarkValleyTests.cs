using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The dark end of <see cref="Sprocket.EstimateDarkValley"/> on a DIM frame — one whose film
/// reaches only a quarter of the scale, as a copy stand exposed to keep the light board out of
/// clipping produces.
///
/// The estimator bins the frame's luma and looks for a valley between the opaque surround and
/// the picture's densest tones. Binned over the fixed [0, 1] a dim frame gets a quarter of the
/// bins, the two populations land a few bins apart, and the smoothing folds them into one hump;
/// the valley test then fails and the surround goes into every statistic. Binned over the film's
/// own range they separate. These tests pin that, and that the finer binning invents nothing on a
/// frame with no surround at all.
/// </summary>
public class DarkValleyTests
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
    /// The picture of a dim frame: a body that thins smoothly from the film's top at 0.27 down
    /// to 0.02, with a broad, dense highlight population at the bottom of it (0.02-0.04) — the
    /// tones that sit closest to the surround and are the ones the smoothing folds into it.
    /// </summary>
    private static float Picture(int x, int y, Lcg rng)
    {
        double u = (y % 50) / 50.0;                                   // repeats, so every strip has the same tones
        if (x < 24) return (float)(0.02 + 0.02 * (rng.Next() + 1) / 2);   // dense highlights
        return (float)(0.02 + 0.25 * u * u + 0.004 * rng.Next());          // body, thinning upward
    }

    /// <summary>
    /// THE CARRIER'S BLACK IS CUT, ON A FRAME THAT REACHES ONLY 0.27.
    ///
    /// A film carrier's opaque frame across the top and bottom, 11% of the frame at 0.0005 —
    /// what a GFX copy stand recorded on "135 ecn2 1142", where the fixed-range histogram missed
    /// it on seven frames in ten. The valley must land between the carrier and the picture's
    /// densest tone, so that exactly the carrier is below it.
    /// </summary>
    [Fact]
    public void An_opaque_carrier_on_a_dim_frame_is_cut()
    {
        ImageBuffer f = Build((x, y, rng) =>
        {
            if (y < 11 || y >= H - 11) return (float)(0.0005 + 0.0003 * rng.Next());   // carrier
            return Picture(x, y, rng);
        });

        double cut = Sprocket.EstimateDarkValley(f);

        Assert.True(cut > Sprocket.NoMaskDark, "the carrier was not found at all");
        Assert.InRange(cut, 0.001, 0.02);
    }

    /// <summary>
    /// THE SAME PICTURE WITHOUT A CARRIER HAS NO MASK. The finer binning must not turn the
    /// picture's own dense highlights into a surround: they are picture, and cutting them would
    /// hand the highlight endpoint to the wrong pixels.
    /// </summary>
    [Fact]
    public void A_dim_frame_without_a_carrier_has_no_mask()
    {
        ImageBuffer f = Build(Picture);

        Assert.Equal(Sprocket.NoMaskDark, Sprocket.EstimateDarkValley(f));
    }
}
