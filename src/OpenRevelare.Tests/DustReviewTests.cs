using System.Reflection;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DustReviewTests
{
    [Fact]
    public void Repeated_review_changes_leave_the_saved_dust_layer_and_undo_version_intact()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var original = new DustMask(64, 48, (0, 0, 1, 1), [new(10, 10, 15)]);
        var proposed = new DustMask(64, 48, (0, 0, 1, 1), [new(20, 20, 25)]);
        var adjusted = new DustMask(64, 48, (0, 0, 1, 1), [new(20, 20, 23)]);
        using var vm = new MainViewModel();
        typeof(MainViewModel).GetMethod("LoadParams", flags)!.Invoke(vm,
            [new FrameParams { AutomaticDustMask = original, DustEnabled = false }]);
        FrameParams Saved() => (FrameParams)typeof(MainViewModel).GetMethod("BuildParams", flags)!.Invoke(vm, null)!;
        var editVersion = typeof(MainViewModel).GetField("_editVersion", flags)!;
        object? beforeVersion = editVersion.GetValue(vm);

        foreach (DustMask? selection in new DustMask?[] { proposed, null, adjusted, null, adjusted })
        {
            vm.SetDustReviewPreview(selection, refresh: false);
            var stored = Saved();
            var preview = vm.WithDustReviewPreview(stored);
            Assert.Same(selection ?? original, preview.AutomaticDustMask);
            Assert.Equal(selection is not null, preview.DustEnabled);
            Assert.Same(original, stored.AutomaticDustMask);
            Assert.False(stored.DustEnabled);
            Assert.Same(original, Saved().AutomaticDustMask);
            Assert.Equal(beforeVersion, editVersion.GetValue(vm));
        }
        vm.SetDustReviewPreview(null, refresh: false);
        Assert.Same(original, vm.WithDustReviewPreview(Saved()).AutomaticDustMask);
    }

    [Fact]
    public void Before_view_suppresses_manual_and_automatic_repairs_without_changing_saved_settings()
    {
        using var vm = new MainViewModel();
        var original = new DustMask(64, 48, (0, 0, 1, 1), [new(10, 10, 15)]);
        var stored = new FrameParams
        {
            DustEnabled = true, AutomaticDustMask = original,
            DustSpots = [new DustSpot(.5, .5, .02)],
        };
        vm.SetDustReviewPreview(null, refresh: false, before: true);
        FrameParams before = vm.WithDustReviewPreview(stored);
        Assert.False(before.DustEnabled);
        Assert.True(stored.DustEnabled);
        Assert.Same(original, stored.AutomaticDustMask);
        Assert.Single(stored.DustSpots);
        vm.SetDustReviewPreview(null, refresh: false);
        Assert.Same(stored, vm.WithDustReviewPreview(stored));
    }

    [Fact]
    public void Candidate_zero_remains_visible_after_rotation_and_crop_excludes_the_other_candidate()
    {
        var key = new DustReviewProjection.GeometryKey(80, 60, null, null, null, 0,
            1, false, false, 0, (0, 0, 1, .5));
        var runs = Enumerable.Range(13, 5)
            .Select(y => new PureNegComponent.CandidateRun(0, new(y, 13, 18)))
            .Concat(Enumerable.Range(38, 5).Select(y => new PureNegComponent.CandidateRun(17, new(y, 58, 63))))
            .ToArray();
        var projection = new DustReviewProjection(key, 80, 60, (0, 0, 1, 1), runs);
        Assert.Equal(60, projection.Width);
        Assert.Equal(40, projection.Height);
        Assert.Equal(0, Assert.Single(projection.VisibleIds));
        var selected = new DustMask(80, 60, (0, 0, 1, 1), runs.Select(run => run.Run).OrderBy(run => run.Y).ThenBy(run => run.Start));
        var empty = new DustMask(80, 60, (0, 0, 1, 1), []);
        Assert.NotEqual(projection.MakeOverlay(selected), projection.MakeOverlay(empty));
    }
}
