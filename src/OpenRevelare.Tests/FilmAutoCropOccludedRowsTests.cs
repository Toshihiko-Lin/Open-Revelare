using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class FilmAutoCropOccludedRowsTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Roll_size_and_inward_holes_can_confirm_a_gate_with_no_outward_hole_pair(bool positive, bool left)
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(positive))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        var hidden = HiddenOutwardHoles(positive, left);
        if (positive && left) Assert.Null(FilmAutoCrop.Detect(hidden));
        var recovered = FilmAutoCrop.DetectWithRollReference(hidden, refs, 640, 480);
        Assert.NotNull(recovered);
        Assert.True(recovered.UsedRollReference);
        Assert.Equal(positive ? FilmPolarity.Positive : FilmPolarity.Negative, recovered.Polarity);
        Assert.InRange((recovered.Image.X + recovered.Image.W / 2) * 640, 328, 332);
        Assert.NotNull(FilmAutoCrop.Plan(new[] { recovered }, FilmCropMode.WithSprockets)[0]);
    }

    [Fact]
    public void Inward_holes_do_not_let_an_opaque_holder_edge_impersonate_the_missing_gate_edge()
    {
        var clear = FilmAutoCrop.Detect(FilmAutoCropTests.Perforated(true))!;
        var refs = FilmAutoCrop.BuildRollReferences(new[] { clear, clear, clear });
        var source = FilmAutoCropTests.Perforated(true);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
        {
            int at = (y * 640 + x) * 3;
            if (x >= 582) { source.Data[at] = source.Data[at + 1] = source.Data[at + 2] = .01f; continue; }
            if (y < 65 || y >= 415 || source.Data[at] > .85f) continue;
            bool window = x >= 222 && x < 275 && y >= 120 && y < 360;
            bool neighbour = x >= 445 && x < 582 && y >= 120 && y < 360;
            float value = neighbour ? .45f : window ? .28f : .018f;
            source.Data[at] = value; source.Data[at + 1] = value * .6f; source.Data[at + 2] = value * .25f;
        }
        Assert.Null(FilmAutoCrop.DetectWithRollReference(source, refs, 640, 480));
    }

    internal static ImageBuffer HiddenOutwardHoles(bool positive, bool left)
    {
        var source = FilmAutoCropTests.Perforated(positive);
        float baseValue = positive ? .025f : .65f;
        for (int y = 65; y < 415; y++) for (int x = 0; x < 640; x++)
        {
            if (!(left ? x < 150 : x >= 510) || source.Data[(y * 640 + x) * 3] < .85f) continue;
            for (int c = 0; c < 3; c++) source.Data[(y * 640 + x) * 3 + c] = baseValue * (c == 0 ? 1 : c == 1 ? .6f : .25f);
        }
        return source;
    }
}
