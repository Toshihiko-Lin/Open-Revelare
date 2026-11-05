using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmAutoCropReferenceTests
{
    [Fact]
    public void Portrait_scene_fallback_cannot_bypass_landscape_roll_reference()
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(false))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        var internalWindow = new FilmAutoCrop.Detection(new(.2, .2, .3, .6), 1, 640, 480);
        Assert.True(FilmAutoCrop.NeedsRollReference(internalWindow, refs));
        Assert.True(FilmAutoCrop.NeedsRollReference(clear with { FilmWidth = clear.FilmWidth * .7 }, refs));
    }

    [Fact]
    public void Local_perforations_can_corroborate_tilt_outside_the_roll_range_without_losing_saved_straightening()
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(false))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear, clear with { RotationCorrection = 3 } });
        Assert.Single(refs);
        Assert.False(FilmAutoCrop.NeedsRollReference(clear with { RotationCorrection = 3 }, refs));
        Assert.False(FilmAutoCrop.NeedsRollReference(clear with { AppliedRotation = 2, RotationCorrection = -2 }, refs));
        var tilted = Geometry.ApplyRotation(OneEdge(false), 1.5, fill: 0);
        var locallyConfirmed = FilmAutoCrop.DetectWithRollReference(tilted, refs, 640, 480);
        Assert.NotNull(locallyConfirmed);
        Assert.InRange(locallyConfirmed.RotationCorrection, -1.8, -1.2);
        var corrected = FilmAutoCrop.DetectWithRollReference(tilted, refs, 640, 480, appliedRotation: 1.5);
        Assert.NotNull(corrected);
        Assert.InRange(corrected.RotationCorrection, -1.8, -1.2);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Visible_outer_edges_constrain_the_border_instead_of_assuming_the_hole_pair_is_exact(bool positive)
    {
        var image = FilmAutoCropTests.Perforated(positive);
        float baseValue = positive ? .025f : .65f;
        for (int x = 0; x < 640; x++) for (int y = 62; y < 65; y++)
            for (int c = 0; c < 3; c++) image.Data[(y * 640 + x) * 3 + c] = baseValue * (c == 0 ? 1 : c == 1 ? .6f : .25f);
        for (int x = 0; x < 640; x++) for (int y = 412; y < 415; y++)
            for (int c = 0; c < 3; c++) image.Data[(y * 640 + x) * 3 + c] = .9f;
        var d = FilmAutoCrop.Detect(image);
        Assert.NotNull(d);
        Assert.Equal(237d / 480, d.AcrossCentre, 5);
        var crop = FilmAutoCrop.Plan(new[] { d }, FilmCropMode.WithSprockets)[0]!.Value;
        Assert.InRange(Math.Abs((crop.Y + crop.H / 2) * 480 - 237), 0, .51);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Border_crop_centres_on_the_detected_gate_even_when_capture_is_off_centre(int turns, bool flip)
    {
        var source = Shift(FilmAutoCropTests.Perforated(false), 35, -20);
        source = Geometry.ApplyOrientation(source, turns, flip, false);
        var d = FilmAutoCrop.Detect(source);
        Assert.NotNull(d);
        var r = FilmAutoCrop.Plan(new[] { d }, FilmCropMode.WithSprockets)[0];
        Assert.NotNull(r);
        double x = 365, y = 220, w = 640, h = 480;
        for (int i = 0; i < turns; i++) (x, y, w, h) = (h - y, x, h, w);
        if (flip) x = w - x;
        Assert.InRange(Math.Abs((r.Value.X + r.Value.W / 2) * w - x), 0, 1.1);
        Assert.InRange(Math.Abs((r.Value.Y + r.Value.H / 2) * h - y), 0, 1.1);
    }

    [Fact]
    public void References_require_three_agreeing_physical_rulers_and_never_contain_position()
    {
        var d = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(false))!;
        Assert.Empty(FilmAutoCrop.BuildRollReferences(new[] { d, d }));
        Assert.Single(FilmAutoCrop.BuildRollReferences(new[] { d, d, d }));
        Assert.Empty(FilmAutoCrop.BuildRollReferences(new[] { d, d with { FilmWidth = d.FilmWidth * .9 }, d with { FilmWidth = d.FilmWidth * 1.1 } }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Roll_reference_recovers_one_edge_using_this_frames_position(bool positive)
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(positive))!;
        var references = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        var weak = OneEdge(positive);
        Assert.True(FilmAutoCrop.NeedsRollReference(FilmAutoCrop.Detect(weak), references));
        var d = FilmAutoCrop.DetectWithRollReference(weak, references, 640, 480);
        Assert.NotNull(d);
        Assert.True(d.UsedRollReference);
        Assert.InRange((d.Image.X + d.Image.W / 2) * 640, 328, 332);
        var moved = Shift(weak, 35, -20);
        d = FilmAutoCrop.DetectWithRollReference(moved, references, 640, 480);
        Assert.NotNull(d);
        Assert.InRange((d.Image.X + d.Image.W / 2) * 640, 363, 367);
        Assert.InRange(d.AcrossCentre * 480, 218, 222);
    }

    [Fact]
    public void Blank_frames_wrong_geometry_and_conflicting_polarities_still_have_no_result()
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(false))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        Assert.Null(FilmAutoCrop.DetectWithRollReference(FilmAutoCropTests.Unreadable(), refs, 640, 480));
        Assert.Null(FilmAutoCrop.DetectWithRollReference(OneEdge(false), refs, 1280, 960));
        Assert.Null(FilmAutoCrop.DetectWithRollReference(OneEdge(false), refs.Concat(refs.Select(r => r with { Polarity = FilmPolarity.Positive })).ToArray(), 640, 480));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strong_internal_rectangle_or_separate_half_frames_do_not_become_a_missing_edge_recovery(bool positive)
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(positive))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        var image = FilmAutoCropTests.Perforated(positive);
        float baseValue = positive ? .025f : .65f;
        for (int y = 120; y < 360; y++) for (int x = 460; x < 510; x++)
            for (int c = 0; c < 3; c++) image.Data[(y * 640 + x) * 3 + c] = baseValue * (c == 0 ? 1 : c == 1 ? .6f : .25f);
        Assert.Null(FilmAutoCrop.DetectWithRollReference(image, refs, 640, 480));
        image = FilmAutoCropTests.Perforated(positive);
        for (int y = 120; y < 360; y++) for (int x = 322; x < 338; x++)
            for (int c = 0; c < 3; c++) image.Data[(y * 640 + x) * 3 + c] = baseValue * (c == 0 ? 1 : c == 1 ? .6f : .25f);
        Assert.Null(FilmAutoCrop.DetectWithRollReference(image, refs, 640, 480));
    }

    // Preserve the left exposure edge but let the final quarter fade smoothly into the base.
    // There is no measurable right edge; only a corroborated gate size can locate it.
    internal static ImageBuffer OneEdge(bool positive)
    {
        var image = FilmAutoCropTests.Perforated(positive);
        float baseValue = positive ? .025f : .65f;
        for (int y = 120; y < 360; y++) for (int x = 150; x < 510; x++)
        {
            double blend = Math.Clamp((x - 390) / 115d, 0, 1);
            for (int c = 0; c < 3; c++)
            {
                double factor = c == 0 ? 1 : c == 1 ? .6 : .25;
                int p = (y * 640 + x) * 3 + c;
                image.Data[p] = (float)(image.Data[p] * (1 - blend) + baseValue * factor * blend);
            }
        }
        return image;
    }

    private static ImageBuffer Shift(ImageBuffer source, int dx, int dy)
    {
        var moved = new ImageBuffer(source.Width, source.Height);
        Array.Fill(moved.Data, .9f);
        for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
        {
            int sx = x - dx, sy = y - dy;
            if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height) continue;
            Array.Copy(source.Data, (sy * source.Width + sx) * 3, moved.Data, (y * source.Width + x) * 3, 3);
        }
        return moved;
    }
}
