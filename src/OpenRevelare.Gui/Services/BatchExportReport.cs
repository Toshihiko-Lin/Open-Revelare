namespace OpenRevelare.Gui.Services;

internal enum BatchResultFilter { All, NeedsAttention, Failed, Pending, Completed, Skipped }

/// <summary>A read-only projection of the existing job. Filtering never edits states, output
/// reservations or the persisted recovery snapshot.</summary>
internal sealed class BatchExportReport(BatchExportJob job)
{
    internal sealed record Row(int Number, BatchExportItem Item)
    {
        internal string State => Item.State switch
        {
            BatchExportState.Completed => Loc.T("完成"), BatchExportState.Failed => Loc.T("失败"),
            BatchExportState.Skipped => Loc.T("跳过"), _ => Loc.T("未执行"),
        };
        internal string Caption => $"{Number:00} · {State} · {Path.GetFileName(Item.SourcePath)}";
        internal string Details => string.Join("\n", new[]
        {
            Caption, Loc.T("原片：") + Item.SourcePath,
            Loc.T("输出：") + (Item.OutputPath ?? Loc.T("跳过")), Item.Error, Item.Detail,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
    internal string Summary => Loc.F($"完成 {job.Items.Count(i => i.State == BatchExportState.Completed)} · 失败 {job.Items.Count(i => i.State == BatchExportState.Failed)} · 未执行 {job.Items.Count(i => i.State == BatchExportState.Pending)} · 跳过 {job.Items.Count(i => i.State == BatchExportState.Skipped)}");
    internal string FullText => string.Join("\n\n", new[]
    {
        Loc.F($"卷：{job.RollTitle} · 开始于 {job.StartedAt:yyyy-MM-dd HH:mm:ss}"),
        job.OptionsSummary, job.Folder, Summary, job.SystemError, job.RecoveryBlockedReason,
        job.WasCancelled ? Loc.T("导出已取消，已完成的文件已保留") : "",
        Loc.T("重试会读取当前原片，沿用任务开始时的调整参数与输出路径。"),
        string.Join("\n\n", Rows(BatchResultFilter.All).Select(r => r.Details)),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    internal IReadOnlyList<Row> Rows(BatchResultFilter filter) => job.Items.Select((item, index) => new Row(index + 1, item))
        .Where(row => filter switch
        {
            BatchResultFilter.NeedsAttention => row.Item.State is BatchExportState.Failed or BatchExportState.Pending,
            BatchResultFilter.Failed => row.Item.State == BatchExportState.Failed,
            BatchResultFilter.Pending => row.Item.State == BatchExportState.Pending,
            BatchResultFilter.Completed => row.Item.State == BatchExportState.Completed,
            BatchResultFilter.Skipped => row.Item.State == BatchExportState.Skipped,
            _ => true,
        })
        .OrderBy(row => row.Item.State switch { BatchExportState.Failed => 0, BatchExportState.Pending => 1, BatchExportState.Skipped => 2, _ => 3 })
        .ThenBy(row => row.Number).ToArray();
}
