using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmAutoCropTests
{
    // A landscape camera file can contain a portrait negative. The ratio belongs to the
    // exposed frame, not to the sensor, and must survive every discrete orientation.
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(3, true, true)]
    public void Detects_frame_instead_of_holder_and_preserves_orientation(int turns, bool flipH, bool flipV)
    {
        var pixels = Geometry.ApplyOrientation(Negative(), turns, flipH, flipV);
        var d = FilmAutoCrop.Detect(pixels);
        Assert.NotNull(d);
        Assert.InRange(d.Image.W * pixels.Width, turns % 2 == 0 ? 355 : 235, turns % 2 == 0 ? 365 : 245);
        foreach (var mode in Enum.GetValues<FilmCropMode>())
        {
            var r = FilmAutoCrop.Plan(new[] { d }, mode)[0];
            Assert.NotNull(r);
            double ratio = r.Value.W * pixels.Width / (r.Value.H * pixels.Height);
            double expected = mode == FilmCropMode.ImageArea ? 1.5 : 37.0 / 35;
            Assert.Equal(turns % 2 == 0 ? expected : 1 / expected, ratio, 10);
        }
    }

    [Fact]
    public void Blank_or_nonfinite_pixels_do_not_invent_a_crop()
    {
        var blank = new ImageBuffer(120, 100);
        Array.Fill(blank.Data, .4f);
        Assert.Null(FilmAutoCrop.Detect(blank));
        blank.Data[180] = float.NaN;
        Assert.Null(FilmAutoCrop.Detect(blank));
    }

    [Fact]
    public void Roll_uses_one_size_and_independent_centres_and_rejects_scale_outlier()
    {
        var frames = new[] { D(.2, .3, .5), D(.22, .31, .505), D(.18, .28, .495), D(.1, .1, .8), null };
        foreach (var mode in Enum.GetValues<FilmCropMode>())
        {
            var crops = FilmAutoCrop.Plan(frames, mode);
            Assert.Null(crops[3]); Assert.Null(crops[4]);
            for (int i = 0; i < 3; i++)
            {
                var crop = crops[i]!.Value;
                Assert.Equal(crops[0]!.Value.W, crop.W);
                Assert.Equal(crops[0]!.Value.H, crop.H);
                // An exactly half-pixel centre can round a few double ULPs above the bound.
                Assert.InRange(Math.Abs(frames[i]!.Image.X + frames[i]!.Image.W / 2 - crop.X - crop.W / 2), 0, .5 / frames[i]!.Width + 1e-12);
                int width = (int)(Math.Round((crop.X + crop.W) * 6000) - Math.Round(crop.X * 6000));
                int height = (int)(Math.Round((crop.Y + crop.H) * 4000) - Math.Round(crop.Y * 4000));
                Assert.Equal(mode == FilmCropMode.ImageArea ? 1.5 : 37.0 / 35, (double)width / height, 10);
            }
        }
    }

    [Fact]
    public void Sprocket_crop_refuses_missing_film_edges_instead_of_shrinking_or_moving()
    {
        var cropped = D(.2, .02, .6);
        Assert.NotNull(FilmAutoCrop.Plan(new[] { cropped }, FilmCropMode.ImageArea)[0]);
        Assert.Null(FilmAutoCrop.Plan(new[] { cropped }, FilmCropMode.WithSprockets)[0]);
    }

    [Fact]
    public void Repeated_runs_and_mixed_source_sizes_do_not_change_the_ratio()
    {
        var a = D(.2, .25, .5);
        var b = a with { Width = 3000, Height = 2000 };
        var plan = FilmAutoCrop.Plan(new[] { a, b }, FilmCropMode.WithSprockets);
        Assert.Equal(plan, FilmAutoCrop.Plan(new[] { a, b }, FilmCropMode.WithSprockets));
        Assert.Equal(37.0 / 35, plan[1]!.Value.W * 3000 / (plan[1]!.Value.H * 2000), 10);
    }

    // Slides have a dark base; negatives have a bright base. Both must use the same
    // perforation ruler even when rotation changes the bright components at the scan edge.
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(false, -2.0)]
    [InlineData(true, 0.0)]
    [InlineData(true, 2.0)]
    public void Perforations_locate_both_polarities_and_correct_small_rotation(bool positive, double angle)
    {
        var source = Perforated(positive);
        var d = FilmAutoCrop.Detect(Geometry.ApplyRotation(source, angle));
        Assert.NotNull(d);
        Assert.Equal(positive ? FilmPolarity.Positive : FilmPolarity.Negative, d.Polarity);
        Assert.InRange(d.RotationCorrection, -angle - .25, -angle + .25);
        Assert.InRange(d.Image.X * 640, 145, 155);
        Assert.InRange(d.Image.Y * 480, 115, 125);
        Assert.InRange(d.Image.W * 640, 350, 370);
    }

    internal static ImageBuffer Perforated(bool positive)
    {
        var image = new ImageBuffer(640, 480);
        Array.Fill(image.Data, .9f);
        for (int y = 65; y < 415; y++) for (int x = 0; x < 640; x++)
        {
            bool picture = x >= 150 && x < 510 && y >= 120 && y < 360;
            float value = picture ? .3f + .04f * (float)Math.Sin(x * .04) : positive ? .025f : .65f;
            int i = (y * 640 + x) * 3;
            image.Data[i] = value; image.Data[i + 1] = value * .6f; image.Data[i + 2] = value * .25f;
        }
        // 28.17 mm row separation at 10 px/mm; the old 29.5 mm fixture masked the bug.
        for (int hole = 0; hole < 13; hole++) foreach (double centre in new[] { 99.15, 380.85 })
        {
            int cx = (int)Math.Round(32 + hole * 47.5), cy = (int)Math.Round(centre);
            for (int y = cy - 14; y < cy + 14; y++) for (int x = cx - 10; x < cx + 10 && x < 640; x++)
                for (int c = 0; c < 3; c++) image.Data[(y * 640 + x) * 3 + c] = .9f;
        }
        return image;
    }

    private static FilmAutoCrop.Detection D(double x, double y, double width)
        => new(new(x, y, width, width), 1, 6000, 4000);

    private static ImageBuffer Negative()
    {
        var image = new ImageBuffer(640, 480);
        for (int y = 40; y < 440; y++) for (int x = 25; x < 615; x++)
        {
            float v = x >= 150 && x < 510 && y >= 120 && y < 360 ? .23f + .05f * (float)Math.Sin(x * .04) : .65f;
            int p = (y * 640 + x) * 3;
            image.Data[p] = v; image.Data[p + 1] = v * .6f; image.Data[p + 2] = v * .25f;
        }
        return image;
    }
}
