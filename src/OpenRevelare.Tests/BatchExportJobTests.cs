using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class BatchExportJobTests
{
    [Fact]
    public void Results_retain_their_original_roll_context_and_detach_the_item_list()
    {
        var items = new List<BatchExportItem> { new("a.raw", "a.tif", _ => Task.FromResult("")) };
        var job = new BatchExportJob("original-output", items, "original-roll", "TIFF 16-bit");
        items.Clear();
        Assert.Single(job.Items); Assert.Equal("original-roll", job.RollTitle);
        Assert.Equal("original-output", job.Folder); Assert.Equal("TIFF 16-bit", job.OptionsSummary);
    }

    [Fact]
    public async Task Bad_frame_does_not_stop_later_frames_and_retry_preserves_successes()
    {
        int[] calls = new int[3];
        var items = Enumerable.Range(0, 3).Select(i => new BatchExportItem($"{i}.raw", $"{i}.tif", _ =>
        {
            calls[i]++;
            if (i == 1 && calls[i] == 1) throw new InvalidDataException("corrupt frame");
            return Task.FromResult("render snapshot " + i);
        })).ToArray();
        var job = new BatchExportJob("output", items);
        await job.RunAsync(default, (_, _) => { });
        Assert.Equal(new[] { 1, 1, 1 }, calls);
        Assert.Equal(BatchExportState.Failed, items[1].State);
        Assert.Contains("corrupt", items[1].Error);
        Assert.True(job.CanRetry);
        await job.RunAsync(default, (_, _) => { });
        Assert.Equal(new[] { 1, 2, 1 }, calls);
        Assert.All(items, item => Assert.Equal(BatchExportState.Completed, item.State));
        Assert.False(job.CanRetry); Assert.Equal("1.tif", items[1].OutputPath);
    }

    [Fact]
    public async Task Cancellation_retains_completed_entries_and_resumes_only_pending_ones()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var first = new BatchExportItem("a.raw", "a.tif", _ => { calls++; cts.Cancel(); return Task.FromResult(""); });
        var second = new BatchExportItem("b.raw", "b.tif", _ => { calls++; return Task.FromResult(""); });
        var job = new BatchExportJob("output", new[] { first, second });
        await job.RunAsync(cts.Token, (_, _) => { });
        Assert.True(job.WasCancelled); Assert.Equal(1, calls);
        Assert.Equal(BatchExportState.Completed, first.State); Assert.Equal(BatchExportState.Pending, second.State);
        await job.RunAsync(default, (_, _) => { });
        Assert.Equal(2, calls); Assert.False(job.CanRetry);
    }

    [Fact]
    public async Task Permission_failure_pauses_and_preserves_unstarted_entries()
    {
        var first = new BatchExportItem("a.raw", "a.tif", _ => throw new UnauthorizedAccessException("read-only output"));
        var second = new BatchExportItem("b.raw", "b.tif", _ => throw new Exception("must not run"));
        var job = new BatchExportJob("output", new[] { first, second });
        await job.RunAsync(default, (_, _) => { });
        Assert.Equal(BatchExportState.Failed, first.State); Assert.Equal(BatchExportState.Pending, second.State);
        Assert.Contains("read-only", job.SystemError);
    }

    [Fact]
    public async Task Conflict_skips_are_never_retried_or_reported_as_completed()
    {
        var skipped = new BatchExportItem("a.raw", null, _ => throw new Exception("must not run"));
        var job = new BatchExportJob("output", new[] { skipped });
        await job.RunAsync(default, (_, _) => { });
        Assert.Equal(BatchExportState.Skipped, skipped.State); Assert.False(job.CanRetry);
    }
}
