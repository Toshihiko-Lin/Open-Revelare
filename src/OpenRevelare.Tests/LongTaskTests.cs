using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public class LongTaskTests
{
    [Fact]
    public void Frame_status_and_busy_updates_do_not_hide_or_cancel_export()
    {
        using var vm = new MainViewModel();
        Assert.True(vm.TryBeginLongTask("Export 1/3", out var job));
        try
        {
            vm.IsBusy = true;
            vm.StatusText = "Decoding another frame";
            vm.IsBusy = false;
            vm.StatusText = "Another frame loaded";

            Assert.True(vm.ShowProgress);
            Assert.Equal("Export 1/3", vm.DisplayStatus);
            Assert.False(job.IsCancellationRequested);

            vm.ReportLongTask("Export 2/3");
            Assert.Equal("Export 2/3", vm.DisplayStatus);
        }
        finally { vm.EndLongTask(job); }
        Assert.False(vm.ShowProgress);
        Assert.Equal("Another frame loaded", vm.DisplayStatus);
    }

    [Fact]
    public void Cancel_waits_for_worker_exit_and_keeps_cancelling_status()
    {
        using var vm = new MainViewModel();
        Assert.True(vm.TryBeginLongTask("Export", out var job));
        vm.CancelLongTask();
        string cancelling = vm.DisplayStatus;
        vm.ReportLongTask("Export next frame");

        Assert.True(job.IsCancellationRequested);
        Assert.True(vm.HasLongTask);
        Assert.True(vm.ShowProgress);
        Assert.False(vm.CanCancelLongTask);
        Assert.Equal(cancelling, vm.DisplayStatus);
        Assert.False(vm.TryBeginLongTask("Another export", out _));
        vm.EndLongTask(job);

        Assert.True(vm.TryBeginLongTask("Next export", out var next));
        Assert.False(next.IsCancellationRequested);
        vm.EndLongTask(next);
    }

    [Fact]
    public void Delayed_cleanup_cannot_clear_a_new_job()
    {
        using var vm = new MainViewModel();
        vm.TryBeginLongTask("First", out var first);
        vm.EndLongTask(first);
        vm.TryBeginLongTask("Second", out var second);
        vm.EndLongTask(first);

        Assert.True(vm.HasLongTask);
        Assert.Equal("Second", vm.DisplayStatus);
        Assert.False(second.IsCancellationRequested);
        vm.EndLongTask(second);
    }

    [Fact]
    public void Status_and_progress_bindings_are_notified_when_switch_finishes_mid_job()
    {
        using var vm = new MainViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.TryBeginLongTask("Export", out var job);
        Assert.Contains(nameof(vm.DisplayStatus), changed);
        Assert.Contains(nameof(vm.ShowProgress), changed);
        changed.Clear();
        vm.IsBusy = true;
        vm.IsBusy = false;
        Assert.Contains(nameof(vm.ShowProgress), changed);
        Assert.True(vm.ShowProgress);
        vm.EndLongTask(job);
        changed.Clear();
        vm.StatusText = "Ready";
        Assert.Contains(nameof(vm.DisplayStatus), changed);
    }
}
