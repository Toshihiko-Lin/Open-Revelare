using System.Reflection;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class SceneResetTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static MainViewModel Model()
    {
        var vm = new MainViewModel();
        var p = new FrameParams
        {
            ExposureEv = 0.7, Contrast = 0.2, Saturation = 0.3, WbGains = WbMath.TempTintToGains(40, -20),
            BlackPoint = 0.03, WhitePoint = 0.9, Highlights = -0.2, Shadows = 0.1,
            CurvePointsM = [(0, 0), (0.5, 0.6), (1, 1)], CurvePreserveHue = false, CurveHasEndpoints = true,
            DMinPerChannel = [0.1, 0.2, 0.3], DMaxPerChannel = [2, 2.1, 2.4],
            CropRect = (0.1, 0.2, 0.7, 0.6), SplitCell = (0, 0, 0.5, 1), Rotation = 3, QuarterTurns = 1,
            DustEnabled = true, DustSpots = [new(0.3, 0.4, 0.02)], VignetteAmount = 0.2,
        };
        var frame = new RollFrame("scene-reset-test.tif") { Params = p };
        vm.Frames.Add(frame);
        vm.Frames.Add(new RollFrame("other-frame.tif") { IsSelected = true });
        vm.GetType().GetField("_currentFrame", Flags)!.SetValue(vm, frame);
        vm.GetType().GetField("_paramsLoaded", Flags)!.SetValue(vm, true);
        vm.GetType().GetMethod("LoadParams", Flags)!.Invoke(vm, [p]);
        vm.HasImage = true;
        vm.GetType().GetMethod("SetUndoBaseline", Flags)!.Invoke(vm, null);
        return vm;
    }

    private static FrameParams Stored(MainViewModel vm)
    {
        vm.GetType().GetMethod("CommitLiveParams", Flags)!.Invoke(vm, [vm.CurrentFrame]);
        return vm.CurrentFrame!.Params.Clone();
    }

    [Fact]
    public void Reset_preserves_calibration_geometry_repairs_and_other_frames_and_is_one_undo_step()
    {
        using var vm = Model();
        var before = Stored(vm);
        var other = PreviewParametersKey.Create(vm.Frames[1].Params);
        int notifications = 0;
        Action<FrameParams> onLoaded = p => { Assert.Empty(p.CurvePointsM); notifications++; };
        vm.FrameParamsLoaded += onLoaded;
        // During crop editing, storage must retain the applied crop rather than the temporary display crop.
        vm.GetType().GetField("_cropEditing", Flags)!.SetValue(vm, true);
        vm.ResetAdjustments();
        var after = Stored(vm);
        Assert.Equal(PreviewParametersKey.CreateFilm(before), PreviewParametersKey.CreateFilm(after));
        Assert.Equal(other, PreviewParametersKey.Create(vm.Frames[1].Params));
        Assert.Equal(0, after.ExposureEv);
        Assert.Equal(0, after.Contrast);
        Assert.Equal(0, after.Saturation);
        Assert.Equal(0, after.BlackPoint);
        Assert.Equal(1, after.WhitePoint);
        Assert.Equal(0, after.Highlights);
        Assert.All(after.WbGains, v => Assert.Equal(1, v));
        Assert.Empty(after.CurvePointsM);
        Assert.True(after.CurvePreserveHue);
        Assert.False(after.CurveHasEndpoints);
        Assert.Equal(1, notifications);
        vm.FrameParamsLoaded -= onLoaded;
        vm.ResetAdjustments(); // A neutral reset must not add a second undo entry.
        vm.Undo();
        Assert.Equal(PreviewParametersKey.Create(before), PreviewParametersKey.Create(Stored(vm)));
        Assert.False(vm.CanUndo);
        vm.Redo();
        Assert.Equal(PreviewParametersKey.Create(after), PreviewParametersKey.Create(Stored(vm)));
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void Reset_is_ignored_while_busy_or_before_current_frame_parameters_are_ready()
    {
        using var vm = Model();
        var before = PreviewParametersKey.Create(Stored(vm));
        vm.IsBusy = true;
        vm.ResetAdjustments();
        Assert.Equal(before, PreviewParametersKey.Create(Stored(vm)));
        vm.IsBusy = false;
        vm.GetType().GetField("_paramsLoaded", Flags)!.SetValue(vm, false);
        vm.ResetAdjustments();
        vm.GetType().GetField("_paramsLoaded", Flags)!.SetValue(vm, true);
        Assert.Equal(before, PreviewParametersKey.Create(Stored(vm)));
        Assert.False(vm.CanUndo);
    }
}
