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
    [NotifyPropertyChangedFor(nameof(DisplayStatus), nameof(ShowProgress), nameof(CanRestoreProjectReferences), nameof(CanShowBatchExportReport), nameof(CanInspectProjectDependencies))]
    private bool _hasLongTask;

    [ObservableProperty] private bool _canCancelLongTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private string _longTaskStatus = "";

    public string DisplayStatus => HasLongTask ? LongTaskStatus : StatusText;
    public bool ShowProgress => HasLongTask || IsBusy;

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
