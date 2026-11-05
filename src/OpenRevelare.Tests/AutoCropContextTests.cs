using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class AutoCropContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_weak_current_or_selected_frame_can_borrow_anchors_without_cropping_them(bool selected)
    {
        using var vm = await Open(FilmAutoCropOccludedRowsTests.HiddenOutwardHoles(true, true),
            FilmAutoCropTests.Perforated(true), FilmAutoCropTests.Perforated(true), FilmAutoCropTests.Perforated(true));
        vm.Frames[0].IsSelected = selected;
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, wholeRoll: selected);
        Assert.NotNull(vm.Frames[0].Params.CropRect);
        Assert.Contains("整卷互校", vm.Frames[0].Params.AutoCropReviewReason);
        Assert.All(vm.Frames.Skip(1), f => { Assert.Null(f.Params.CropRect); Assert.Null(f.Params.AutoCropRecovery); });
        Assert.Equal(selected, vm.Frames[0].IsSelected);
        Assert.False(vm.HasAutoCropFailure);
        var crop = vm.Frames[0].Params.CropRect;
        vm.Undo(); Assert.All(vm.Frames, f => Assert.Null(f.Params.CropRect));
        vm.Redo(); Assert.Equal(crop, vm.Frames[0].Params.CropRect);
    }

    [Fact]
    public async Task Output_and_photometric_changes_reuse_measurements_while_geometry_changes_invalidate_them()
    {
        using var vm = await Open(Geometry.ApplyRotation(FilmAutoCropTests.Perforated(false), 1.5, fill: 0));
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.NotNull(vm.CurrentCrop);
        Assert.InRange(vm.Rotation, -1.8, -1.2);
        int measurements = vm.AutoCropMeasurementCount;
        await vm.AutoCrop135Async(FilmCropMode.WithSprockets, false);
        Assert.NotNull(vm.CurrentCrop);
        Assert.Equal(measurements, vm.AutoCropMeasurementCount);
        vm.ExposureEv = 1; vm.VignetteAmount = .2;
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.Equal(measurements, vm.AutoCropMeasurementCount);
        vm.RotateCw();
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.Equal(measurements + 1, vm.AutoCropMeasurementCount);
        var crop = vm.CurrentCrop!.Value;
        Assert.Equal(2d / 3, crop.W * 480 / (crop.H * 640), 8);
        vm.DistortionK1 = .005;
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.Equal(measurements + 2, vm.AutoCropMeasurementCount);
    }

    [Fact]
    public async Task Replaced_anchor_files_do_not_reuse_the_old_roll_reference()
    {
        using var vm = await Open(FilmAutoCropOccludedRowsTests.HiddenOutwardHoles(true, true),
            FilmAutoCropTests.Perforated(true), FilmAutoCropTests.Perforated(true), FilmAutoCropTests.Perforated(true));
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.NotNull(vm.CurrentCrop);
        var manual = (X: .2, Y: .2, W: .4, H: .4);
        vm.SetCrop(manual);
        foreach (var frame in vm.Frames.Skip(1))
        {
            TiffIO.ExportTiff16(FilmAutoCropTests.Unreadable(), frame.Path);
            File.SetLastWriteTimeUtc(frame.Path, DateTime.UtcNow.AddMinutes(1));
        }
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.Equal(manual, vm.CurrentCrop);
        Assert.True(vm.HasAutoCropFailure);
        Assert.NotEmpty(vm.AutoCropFailureText);
        Assert.All(vm.Frames.Skip(1), f => Assert.Null(f.Params.CropRect));
    }

    [Fact]
    public async Task A_clear_frame_at_a_new_magnification_is_not_rejected_by_cached_roll_geometry()
    {
        using var vm = await Open(FilmAutoCropTests.Perforated(false), FilmAutoCropTests.Perforated(false),
            FilmAutoCropTests.Perforated(false), FilmAutoCropTests.Perforated(false));
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, true);
        double oldWidth = vm.CurrentCrop!.Value.W;
        var original = FilmAutoCropTests.Perforated(false);
        var larger = new ImageBuffer(640, 480);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
        {
            int sx = Math.Clamp((int)Math.Round(320 + (x - 320) / 1.12), 0, 639);
            int sy = Math.Clamp((int)Math.Round(240 + (y - 240) / 1.12), 0, 479);
            Array.Copy(original.Data, (sy * 640 + sx) * 3, larger.Data, (y * 640 + x) * 3, 3);
        }
        TiffIO.ExportTiff16(larger, vm.Frames[0].Path);
        File.SetLastWriteTimeUtc(vm.Frames[0].Path, DateTime.UtcNow.AddMinutes(1));
        await vm.AutoCrop135Async(FilmCropMode.ImageArea, false);
        Assert.False(vm.HasAutoCropFailure);
        Assert.True(vm.CurrentCrop!.Value.W > oldWidth * 1.05);
    }

    private static async Task<MainViewModel> Open(params ImageBuffer[] images)
    {
        string directory = Path.Combine(TestDataIsolation.Root, "crop-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var frames = new List<Project.Frame>();
        for (int i = 0; i < images.Length; i++)
        {
            string path = Path.Combine(directory, $"frame{i}.tif");
            TiffIO.ExportTiff16(images[i], path); frames.Add(new() { SourcePath = path });
        }
        string project = Path.Combine(directory, "roll.ncproj");
        Project.Save(project, new Project.Data { Meta = new() { InputType = "tiff", TiffIsLinear = true }, Frames = frames });
        var vm = new MainViewModel();
        await vm.OpenProjectAsync(project);
        for (int i = 0; i < 400 && (vm.IsBusy || vm.RollAnalysisPending); i++) await Task.Delay(25);
        Assert.True(vm.HasImage);
        return vm;
    }
}
