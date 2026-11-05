using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLensCorrectionStatus))]
    private string _lensCorrectionStatusText = "";

    public bool HasLensCorrectionStatus => !string.IsNullOrWhiteSpace(LensCorrectionStatusText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLensCorrection))]
    private bool _isReadingLensCorrection;
    public bool CanEditLensCorrection => HasImage && !IsBusy && !IsReadingLensCorrection;
    private int _lensCorrectionGeneration;
    private EmbeddedLensCalibration? _embeddedLens;
    private string _lensCorrectionDiagnostic = "";
    private string _lensCorrectionOrigin = "";
    private bool _lensCorrectionHasVignette;
    private string LensOriginText() => LensOriginSummary() + (string.IsNullOrEmpty(_lensCorrectionDiagnostic) ? "" : " · " + Loc.T(_lensCorrectionDiagnostic));
    private string LensOriginSummary() => _embeddedLens is {} native ? Loc.F($"自动校正来源：{native.Source}；滑块为额外微调") : string.IsNullOrEmpty(_lensCorrectionOrigin)
        ? (LensfunEnabled ? Loc.T("自动校正来源：Lensfun；滑块为额外手动调整") : Loc.T("自动校正：未应用；滑块可手动调整"))
        : Loc.F($"滑块来源：{_lensCorrectionOrigin}（可继续手动微调）");

    private bool HasEmbeddedLensAdjustments() => _embeddedLens is not null || !string.IsNullOrEmpty(_lensCorrectionOrigin)
        || Frames.Any(f => !ReferenceEquals(f, CurrentFrame) && (f.Params.EmbeddedLens is not null || !string.IsNullOrEmpty(f.Params.LensCorrectionOrigin)));

    public void DisableAutoLensCorrection(bool wholeRoll)
    {
        if (!_paramsLoaded || CurrentFrame is null || IsBusy || IsReadingLensCorrection) return;
        CommitUndo();
        var targets = wholeRoll ? Frames.ToArray() : new[] { CurrentFrame };
        foreach (var frame in targets)
        {
            var p = frame.Params;
            if (p.EmbeddedLens is null && !string.IsNullOrEmpty(p.LensCorrectionOrigin))
            { p.DistortionK1 = 0; p.VignetteAmount = 0; p.VignetteFalloff = 2.5; }
            p.EmbeddedLens = null; p.LensfunEnabled = false;
            p.LensCorrectionDiagnostic = ""; p.LensCorrectionOrigin = ""; p.LensCorrectionHasVignette = false;
            SetThumbnail(frame,null);
        }
        LoadParams(CurrentFrame.Params); MarkEdit(); CommitUndo(); RestartThumbnails(); ScheduleRender();
    }

    public void ResetLensSliders(bool wholeRoll)
    {
        if (!_paramsLoaded || CurrentFrame is null || IsBusy || IsReadingLensCorrection) return;
        CommitUndo();
        if (wholeRoll)
            foreach (var f in Frames)
            {
                f.Params.DistortionK1 = 0; f.Params.VignetteAmount = 0; f.Params.VignetteFalloff = 2.5;
                if (f.Params.EmbeddedLens is null) { f.Params.LensCorrectionOrigin = ""; f.Params.LensCorrectionHasVignette = false; }
                SetThumbnail(f, null);
            }
        if (_embeddedLens is null) { _lensCorrectionOrigin = ""; _lensCorrectionHasVignette = false; }
        DistortionK1 = 0; VignetteAmount = 0; VignetteFalloff = 2.5;
        LensCorrectionStatusText = LensOriginText();
        MarkEdit(); CommitUndo(); RestartThumbnails(); ScheduleRender();
    }

    public void SyncLensSliders(bool wholeRoll)
    {
        if (!_paramsLoaded || CurrentFrame is null || IsBusy || IsReadingLensCorrection) return;
        CommitLiveParams(CurrentFrame);
        var source = CurrentFrame.Params.Clone();
        var targets = Frames.Where(f => !ReferenceEquals(f, CurrentFrame) && (wholeRoll || f.IsSelected)).ToArray();
        if (source.EmbeddedLens is null && !string.IsNullOrEmpty(source.LensCorrectionOrigin)
            && targets.Any(f => f.Params.LensfunEnabled || f.Params.EmbeddedLens is not null))
        {
            StatusText = Loc.T("目标帧的 Lensfun 状态不同，请先统一校正方式再同步滑块。");
            return;
        }
        if (!TryApplyBatch(targets, p =>
        {
            p.DistortionK1 = source.DistortionK1; p.VignetteAmount = source.VignetteAmount;
            p.VignetteFalloff = source.VignetteFalloff;
            if (p.EmbeddedLens is null)
            {
                p.LensCorrectionOrigin = source.EmbeddedLens is null ? source.LensCorrectionOrigin : "";
                p.LensCorrectionHasVignette = source.EmbeddedLens is null && source.LensCorrectionHasVignette;
            }
        }, CurrentFrame)) return;
        StatusText = Loc.F($"已同步当前镜头滑块：{targets.Length} 个目标帧");
        RestartThumbnails();
    }
    private Func<string, Task<LensCorrectionEstimate?>> _readLensCorrectionAsync =
        path => Task.Run(() => LensDetectionCache.Get(path).Embedded);

    private Func<string, Task<LensfunCalibration?>> _matchLensfunAsync =
        path => Task.Run(() => LensDetectionCache.Get(path).Lensfun);

    public async Task AutoLensCorrectionAsync(bool wholeRoll)
    {
        if (IsReadingLensCorrection || IsBusy || !_paramsLoaded || CurrentFrame is not { } frame) return;
        var roll = _roll;
        var version = _editVersion;
        var generation = _lensCorrectionGeneration;
        bool previousLensfun = LensfunEnabled;
        var frames = Frames.ToArray();
        var selection = frames.Select(f => f.IsSelected).ToArray();
        var targets = wholeRoll ? frames : new[] { frame };
        IsReadingLensCorrection = true;
        try
        {
            CommitLiveParams(frame);
            var prepared = new List<(RollFrame Frame, FrameParams Params)>();
            foreach (var target in targets)
            {
                var estimate = await _readLensCorrectionAsync(target.Path);
                // Prefer embedded data even if Lensfun is currently enabled.
                var calibration = AutoLensCorrection.IsUsable(estimate) ? null
                    : await _matchLensfunAsync(target.Path);
                var choice = AutoLensCorrection.Select(estimate, calibration);
                var next = target.Params.Clone();
                choice.Apply(next, LccEnabled && LccAvailable);
                prepared.Add((target, next));
            }
            if (_disposed || generation != _lensCorrectionGeneration || !ReferenceEquals(roll, _roll)
                || !ReferenceEquals(frame, CurrentFrame) || !_paramsLoaded || IsBusy || version != _editVersion
                || previousLensfun != LensfunEnabled || !frames.SequenceEqual(Frames)
                || !selection.SequenceEqual(Frames.Select(f => f.IsSelected))) return;
            // Validate all sources before publishing a single atomic undoable change.
            BatchParameterEdit.Prepare(targets, _ => { }, frame);
            CommitUndo();
            foreach (var (target, next) in prepared) { target.Params = next; SetThumbnail(target, null); }
            LoadParams(frame.Params);
            MarkEdit(); CommitUndo();
            LensCorrectionStatusText = LensOriginText();
            int embeddedCount = prepared.Count(x => x.Params.EmbeddedLens is not null || !string.IsNullOrEmpty(x.Params.LensCorrectionOrigin));
            int lensfunCount = prepared.Count(x => x.Params.LensfunEnabled);
            StatusText = Loc.F($"自动校正：内嵌 {embeddedCount} 帧，Lensfun {lensfunCount} 帧，未应用 {targets.Length-embeddedCount-lensfunCount} 帧");

            RestartThumbnails(); ScheduleRender();
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(frame, CurrentFrame)) StatusText = Loc.F($"批量操作未应用：{ex.Message}");
        }
        finally { IsReadingLensCorrection = false; }
    }
}
