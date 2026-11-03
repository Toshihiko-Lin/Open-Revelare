using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
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
