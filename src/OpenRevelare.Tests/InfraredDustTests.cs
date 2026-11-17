using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class InfraredDustTests
{
    [Fact]
    public void Smooth_shading_is_preserved_and_absorption_is_selected()
    {
        const int size = 80;
        var plane = Enumerable.Range(0, size * size).Select(i => .6f + i % size * .002f).ToArray();
        Assert.Equal(0, InfraredDust.Detect(plane, size, size).PixelCount);
        plane[40 * size + 40] = .1f;
        var mask = InfraredDust.Detect(plane, size, size);
        Assert.Equal(9, mask.PixelCount);
        Assert.Contains(mask.EnumerateRuns(), run => run.Y == 40 && run.Start == 39 && run.End == 42);
    }

    [Fact]
    public void Uniform_dark_plane_is_not_dust()
    {
        Assert.Equal(0, InfraredDust.Detect(new float[400], 20, 20).PixelCount);
    }

    [Fact]
    public void Cancelled_detection_stops()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        Assert.Throws<OperationCanceledException>(() => InfraredDust.Detect(new float[400], 20, 20, ct.Token));
    }
}
