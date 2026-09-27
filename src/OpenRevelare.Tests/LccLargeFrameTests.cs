using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class LccLargeFrameTests
{
    [Fact]
    public void Large_frame_region_uses_the_same_global_align_corners_sampling()
    {
        // 4097 selects the non-cached large-frame path without making the test itself large.
        // The slice sits around the horizontal midpoint and spans both rows of a 3-row frame.
        var flat = new ImageBuffer(2, 2, new float[]
        {
            1f, 2f, 4f,   3f, 4f, 8f,
            5f, 6f, 12f,  7f, 8f, 16f,
        });
        const int width = 5, height = 2;
        var pixels = Enumerable.Repeat(24f, width * height * 3).ToArray();
        var region = new FrameRegion(2046, 1, 4097, 3);

        Lcc.Apply(pixels, width, height, flat, region);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        for (int c = 0; c < 3; c++)
        {
            double gx = (2046 + x) / 4096.0;
            double gy = (1 + y) / 2.0;
            double top = flat.Data[c] * (1.0 - gx) + flat.Data[3 + c] * gx;
            double bottom = flat.Data[6 + c] * (1.0 - gx) + flat.Data[9 + c] * gx;
            double divisor = top * (1.0 - gy) + bottom * gy;
            Assert.Equal(24.0 / divisor, pixels[(y * width + x) * 3 + c], 5);
        }
    }

    [Fact]
    public void Large_frame_direct_sampling_clamps_a_guard_region_at_frame_edges()
    {
        var flat = new ImageBuffer(2, 2, new float[]
        {
            1f, 1f, 1f,  2f, 2f, 2f,
            3f, 3f, 3f,  4f, 4f, 4f,
        });
        var pixels = Enumerable.Repeat(8f, 3 * 2 * 3).ToArray();
        // Region bounds normally stay inside the frame. Guard pixels used by a resampler can
        // touch an edge, though, and must extend the edge rather than index outside the field.
        var region = new FrameRegion(4095, 4096, 4097, 4097);

        Lcc.Apply(pixels, 3, 2, flat, region);

        Assert.Equal(8f / 4f, pixels[(0 * 3 + 1) * 3], 5);
        Assert.Equal(8f / 4f, pixels[(1 * 3 + 2) * 3], 5);
    }
}
