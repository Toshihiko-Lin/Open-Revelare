using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmAutoCropClippedRowsTests
{
    // Reproduces the 5219 capture geometry: complete gate, partial hole rows, no outer
    // film edge and less than one perforation pitch outside the left/right gate edges.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void Partial_perforations_keep_the_real_gate_orientation_and_refuse_missing_outer_film(bool positive, int turns)
    {
        var image = Geometry.ApplyOrientation(TightCapture(positive), turns, false, false);
        var d = FilmAutoCrop.Detect(image);
        Assert.NotNull(d);
        Assert.True(d.HasPerforationRuler);
        Assert.Equal(positive ? FilmPolarity.Positive : FilmPolarity.Negative, d.Polarity);
        Assert.Equal(turns % 2 == 0, d.Image.W * image.Width > d.Image.H * image.Height);
        double width = turns % 2 == 0 ? d.Image.W * image.Width : d.Image.H * image.Height;
        Assert.InRange(width, 350, 370);
        Assert.NotNull(FilmAutoCrop.Plan(new[] { d }, FilmCropMode.ImageArea)[0]);
        Assert.Null(FilmAutoCrop.Plan(new[] { d }, FilmCropMode.WithSprockets)[0]);
    }

    [Theory]
    [InlineData(-1.5)]
    [InlineData(1.5)]
    public void Clipped_rows_measure_tilt_from_inner_edges_instead_of_visible_centroids(double angle)
    {
        var image = TightCapture(false, angle);
        var d = FilmAutoCrop.Detect(image);
        Assert.NotNull(d);
        Assert.True(d.HasPerforationRuler);
        Assert.InRange(d.RotationCorrection, -angle - .35, -angle + .35);
    }

    internal static ImageBuffer TightCapture(bool positive, double angle = 0)
    {
        var source = FilmAutoCropTests.Perforated(positive);
        if (angle != 0) source = Geometry.ApplyRotation(source, angle, fill: 0);
        var image = new ImageBuffer(400, 290);
        for (int y = 0; y < image.Height; y++)
            Array.Copy(source.Data, ((y + 96) * source.Width + 130) * 3, image.Data, y * image.Width * 3, image.Width * 3);
        return image;
    }
}
