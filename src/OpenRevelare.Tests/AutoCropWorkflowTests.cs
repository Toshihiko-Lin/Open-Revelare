using System.Security.Cryptography;
using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class AutoCropWorkflowTests
{
    // The detector alone cannot protect the live controls, undo snapshots or project writer.
    // Both modes must reach the exported/stored frame rather than just change a preview overlay.
    [Theory]
    [InlineData(false, FilmCropMode.ImageArea)]
    [InlineData(false, FilmCropMode.WithSprockets)]
    [InlineData(true, FilmCropMode.ImageArea)]
    [InlineData(true, FilmCropMode.WithSprockets)]
    public async Task Roll_crops_are_undoable_persisted_and_leave_sources_unchanged(bool positive, FilmCropMode mode)
    {
        string directory = Path.Combine(TestDataIsolation.Root, "auto-crop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "frame.tif"), second = Path.Combine(directory, "frame2.tif");
        TiffIO.ExportTiff16(FilmAutoCropTests.Perforated(positive), source);
        File.Copy(source, second);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        string project = Path.Combine(directory, "roll.ncproj");
        Project.Save(project, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true },
            Frames = new() { new() { SourcePath = source }, new() { SourcePath = second } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(project);
        for (int i = 0; i < 400 && (vm.IsBusy || vm.RollAnalysisPending); i++) await Task.Delay(25);
        Assert.True(vm.HasImage);
        Assert.False(vm.IsBusy);
        await vm.AutoCrop135Async(mode, wholeRoll: true);
        Assert.False(vm.IsAutoCropping);
        Assert.All(vm.Frames, f => Assert.NotNull(f.Params.CropRect));
        var crops = vm.Frames.Select(f => f.Params.CropRect).ToArray();
        var crop = crops[0]!.Value;
        Assert.Equal(mode == FilmCropMode.ImageArea ? 1.5 : 37.0 / 35, crop.W * 640 / (crop.H * 480), 10);
        Assert.Equal(crops[0], crops[1]);
        vm.Undo();
        Assert.All(vm.Frames, f => Assert.Null(f.Params.CropRect));
        vm.Redo();
        Assert.Equal(crops, vm.Frames.Select(f => f.Params.CropRect).ToArray());
        Assert.True(await vm.SaveProjectAsync(project));
        Assert.Equal(crops, Project.Load(project).Frames.Select(f => f.Params.CropRect).ToArray());
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(second)));

        // A checked target must not also crop the unselected current frame.
        vm.Undo();
        Assert.All(vm.Frames, f => Assert.Null(f.Params.CropRect));
        vm.Frames[1].IsSelected = true;
        await vm.AutoCrop135Async(mode, wholeRoll: true);
        Assert.Null(vm.Frames[0].Params.CropRect);
        Assert.NotNull(vm.Frames[1].Params.CropRect);
        vm.Undo();
        Assert.All(vm.Frames, f => Assert.Null(f.Params.CropRect));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText) && vm.IsAutoCropping) vm.CancelAutoCrop();
        };
        await vm.AutoCrop135Async(mode, wholeRoll: true);
        Assert.All(vm.Frames, f => Assert.Null(f.Params.CropRect));
        Assert.False(vm.IsAutoCropping);
    }
}
