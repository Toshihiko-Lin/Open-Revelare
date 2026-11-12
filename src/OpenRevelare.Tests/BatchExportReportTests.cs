using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class BatchExportReportTests
{
    [Fact]
    public void Filters_preserve_original_numbers_identity_and_retry_population()
    {
        var items = new[]
        {
            new BatchExportItem("complete.raw", "complete.tif", _ => Task.FromResult("")) { State = BatchExportState.Completed },
            new BatchExportItem("pending.raw", "pending.tif", _ => Task.FromResult("")),
            new BatchExportItem("skipped.raw", null, _ => Task.FromResult("")),
            new BatchExportItem("bad.raw", "bad.tif", _ => Task.FromResult("")) { State = BatchExportState.Failed, Error = "decode failed", Detail = "full diagnostic" },
        };
        var job = new BatchExportJob("output", items);
        var report = new BatchExportReport(job);
        Assert.Equal(new[] { 4, 2, 3, 1 }, report.Rows(BatchResultFilter.All).Select(r => r.Number));
        Assert.Equal(new[] { 4, 2 }, report.Rows(BatchResultFilter.NeedsAttention).Select(r => r.Number));
        foreach (var pair in new[] { (BatchResultFilter.Completed, 1), (BatchResultFilter.Pending, 2), (BatchResultFilter.Skipped, 3), (BatchResultFilter.Failed, 4) })
        {
            var row = Assert.Single(report.Rows(pair.Item1));
            Assert.Equal(pair.Item2, row.Number);
            Assert.Same(items[pair.Item2 - 1], row.Item);
        }
        var failed = report.Rows(BatchResultFilter.Failed)[0];
        Assert.Contains("bad.raw", failed.Details); Assert.Contains("bad.tif", failed.Details);
        Assert.Contains("decode failed", failed.Details); Assert.Contains("full diagnostic", failed.Details);
        Assert.True(job.CanRetry);
        job.RecoveryBlockedReason = "reference changed";
        Assert.Contains("reference changed", report.FullText);
        foreach (var item in items) Assert.Contains(item.SourcePath, report.FullText);
        Assert.Contains("full diagnostic", report.FullText);
        Assert.False(job.CanRetry);
        Assert.Equal(2, report.Rows(BatchResultFilter.NeedsAttention).Count); // filtering cannot bypass recovery guard
        Assert.Equal(BatchExportState.Completed, items[0].State);
        Assert.Equal(BatchExportState.Skipped, items[2].State);
    }

    [Fact]
    public void Empty_filter_does_not_invent_an_item_or_make_completed_work_retryable()
    {
        var item = new BatchExportItem("source.raw", "output.tif", _ => Task.FromResult("")) { State = BatchExportState.Completed };
        var job = new BatchExportJob("output", [item]);
        var report = new BatchExportReport(job);
        Assert.Empty(report.Rows(BatchResultFilter.NeedsAttention));
        Assert.False(job.CanRetry);
        Assert.Single(report.Rows(BatchResultFilter.Completed));
    }
}
