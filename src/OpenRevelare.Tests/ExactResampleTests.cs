using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="Resample.ToLongEdge"/>, the export's scaler. Its whole reason to exist is that the
/// requested size is a requirement rather than a suggestion, so that is what most of this asserts;
/// the rest pins the two properties an export would otherwise lose quietly — a flat field staying
/// flat, and the picture not sliding half a pixel.
/// </summary>
public sealed class ExactResampleTests
{
    private static ImageBuffer Fill(int w, int h, Func<int, int, (float R, float G, float B)> f)
    {
        var img = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                int i = (y * w + x) * 3;
                img.Data[i] = r; img.Data[i + 1] = g; img.Data[i + 2] = b;
            }
        return img;
    }

    private static ImageBuffer Flat(int w, int h, float v) => Fill(w, h, (_, _) => (v, v, v));

    [Theory]
    // The case that motivated this: an integer box factor lands on 1943 here, not 2048.
    [InlineData(5832, 3888, 2048, 2048, 1365)]
    [InlineData(3888, 5832, 2048, 1365, 2048)]
    [InlineData(6000, 6000, 1024, 1024, 1024)]
    public void Puts_the_long_edge_exactly_where_it_was_asked(
        int w, int h, int longEdge, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), Resample.LongEdgeSize(w, h, longEdge, allowUpscale: false));
    }

    /// <summary>What the buffer comes back as must be what the dialog was told it would be.</summary>
    [Theory]
    [InlineData(729, 486, 256)]
    [InlineData(486, 729, 256)]
    [InlineData(400, 300, 900)]
    public void Delivers_the_size_it_reports(int w, int h, int longEdge)
    {
        var (pw, ph) = Resample.LongEdgeSize(w, h, longEdge, allowUpscale: true);

        ImageBuffer scaled = Resample.ToLongEdge(Flat(w, h, 0.5f), longEdge, allowUpscale: true);

        Assert.Equal((pw, ph), (scaled.Width, scaled.Height));
        Assert.Equal(longEdge, Math.Max(scaled.Width, scaled.Height));
    }

    [Fact]
    public void Leaves_a_picture_that_is_already_smaller_alone()
    {
        ImageBuffer src = Flat(800, 600, 0.25f);

        // Reference identity, not just equal pixels: callers skip work on it.
        Assert.Same(src, Resample.ToLongEdge(src, 2048, allowUpscale: false));
    }

    [Fact]
    public void Enlarges_it_when_upscaling_is_allowed()
    {
        ImageBuffer src = Flat(800, 600, 0.25f);

        ImageBuffer up = Resample.ToLongEdge(src, 1600, allowUpscale: true);

        Assert.Equal((1600, 1200), (up.Width, up.Height));
    }

    /// <summary>
    /// The claim in the doc comment: a request that happens to be an integer factor is the box
    /// average, so switching the export over to this changed the SIZE it can hit and nothing about
    /// what an already-achievable size produces.
    /// </summary>
    [Fact]
    public void Reproduces_the_box_average_on_an_integer_factor()
    {
        ImageBuffer src = Fill(64, 48, (x, y) => (x / 64f, y / 48f, (x + y) / 112f));

        ImageBuffer box = Resample.Box(src, 16);
        ImageBuffer exact = Resample.ToLongEdge(src, 16, allowUpscale: false);

        Assert.Equal((box.Width, box.Height), (exact.Width, exact.Height));
        for (int i = 0; i < box.Data.Length; i++)
            Assert.Equal(box.Data[i], exact.Data[i], 6);
    }

    [Theory]
    [InlineData(320)]    // down
    [InlineData(1280)]   // up
    public void Keeps_a_flat_field_flat(int longEdge)
    {
        ImageBuffer scaled = Resample.ToLongEdge(Flat(640, 480, 0.375f), longEdge, allowUpscale: true);

        foreach (float v in scaled.Data) Assert.Equal(0.375f, v, 5);
    }

    /// <summary>
    /// A half-pixel shift is the classic enlargement bug and it is invisible on anything but a
    /// pattern: a mirror-symmetric source must stay mirror-symmetric, whichever way it is scaled.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(300)]
    public void Does_not_slide_the_picture_sideways(int longEdge)
    {
        ImageBuffer src = Fill(120, 40, (x, _) =>
        {
            float v = Math.Min(x, 119 - x) / 60f;      // symmetric about the centre
            return (v, v, v);
        });

        ImageBuffer scaled = Resample.ToLongEdge(src, longEdge, allowUpscale: true);

        for (int y = 0; y < scaled.Height; y++)
            for (int x = 0; x < scaled.Width / 2; x++)
            {
                int left = (y * scaled.Width + x) * 3;
                int right = (y * scaled.Width + (scaled.Width - 1 - x)) * 3;
                Assert.Equal(scaled.Data[left], scaled.Data[right], 5);
            }
    }

    /// <summary>
    /// The lattice travels with the pixels (<see cref="ImageBuffer.SourceQuantisationStep"/>): it
    /// describes the FILE, and resizing rearranges samples without telling us more about the film.
    /// </summary>
    [Fact]
    public void Carries_the_source_quantisation_step()
    {
        ImageBuffer src = Flat(640, 480, 0.5f);
        src.SourceQuantisationStep = 1.0 / 255.0;

        Assert.Equal(src.SourceQuantisationStep,
                     Resample.ToLongEdge(src, 320, allowUpscale: false).SourceQuantisationStep);
    }
}
