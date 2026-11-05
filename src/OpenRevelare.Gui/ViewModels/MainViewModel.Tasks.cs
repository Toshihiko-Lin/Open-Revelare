using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRollProgressBanner), nameof(RollProgressTitle), nameof(RollProgressDetail), nameof(RollProgressHint))]
    private int _importStageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRollProgressBanner), nameof(RollProgressTitle), nameof(RollProgressDetail), nameof(RollProgressHint))]
    private bool _loadingImportedPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRollProgressBanner), nameof(RollProgressTitle), nameof(RollProgressDetail), nameof(RollProgressHint))]
    private string _rollAnalysisProgress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollProgressDecodeDetail))]
    private string _thumbnailProgress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollProgressDecodeDetail))]
    private string _warmupProgress = "";

    public string RollProgressDecodeDetail => WarmupProgress.Length > 0 ? WarmupProgress : ThumbnailProgress;

    private void ReportPreviewProgress(CancellationToken ct, string text, bool warmup = false)
    {
        void Publish()
        {
            var owner = warmup ? _warmCts : _thumbCts;
            if (owner is null || owner.Token != ct || ct.IsCancellationRequested) return;
            if (warmup) WarmupProgress = text;
            else ThumbnailProgress = text;
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Publish();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Publish);
    }

    private object? _rollProgressOwner;
    public bool ShowRollProgressBanner => !RollAnalysisNoticeDismissed
        && (ImportStageCount > 0 || LoadingImportedPreview || RollAnalysisProgress.Length > 0);
    public string RollProgressTitle => ImportStageCount > 0 || LoadingImportedPreview
        ? Loc.T("正在导入新卷 …") : Loc.T("正在自动分析整卷 …");
    public string RollProgressDetail => ImportStageCount > 0 || LoadingImportedPreview
        ? (BackgroundStatus.Length > 0 ? BackgroundStatus : Loc.T("正在准备图像和首帧预览，请稍候。"))
        : RollAnalysisProgress;

    public string RollProgressHint => ImportStageCount > 0 || LoadingImportedPreview
        ? Loc.T("后台处理中，请稍候。")
        : Loc.T("分析完成后自动刷新全卷，期间预览可能更新。");

    internal async Task RunImportStageAsync(Func<Task> action)
    {
        if (ImportStageCount == 0 && !LoadingImportedPreview && RollAnalysisProgress.Length == 0)
            RollAnalysisNoticeDismissed = false;
        ImportStageCount++;
        try { await action(); }
        finally { ImportStageCount--; }
    }

    private void ReportRollAnalysisProgress(CancellationTokenSource owner, string text)
    {
        void Publish()
        {
            if (_rollProgressOwner is null || !ReferenceEquals(_autoInvertCts, owner) || owner.IsCancellationRequested) return;
            ReportBackground(text);
            // Keep a visible message during the final endpoint calculations as well.
            RollAnalysisProgress = text.Length > 0 ? text : Loc.T("正在汇总整卷标定结果 …");
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Publish();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Publish);
    }

    // Explicit jobs own their lifetime and status independently of preview/frame switches.
    private CancellationTokenSource? _longTaskCts;
    private TaskCompletionSource? _longTaskCompletion;
    internal Task WaitForLongTaskAsync() => _longTaskCompletion?.Task ?? Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus), nameof(ShowProgress), nameof(CanRestoreProjectReferences), nameof(CanShowBatchExportReport), nameof(CanInspectProjectDependencies), nameof(HasTaskCenterContent), nameof(TaskCenterForegroundText), nameof(TaskCenterSummary))]
    private bool _hasLongTask;

    [ObservableProperty] private bool _canCancelLongTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus), nameof(TaskCenterForegroundText), nameof(TaskCenterSummary))]
    private string _longTaskStatus = "";

    public string DisplayStatus => HasLongTask ? LongTaskStatus : StatusText;
    public bool ShowProgress => HasLongTask || IsBusy;
    public bool HasTaskCenterContent => HasLongTask || IsBusy || BackgroundStatus.Length > 0
        || LastBatchExportJob is not null || SaveNeedsAttention;
    public string TaskCenterForegroundText => HasLongTask ? LongTaskStatus
        : IsBusy ? DisplayStatus
        : Loc.T("没有正在运行的前台任务");
    public string TaskCenterBackgroundText => BackgroundStatus.Length > 0
        ? BackgroundStatus
        : Loc.T("后台队列空闲");
    public string TaskCenterResultText => LastBatchExportJob is null
        ? Loc.T("还没有批量导出记录")
        : Loc.T("已有批量导出结果，可打开查看或重试。");
    public string TaskCenterSummary => HasLongTask ? Loc.T("任务 · 进行中")
        : BackgroundStatus.Length > 0 ? Loc.T("任务 · 后台")
        : LastBatchExportJob is not null ? Loc.T("任务 · 有结果")
        : SaveNeedsAttention ? Loc.T("任务 · 需处理")
        : Loc.T("任务");

    internal bool TryBeginLongTask(string status, out CancellationTokenSource owner)
    {
        CancelPreviewWarmup();
        owner = null!;
        if (HasLongTask) return false;
        owner = new CancellationTokenSource();
        _longTaskCts = owner;
        _longTaskCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LongTaskStatus = status;
        CanCancelLongTask = true;
        HasLongTask = true;
        return true;
    }

    public void CancelLongTask()
    {
        if (_longTaskCts is not { } owner || !CanCancelLongTask) return;
        CanCancelLongTask = false;
        LongTaskStatus = Loc.T("正在取消，等待当前步骤完成 …");
        owner.Cancel();
    }

    internal void ReportLongTask(string status)
    {
        if (CanCancelLongTask) LongTaskStatus = status;
    }

    internal void EndLongTask(CancellationTokenSource owner)
    {
        if (!ReferenceEquals(_longTaskCts, owner)) return;
        _longTaskCts = null;
        HasLongTask = false;
        CanCancelLongTask = false;
        owner.Dispose();
        _longTaskCompletion?.TrySetResult();
        _longTaskCompletion = null;
    }
}
