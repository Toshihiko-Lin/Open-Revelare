using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensfunRollTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Import_choice_applies_to_all_frames_and_survives_toggle_save_and_reopen(bool enabled)
    {
        string folder = Path.Combine(TestDataIsolation.Root, "lensfun-roll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string first = WriteTiff(folder, "a.tif"), second = WriteTiff(folder, "b.tif");
        var calibration = LensfunCorrectionTests.Calibration();
        var config = new ImportConfig { AutoInvert = false, Lensfun = calibration, LensfunEnabled = enabled };
        config.Paths.AddRange(new[] { first, second });
        using var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_matchLensfunAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vm, (Func<string, Task<LensfunCalibration?>>)(p => Task.FromResult<LensfunCalibration?>(p == first || p == second ? calibration : null)));
        await vm.LoadRollWithConfigAsync(config);
        await Settle(vm);
        Assert.True(vm.LensfunAvailable);
        Assert.True(vm.LensfunEnabled); // automatic per-frame fallback ignores the old import enable switch
        vm.LensfunEnabled = enabled;
        Assert.Contains(calibration.Profile, vm.LensfunProfileText);
        Assert.All(vm.Frames, f =>
        {
            Assert.Equal(calibration, f.Params.Lensfun);
            Assert.Equal(enabled, f.Params.LensfunEnabled);
        });

        vm.LensfunEnabled = !enabled;
        Assert.All(vm.Frames, f => Assert.Equal(!enabled, f.Params.LensfunEnabled));
        string project = Path.Combine(folder, "saved.ncproj");
        Assert.True(await vm.SaveProjectAsync(project));
        await vm.OpenProjectAsync(project);
        await Settle(vm);
        Assert.True(vm.LensfunAvailable);
        Assert.Equal(!enabled, vm.LensfunEnabled);
        Assert.All(vm.Frames, f =>
        {
            Assert.Equal(calibration, f.Params.Lensfun);
            Assert.Equal(!enabled, f.Params.LensfunEnabled);
        });

        // Another roll with no matching profile must never inherit the previous roll's state.
        var uncorrected = new ImportConfig { AutoInvert = false };
        uncorrected.Paths.Add(WriteTiff(folder, "other.tif"));
        await vm.LoadRollWithConfigAsync(uncorrected);
        await Settle(vm);
        Assert.False(vm.LensfunAvailable);
        Assert.False(vm.LensfunEnabled);
        Assert.All(vm.Frames, f => { Assert.Null(f.Params.Lensfun); Assert.False(f.Params.LensfunEnabled); });
    }

    [Fact]
    public async Task MixedRollChoosesEachSourceIndependentlyAndReopenKeepsSavedEdits()
    {
        string folder = Path.Combine(TestDataIsolation.Root, "auto-lens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string first = WriteTiff(folder, "a.tif"), second = WriteTiff(folder, "b.tif"), third = WriteTiff(folder, "c.tif");
        var embedded = new LensCorrectionEstimate("Camera", 100, 8, 0.01, 0.2, 2.5, "Embedded");
        var calibration = LensfunCorrectionTests.Calibration();
        using var vm = new MainViewModel();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        int reads = 0;
        typeof(MainViewModel).GetField("_readLensCorrectionAsync", flags)!.SetValue(vm,
            (Func<string, Task<LensCorrectionEstimate?>>)(p => { reads++; return Task.FromResult<LensCorrectionEstimate?>(p == first ? embedded : null); }));
        typeof(MainViewModel).GetField("_matchLensfunAsync", flags)!.SetValue(vm,
            (Func<string, Task<LensfunCalibration?>>)(p =>
            {
                Assert.NotEqual(first, p); // embedded wins without consulting Lensfun
                return Task.FromResult<LensfunCalibration?>(p == second ? calibration : null);
            }));
        var config = new ImportConfig { AutoInvert = false }; config.Paths.AddRange(new[] { first, second });
        await vm.LoadRollWithConfigAsync(config); await Settle(vm);
        Assert.Equal(0.01, vm.Frames[0].Params.DistortionK1);
        Assert.False(vm.Frames[0].Params.LensfunEnabled);
        Assert.True(vm.Frames[1].Params.LensfunEnabled);
        await vm.AddImagesAsync(new[] { third });
        Assert.False(vm.Frames[2].Params.LensfunEnabled);
        Assert.Equal(0, vm.Frames[2].Params.DistortionK1);
        Assert.Equal(0, vm.Frames[2].Params.VignetteAmount);
        vm.DistortionK1 = 0.03;
        string project = Path.Combine(folder, "saved.ncproj");
        Assert.True(await vm.SaveProjectAsync(project));
        int before = reads;
        await vm.OpenProjectAsync(project); await Settle(vm);
        Assert.Equal(before, reads); // reopening restores saved parameters rather than reselecting
        Assert.Equal(0.03, vm.DistortionK1);
        Assert.True(vm.Frames[1].Params.LensfunEnabled);
        Assert.False(vm.Frames[2].Params.LensfunEnabled);
    }

    private static async Task Settle(MainViewModel vm)
    {
        for (int i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.IsBusy);
        Assert.True(vm.HasImage, vm.StatusText);
    }

    private static string WriteTiff(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        using var tiff = Tiff.Open(path, "w")!;
        tiff.SetField(TiffTag.IMAGEWIDTH, 16);
        tiff.SetField(TiffTag.IMAGELENGTH, 12);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
        for (int y = 0; y < 12; y++) tiff.WriteScanline(Enumerable.Repeat((byte)128, 48).ToArray(), y);
        return path;
    }
}
