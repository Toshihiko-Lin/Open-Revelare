using System.Reflection;
using System.Text.Json.Nodes;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class RecalibrationNoticeTests
{
    private static string NewProject()
    {
        string dir = Path.Combine(TestDataIsolation.Root, "calibration-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "frame.tif");
        var pixels = new ImageBuffer(16, 12);
        Array.Fill(pixels.Data, 0.25f);
        TiffIO.ExportTiff16(pixels, source);
        string project = Path.Combine(dir, "roll.ncproj");
        Project.Save(project, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true },
            Frames = new() { new() { SourcePath = source } },
        });
        return project;
    }

    private static async Task Open(MainViewModel vm, string project)
    {
        await vm.OpenProjectAsync(project);
        for (int i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.IsBusy);
        Assert.True(vm.HasImage);
    }

    [Fact]
    public async Task Scene_crop_and_geometry_edits_do_not_invalidate_calibration()
    {
        using var vm = new MainViewModel();
        await Open(vm, NewProject());
        vm.ExposureEv = 0.5;
        vm.Temp = 12;
        vm.Contrast = 0.1;
        vm.SetCrop((0.1, 0.1, 0.8, 0.8));
        vm.ApplyStraightenAngle(1);
        vm.DistortionK1 = 0.05;
        vm.SprocketThreshold = 0.7; // disabled mask: import-time suggestion only
        Assert.False(vm.NeedsRecalibration);
    }

    [Fact]
    public async Task Loading_stored_optical_parameters_is_not_a_user_edit()
    {
        using var vm = new MainViewModel();
        await Open(vm, NewProject());
        var stored = new FrameParams { VignetteAmount = 0.2, SprocketEnabled = true, SprocketThreshold = 0.7 };
        typeof(MainViewModel).GetMethod("LoadParams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { stored });
        Assert.False(vm.NeedsRecalibration);
    }

    [Fact]
    public async Task Optical_edit_requests_recalibration_without_claiming_a_legacy_model()
    {
        using var vm = new MainViewModel();
        await Open(vm, NewProject());
        vm.VignetteAmount = 0.2;
        Assert.True(vm.NeedsRecalibration);
        Assert.DoesNotContain("旧版模型", vm.RecalibrationNoticeText);
        Assert.Contains("光学校正", vm.RecalibrationNoticeText);
    }

    [Fact]
    public async Task Legacy_notice_is_reserved_for_a_legacy_project_and_does_not_leak_into_import()
    {
        string legacy = NewProject();
        JsonNode data = JsonNode.Parse(File.ReadAllText(legacy))!;
        data["frames"]![0]!.AsObject().Remove("d_min_per_channel");
        File.WriteAllText(legacy, data.ToJsonString());
        using var vm = new MainViewModel();
        await Open(vm, legacy);
        Assert.True(vm.NeedsRecalibration);
        Assert.Contains("旧版模型", vm.RecalibrationNoticeText);

        var config = new ImportConfig { AutoInvert = false, TiffInputAssumption = TiffInputAssumption.Linear };
        config.Paths.Add(Project.Load(NewProject()).Frames[0].SourcePath);
        await vm.LoadRollWithConfigAsync(config);
        for (int i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.NeedsRecalibration);
        Assert.DoesNotContain("旧版模型", vm.RecalibrationNoticeText);
    }
}
