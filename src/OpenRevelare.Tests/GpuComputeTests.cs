using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

// General CI may have no hardware GPU. Set OPENREVELARE_REQUIRE_GPU=1 for real-driver
// acceptance on Windows/macOS; initialization/compilation errors must then fail tests.
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENREVELARE_REQUIRE_GPU") != "1")
            Skip = "Requires a hardware GPU; set OPENREVELARE_REQUIRE_GPU=1 to run.";
    }
}

public sealed class GpuComputeTests
{
    [GpuFact]
    public void Gpu_resample_matches_cpu_for_area_enlargement_borders_and_single_pixel_axes()
    {
        using var session = new GpuComputeSession();
        foreach (var (w, h, edge) in new[] { (257, 129, 113), (47, 23, 83), (1, 7, 13), (17, 1, 9), (5, 5, 1) })
        {
            var source = Fixture(w, h);
            var (dw, dh) = Resample.LongEdgeSize(w, h, edge, true);
            float[] before = (float[])source.Data.Clone();
            float[] actual = session.Resize(source, dw, dh);
            // Small fixtures always exercise the CPU path under the default Auto policy.
            float[] reference = Resample.ToLongEdge(source, edge, true).Data;
            Assert.True(GpuAcceleration.Equivalent(reference, actual), $"Mismatch at {w}x{h} -> {dw}x{dh}");
            Assert.Equal(before, source.Data);
        }
    }

    [GpuFact]
    public void Gpu_sharpen_matches_cpu_including_threshold_white_fill_and_nonfinite_samples()
    {
        using var session = new GpuComputeSession();
        foreach (var (sigma, amount, level) in new[] {
            (0.5f, 0.35f, OutputSharpen.Level.Low), (0.7f, 0.65f, OutputSharpen.Level.Standard),
            (0.9f, 1f, OutputSharpen.Level.High) })
        {
            var source = Fixture(257, 129);
            // Exercise white fill, finite HDR/negative values and a nonfinite neighbourhood.
            Array.Fill(source.Data, 1f, 0, 257 * 3);
            source.Data[257 * 3 + 15] = float.NaN;
            float[] before = (float[])source.Data.Clone();
            float[] actual = session.Sharpen(source, Gaussian(sigma), amount, 2f / 255f, 0.12f);
            Assert.Equal(before, source.Data);
            OutputSharpen.Apply(source, level);
            Assert.True(GpuAcceleration.Equivalent(source.Data, actual));
        }
    }

    [GpuFact]
    public void Device_buffers_can_grow_and_shrink_between_operations()
    {
        using var session = new GpuComputeSession();
        foreach (int edge in new[] { 129, 513, 65, 257 })
        {
            var source = Fixture(edge, edge / 2);
            float[] resized = session.Resize(source, edge / 2, edge / 4);
            Assert.All(resized, sample => Assert.True(float.IsFinite(sample)));
            float[] sharpened = session.Sharpen(source, Gaussian(0.7f), 0.65f, 2f / 255f, 0.12f);
            var expected = Fixture(edge, edge / 2);
            OutputSharpen.Apply(expected, OutputSharpen.Level.Standard);
            Assert.True(GpuAcceleration.Equivalent(expected.Data, sharpened));
        }
    }

    [Fact]
    public void Parity_check_rejects_corruption_and_accepts_equivalent_nonfinite_values()
    {
        Assert.True(GpuAcceleration.Equivalent([0f, -0.1f, 4f, float.NaN, float.PositiveInfinity],
            [0f, -0.1f, 4f, float.NaN, float.PositiveInfinity]));
        Assert.False(GpuAcceleration.Equivalent([0f], [float.NaN]));
        Assert.False(GpuAcceleration.Equivalent([float.PositiveInfinity], [float.NegativeInfinity]));
        Assert.False(GpuAcceleration.Equivalent([0.5f], [0.51f]));
        Assert.False(GpuAcceleration.Equivalent([0f], [0f, 1f]));
    }

    private static ImageBuffer Fixture(int width, int height)
    {
        var image = new ImageBuffer(width, height);
        var random = new Random(42);
        for (int i = 0; i < image.Data.Length; i++) image.Data[i] = random.NextSingle() * 1.4f - 0.1f;
        return image;
    }

    private static float[] Gaussian(float sigma)
    {
        int radius = (int)MathF.Ceiling(sigma * 3f);
        var result = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            result[i + radius] = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            sum += result[i + radius];
        }
        for (int i = 0; i < result.Length; i++) result[i] /= sum;
        return result;
    }
}
