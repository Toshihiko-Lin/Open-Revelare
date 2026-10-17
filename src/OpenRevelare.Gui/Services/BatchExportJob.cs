namespace OpenRevelare.Gui.Services;

public enum BatchExportState { Pending, Completed, Skipped, Failed }

public sealed class BatchExportItem
{
    public string SourcePath { get; }
    public string? OutputPath { get; }
    public BatchExportState State { get; internal set; }
    public string Error { get; internal set; } = "";
    public string Detail { get; internal set; } = "";
    internal Func<CancellationToken, Task<string>> Execute { get; }

    internal BatchExportItem(string sourcePath, string? outputPath, Func<CancellationToken, Task<string>> execute)
    {
        SourcePath = sourcePath; OutputPath = outputPath; Execute = execute;
        State = outputPath is null ? BatchExportState.Skipped : BatchExportState.Pending;
    }
}

/// <summary>A retry keeps the original destinations and detached render snapshot. Existence
/// alone cannot prove a file was completed by this job, so only successful entries are skipped.</summary>
public sealed class BatchExportJob
{
    public string Folder { get; }
    public string RollTitle { get; }
    public string OptionsSummary { get; }
    public DateTime StartedAt { get; }
    public IReadOnlyList<BatchExportItem> Items { get; }
    public bool WasCancelled { get; private set; }
    public string SystemError { get; private set; } = "";
    public string RecoveryBlockedReason { get; internal set; } = "";
    internal Func<Task>? StateChangedAsync { get; set; }
    internal Func<string?>? ValidateRecovery { get; set; }
    public bool CanRetry => RecoveryBlockedReason.Length == 0 &&
        Items.Any(i => i.State is BatchExportState.Pending or BatchExportState.Failed);

    internal BatchExportJob(string folder, IReadOnlyList<BatchExportItem> items, string rollTitle = "", string optionsSummary = "",
        DateTime? startedAt = null)
    {
        Folder = folder; Items = items.ToArray(); RollTitle = rollTitle; OptionsSummary = optionsSummary;
        StartedAt = startedAt ?? DateTime.Now;
    }

    internal async Task RunAsync(CancellationToken ct, Action<int, BatchExportItem> progress)
    {
        WasCancelled = false; SystemError = "";
        for (int i = 0; i < Items.Count; i++)
        {
            BatchExportItem item = Items[i];
            if (item.State is BatchExportState.Completed or BatchExportState.Skipped) continue;
            if (ct.IsCancellationRequested) { WasCancelled = true; break; }
            progress(i, item);
            item.Error = "";
            bool stop = false;
            try
            {
                item.Detail = await item.Execute(ct);
                item.State = BatchExportState.Completed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.State = BatchExportState.Pending;
                WasCancelled = true;
                stop = true;
            }
            catch (Exception ex)
            {
                item.State = BatchExportState.Failed;
                item.Error = ex.Message;
                // A bad frame must not strand the rest of a roll. Resource and permission
                // failures instead pause the job so the user can repair its environment.
                if (ex is OutOfMemoryException or UnauthorizedAccessException ||
                    (ex.HResult & 0xffff) is 112 or 39 or 28)
                {
                    SystemError = ex.Message;
                    stop = true;
                }
            }
            if (StateChangedAsync is { } save) await save();
            if (stop) break;
        }
        if (StateChangedAsync is { } lastSave) await lastSave();
    }
}
