using Avalonia;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustMaskEditorTests
{
    [Fact]
    public void Restore_cuts_a_hole_in_an_automatic_region_and_add_can_extend_it()
    {
        var initial = new DustMask(80, 60, (0, 0, 1, 1),
            Enumerable.Range(10, 15).Select(y => new DustMask.Run(y, 10, 25)));
        var editor = new DustMaskEditor(initial);
        Assert.True(editor.Paint([new(17, 17, 3, 0, 0, 3)], restore: true));
        var restored = editor.Mask;
        Assert.False(Pixels(restored)[17 * 80 + 17]);
        Assert.True(Pixels(restored)[10 * 80 + 10]);
        Assert.InRange(restored.PixelCount, 1, initial.PixelCount - 1);
        Assert.True(editor.Paint([new(40, 30, 4, 0, 0, 3)], restore: false));
        var supplemented = editor.Mask;
        Assert.True(Pixels(supplemented)[30 * 80 + 40]);
        Assert.False(Pixels(supplemented)[17 * 80 + 17]);
        Assert.True(editor.Undo());
        Assert.Same(restored, editor.Mask);
        Assert.True(editor.Undo());
        Assert.Same(initial, editor.Mask);
        Assert.False(editor.IsModified);
        Assert.True(editor.Redo());
        Assert.Same(restored, editor.Mask);
        Assert.True(editor.Paint([new(55, 30, 2, 0, 0, 2)], restore: false));
        Assert.False(editor.CanRedo);
        Assert.Equal(225, initial.PixelCount);
    }

    [Fact]
    public void Brush_maps_back_through_rotation_and_crop_and_its_overlay_matches_the_edited_pixels()
    {
        var key = new DustReviewProjection.GeometryKey(80, 60, null, null, null, 0,
            1, false, false, 0, (0, 0, 1, .5));
        var projection = new DustReviewProjection(key, 80, 60, (0, 0, 1, 1), []);
        var brush = projection.MakeBrush(new Point(44.5 / 60, 15.5 / 40), .05, .1);
        Assert.NotNull(brush);
        Assert.Equal(15, brush.Value.X, 6);
        Assert.Equal(15, brush.Value.Y, 6);
        var editor = new DustMaskEditor(new(80, 60, (0, 0, 1, 1), []));
        Assert.True(editor.Paint([brush.Value], restore: false));
        Assert.True(Pixels(editor.Mask)[15 * 80 + 15]);
        Assert.False(Pixels(editor.Mask)[25 * 80 + 15]);
        byte[] overlay = projection.MakeOverlay(editor.Mask);
        Assert.True(overlay[(15 * projection.Width + 44) * 4 + 3] > 0);
    }

    [Fact]
    public void A_brush_outside_the_detector_snapshot_is_clipped_to_the_snapshot()
    {
        var key = new DustReviewProjection.GeometryKey(100, 80, null, null, null, 0,
            0, false, false, 0, null);
        var projection = new DustReviewProjection(key, 50, 40, (.25, .25, .5, .5), []);
        var editor = new DustMaskEditor(new(50, 40, (.25, .25, .5, .5), []));
        Assert.False(editor.Paint([projection.MakeBrush(new(.01, .01), .01, .01)!.Value], restore: false));
        Assert.True(editor.Paint([projection.MakeBrush(new(.5, .5), .02, .02)!.Value], restore: false));
        Assert.True(editor.Mask.PixelCount > 0);
        Assert.Equal((.25, .25, .5, .5), editor.Mask.Bounds);
    }

    [Fact]
    public void An_oversized_edit_is_atomic_and_preserves_undo()
    {
        var editor = new DustMaskEditor(new(4000, 4000, (0, 0, 1, 1), [new(10, 10, 11)]));
        var original = editor.Mask;
        Assert.Throws<ArgumentException>(() =>
            editor.Paint([new(2000, 2000, 1500, 0, 0, 1500)], restore: false));
        Assert.Same(original, editor.Mask);
        Assert.False(editor.CanUndo);
    }

    private static bool[] Pixels(DustMask mask)
    {
        var pixels = new bool[mask.Width * mask.Height];
        mask.AddTo(pixels, mask.Width, mask.Height);
        return pixels;
    }
}
