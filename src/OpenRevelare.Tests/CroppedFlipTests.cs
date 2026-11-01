using System.Reflection;
using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CroppedFlipTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int turns in new[] { 0, 1, 2, 3 })
        foreach (bool mirrored in new[] { false, true })
        foreach (bool horizontal in new[] { false, true })
        foreach (double angle in new[] { 0.0, 7.5 })
            yield return new object[] { turns, mirrored, horizontal, angle };
    }

    // Compare the actual stored pipeline geometry with a mirror of the finished crop. Checking
    // only flags/rectangles misses the noncommutativity of reflection and straighten rotation.
    [Theory]
    [MemberData(nameof(Cases))]
    public void Flip_mirrors_the_finished_crop_pixel_for_pixel(int turns, bool mirrored, bool horizontal, double angle)
    {
        var parameters = new FrameParams
        {
            QuarterTurns = turns, FlipH = mirrored, Rotation = angle,
            CropRect = (.1, .2, .6, .4),
        };
        using var vm = new MainViewModel();
        Load(vm, parameters);
        var source = Pixels();
        var expected = Geometry.ApplyOrientation(Render(source, parameters), 0, horizontal, !horizontal);
        if (horizontal) vm.FlipHorizontal(); else vm.FlipVertical();
        FrameParams actualParameters = Stored(vm);
        Assert.Equal(-angle, actualParameters.Rotation);
        var actual = Render(source, actualParameters);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.All(expected.Data.Zip(actual.Data), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 2e-6));
        if (horizontal) vm.FlipHorizontal(); else vm.FlipVertical();
        Assert.Equal(parameters.Rotation, vm.Rotation);
        var restored = vm.CurrentCrop!.Value;
        var original = parameters.CropRect!.Value;
        Assert.Equal(original.X, restored.X, 12);
        Assert.Equal(original.Y, restored.Y, 12);
        Assert.Equal(original.W, restored.W, 12);
        Assert.Equal(original.H, restored.H, 12);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uncropped_rotated_frame_is_mirrored_in_screen_axes(bool horizontal)
    {
        var parameters = new FrameParams { QuarterTurns = 3, FlipV = true, Rotation = -9.25 };
        using var vm = new MainViewModel();
        Load(vm, parameters);
        var source = Pixels();
        var expected = Geometry.ApplyOrientation(Render(source, parameters), 0, horizontal, !horizontal);
        if (horizontal) vm.FlipHorizontal(); else vm.FlipVertical();
        var actual = Render(source, Stored(vm));
        Assert.Null(vm.CurrentCrop);
        Assert.All(expected.Data.Zip(actual.Data), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 2e-6));
    }

    // Dragging the draft does not yet change the saved crop. A mirror must preserve that draft,
    // while keeping it separate so Escape can still leave only the mirrored committed crop.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Unconfirmed_draft_survives_without_being_committed(bool horizontal, bool split)
    {
        var parameters = new FrameParams { QuarterTurns = 1, Rotation = -6, CropRect = (.2, .3, .3, .4) };
        using var vm = new MainViewModel();
        Load(vm, parameters);
        if (split) typeof(MainViewModel).GetField("_previewMargin", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, ((double X, double Y, double W, double H)?)(.1, .1, .6, .75));
        vm.CropEditing = true;
        var draft = (X: .15, Y: .25, W: .3, H: .35);
        var result = horizontal ? vm.FlipHorizontal(draft) : vm.FlipVertical(draft);
        Assert.NotNull(result);
        var expected = horizontal ? MainViewModel.FlipCropH(draft) : MainViewModel.FlipCropV(draft);
        AssertRect(expected, result.Value);
        vm.CropEditing = false;
        var committed = horizontal ? MainViewModel.FlipCropH(parameters.CropRect!.Value) : MainViewModel.FlipCropV(parameters.CropRect!.Value);
        AssertRect(committed, Stored(vm).CropRect!.Value);
        var restored = horizontal ? vm.FlipHorizontal(result) : vm.FlipVertical(result);
        AssertRect(draft, restored!.Value);
    }

    private static void AssertRect((double X, double Y, double W, double H) expected,
        (double X, double Y, double W, double H) actual)
    {
        Assert.Equal(expected.X, actual.X, 12); Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.W, actual.W, 12); Assert.Equal(expected.H, actual.H, 12);
    }

    private static void Load(MainViewModel vm, FrameParams p)
        => typeof(MainViewModel).GetMethod("LoadParams", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object[] { p });
    private static FrameParams Stored(MainViewModel vm)
        => (FrameParams)typeof(MainViewModel).GetMethod("BuildParams", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
    private static ImageBuffer Render(ImageBuffer source, FrameParams p)
    {
        var pixels = Geometry.ApplyRotation(Geometry.ApplyOrientation(source, p.QuarterTurns, p.FlipH, p.FlipV), p.Rotation);
        return p.CropRect is { } r ? Geometry.ApplyCrop(pixels, r) : pixels;
    }
    private static ImageBuffer Pixels()
    {
        var image = new ImageBuffer(80, 60);
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            int i = (y * image.Width + x) * 3;
            image.Data[i] = x / 80f; image.Data[i + 1] = y / 60f;
            image.Data[i + 2] = (float)(.5 + .3 * Math.Sin(x * .2 + y * .1));
        }
        return image;
    }
}
