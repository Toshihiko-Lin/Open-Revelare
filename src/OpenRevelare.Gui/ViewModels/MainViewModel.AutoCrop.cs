using OpenRevelare.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _autoCropCts;

    public bool IsAutoCropping => _autoCropCts is not null;
    public bool CanAutoCrop => HasImage && !IsBusy && !IsAutoCropping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoCropContentMode))]
    [NotifyPropertyChangedFor(nameof(AutoCropOutputHint))]
    private bool _autoCropIncludeSprockets;
    public bool AutoCropContentMode
    {
        get => !AutoCropIncludeSprockets;
        set { if (value) AutoCropIncludeSprockets = false; }
    }
    public string AutoCropOutputHint => AutoCropIncludeSprockets
        ? Loc.T("含齿孔：横向 37:35，竖向 35:37。结果需检查。")
        : Loc.T("仅画面：横向 3:2，竖向 2:3。结果需检查。");
    public string CropBatchDetectionLabel => HasFrameSelection
        ? Loc.F($"识别勾选 {SelectedFrameCount} 帧") : Loc.F($"识别整卷 {Frames.Count} 帧");

    [ObservableProperty] private bool _allowAutoCropSuggestions;
    private string _autoCropReviewReason = "";
    private AutoCropRecovery? _autoCropRecovery;
    public string AutoCropReviewText => Loc.T(_autoCropReviewReason);
    public bool HasAutoCropReview => !string.IsNullOrEmpty(_autoCropReviewReason);
    public bool HasAutoCropFailure => _autoCropReviewReason.StartsWith("未可靠识别：", StringComparison.Ordinal)
        || _autoCropReviewReason.StartsWith("含齿孔范围未完整拍入：", StringComparison.Ordinal);
    public string AutoCropFailureText => !HasAutoCropFailure ? "" : _autoCropReviewReason.StartsWith("含齿孔", StringComparison.Ordinal)
        ? Loc.T("未拍全片边，已保留原框；可改用仅画面或手动裁切。")
        : Loc.T("边界不清，已保留原框；可直接拖框调整。");
    public bool HasAutoCropRecovery => _autoCropRecovery is not null;
    private void LoadAutoCropReview(FrameParams p)
    {
        _autoCropReviewReason = p.AutoCropReviewReason; _autoCropRecovery = p.AutoCropRecovery;
        OnPropertyChanged(nameof(AutoCropReviewText)); OnPropertyChanged(nameof(HasAutoCropReview));
        OnPropertyChanged(nameof(HasAutoCropRecovery));
        NotifyAutoCropFailure();
    }
    private void NotifyAutoCropFailure()
    {
        OnPropertyChanged(nameof(HasAutoCropFailure)); OnPropertyChanged(nameof(AutoCropFailureText));
    }
    public void SelectAutoCropReviewFrames()
    {
        if (!CanAutoCrop) return;
        CommitLiveParams(CurrentFrame);
        foreach (var f in Frames) f.IsSelected = !string.IsNullOrEmpty(f.Params.AutoCropReviewReason);
        StatusText = Loc.F($"已选中 {Frames.Count(f => f.IsSelected)} 个待检查裁切帧");
    }
    private void ClearAutoCropReviewAfterManualEdit()
    {
        _autoCropReviewReason = "";
        OnPropertyChanged(nameof(AutoCropReviewText)); OnPropertyChanged(nameof(HasAutoCropReview));
        NotifyAutoCropFailure();
    }
    public void ConfirmAutoCropReview()
    {
        if (!CanAutoCrop || CurrentFrame is null) return;
        CommitUndo(); _autoCropReviewReason = "";
        OnPropertyChanged(nameof(AutoCropReviewText)); OnPropertyChanged(nameof(HasAutoCropReview));
        NotifyAutoCropFailure();
        MarkEdit(); CommitUndo();
    }
    public void RestoreAutoCropRecovery()
    {
        if (!CanAutoCrop || CurrentFrame is null || _autoCropRecovery is not {} recovery) return;
        CommitUndo(); CommitLiveParams(CurrentFrame);
        var next = CurrentFrame.Params.Clone();
        next.CropRect = recovery.HadCrop ? (recovery.X,recovery.Y,recovery.W,recovery.H) : null;
        next.QuarterTurns = recovery.QuarterTurns; next.FlipH = recovery.FlipH; next.FlipV = recovery.FlipV;
        next.Rotation = recovery.Rotation; next.AutoCropRecovery = null; next.AutoCropReviewReason = "";
        CurrentFrame.Params = next; LoadParams(next); _sprocketOverlayDirty = true;
        SetThumbnail(CurrentFrame,null); MarkEdit(); ScheduleRender(); CommitUndo(); RestartThumbnails();
        StatusText = Loc.T("已恢复本帧自动裁切前的裁切与拉直，可撤销。");
    }

    public void CancelAutoCrop() => _autoCropCts?.Cancel();

    public async Task AutoCrop135Async(FilmCropMode mode, bool wholeRoll)
    {
        if (_disposed || !_paramsLoaded || CurrentFrame is null || !HasImage || IsBusy || _autoCropCts is not null) return;
        CommitLiveParams(CurrentFrame);
        CommitUndo();
        bool selected = wholeRoll && Frames.Any(f => f.IsSelected);
        var targets = wholeRoll ? Frames.Where(f => !selected || f.IsSelected).ToArray() : new[] { CurrentFrame };
        var snapshots = targets.Select(f => (Frame: f, Params: f.Params.Clone())).ToArray();
        var rollSnapshots = Frames.Select(f => (Frame: f, Params: f.Params.Clone())).ToArray();
        var roll = _roll;
        var sourceFrame = CurrentFrame;
        var sourceSelection = Frames.Select(f => (f, f.IsSelected)).ToArray();
        var stamps = new List<SourceStamp>();
        long version = _editVersion;
        var cts = new CancellationTokenSource();
        _autoCropCts = cts;
        OnPropertyChanged(nameof(IsAutoCropping));
        OnPropertyChanged(nameof(CanAutoCrop));
        IsBusy = true;
        int failed = 0;
        bool allowSuggestions = AllowAutoCropSuggestions;
        try
        {
            var detections = new FilmAutoCrop.Detection?[targets.Length];
            // A frame the detector cannot confirm may still get a starting box; it is applied
            // further down, and only where the frame carries no crop of its own.
            var suggestions = new FilmAutoCrop.Suggestion?[targets.Length];
            var readable = new bool[targets.Length];
            for (int i = 0; i < snapshots.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var (frame, p) = snapshots[i];
                StatusText = Loc.F($"135 自动裁切：检测 {i + 1}/{targets.Length} …");
                // A strip-split crop belongs to a scan, not a camera frame. Applying a single
                // 135 rectangle to that whole scan could pick its neighbour instead.
                if (_splitPaths.Contains(frame.Path)) continue;
                try
                {
                    var outcome = await MeasureCropAsync(frame, p, mode, allowSuggestions, stamps, cts.Token);
                    detections[i] = outcome.Detection;
                    suggestions[i] = outcome.Suggestion;
                    readable[i] = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }
            }
            var references = FilmAutoCrop.BuildRollReferences(CachedRollCropEvidence(rollSnapshots, stamps));
            if (references.Count == 0 && detections.Any(d => d is null || !d.HasPerforationRuler))
            {
                // A single weak frame may borrow nearby physical anchors without applying
                // crops to them. Limit cold discovery; later retries reuse compact evidence.
                var targetSet = targets.ToHashSet();
                var attemptedSources = snapshots.Select(s => s.Frame.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                int attempted = 0;
                foreach (var (frame, p) in rollSnapshots)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (targetSet.Contains(frame) || frame.IsVirtual || _splitPaths.Contains(frame.Path)
                        || !attemptedSources.Add(frame.Path)) continue;
                    if (++attempted > 6) break;
                    StatusText = Loc.F($"135 辅助识别：读取参考 {attempted}/6 …");
                    try { await MeasureCropAsync(frame, p, mode, false, stamps, cts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { continue; }
                    references = FilmAutoCrop.BuildRollReferences(CachedRollCropEvidence(rollSnapshots, stamps));
                    if (references.Count > 0) break;
                }
            }
            if (references.Count > 0)
            {
                // Decode one weak frame at a time; never retain a roll of float previews.
                for (int i = 0; i < snapshots.Length; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!readable[i] || !FilmAutoCrop.NeedsRollReference(detections[i], references)) continue;
                    // A clear local ruler at a different magnification remains valid. The
                    // cached roll model is a reference, not a mandate to reject a new setup.
                    if (detections[i] is { HasPerforationRuler: true } local && double.IsFinite(local.FilmWidth)
                        && !references.Any(r => r.Width == local.Width && r.Height == local.Height
                            && Math.Abs(r.NormalisedFilmWidth / local.FilmWidth - 1) <= .035)) continue;
                    detections[i] = null;
                    StatusText = Loc.F($"135 自动裁切：整卷互校 {i + 1}/{targets.Length} …");
                    var (frame, p) = snapshots[i];
                    try
                    {
                        var stamp = SourceStamp.Capture(frame.Path);
                        var entry = await PreviewAsync(frame.Path, null).WaitAsync(cts.Token);
                        stamp.RequireCurrent(); stamps.Add(stamp);
                        var recovered = await Task.Run(() =>
                        {
                            bool swap = ((p.QuarterTurns % 2) + 2) % 2 == 1;
                            return FilmAutoCrop.DetectWithRollReference(FilmAutoCrop.Prepare(entry.Preview, p), references,
                                swap ? entry.SourceHeight : entry.SourceWidth, swap ? entry.SourceWidth : entry.SourceHeight, p.Rotation);
                        }, cts.Token);
                        if (recovered is not null && Math.Abs(p.Rotation + recovered.RotationCorrection) <= StraightenLimit)
                            detections[i] = recovered;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failed++; }
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            // A long RAW decode must not overwrite intervening edits or land in a newly opened roll.
            if (_disposed || !ReferenceEquals(roll, _roll) || !ReferenceEquals(sourceFrame, CurrentFrame)
                || version != _editVersion || !sourceSelection.SequenceEqual(Frames.Select(f => (f, f.IsSelected)))
                || targets.Any(f => !Frames.Contains(f)))
            {
                StatusText = Loc.T("135 自动裁切未应用：检测期间参数或胶卷已改变，请重试。");
                return;
            }
            foreach (var stamp in stamps) stamp.RequireCurrent();
            var context = CachedRollCropEvidence(rollSnapshots, stamps, targets.ToHashSet())
                .Where(anchor => anchor is not null && detections.Any(d => CompatibleCropScale(d, anchor)));
            var planning = detections.Concat(context).ToArray();
            foreach (var stamp in stamps) stamp.RequireCurrent();
            var plan = FilmAutoCrop.Plan(planning, mode);
            var prepared = snapshots.Select((s, i) => (s.Frame, Params: s.Params, Crop: plan[i], Detection: detections[i]))
                .Where(s => s.Crop is not null).ToArray();
            // A guess is a starting point, not a correction: never replace a crop the user already
            // placed by hand, or re-running the command would quietly discard that work.
            var guessed = snapshots
                .Select((s, i) => (s.Frame, Params: s.Params, Crop: plan[i] is null ? suggestions[i] : null))
                .Where(s => s.Crop is not null && s.Frame.Params.CropRect is null).ToArray();
            var applied = prepared.Select(s=>s.Frame).Concat(guessed.Select(s=>s.Frame)).ToHashSet();
            var contentPlan = mode == FilmCropMode.WithSprockets ? FilmAutoCrop.Plan(planning, FilmCropMode.ImageArea) : null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                var s = snapshots[i];
                if (applied.Contains(s.Frame)) continue;
                s.Params.AutoCropReviewReason = contentPlan?[i] is not null
                    ? "含齿孔范围未完整拍入：保留原裁切，可改用仅画面或手动裁切。"
                    : "未可靠识别：保留原裁切，请手动拖框或从参考帧同步裁切。";
                s.Frame.Params = s.Params;
            }
            // Publish once: failure or cancellation during detection leaves every crop intact.
            foreach (var s in prepared)
            {
                var before = s.Params.Clone();
                s.Params.AutoCropRecovery = AutoCropRecovery.Capture(s.Params);
                s.Params.AutoCropReviewReason = s.Detection!.UsedRollReference
                    ? "整卷互校结果：借助其它帧的尺寸与本帧边线定位，请检查；不满意可恢复。"
                    : "边界辅助结果：可直接拖框调整；不满意可撤销。";
                var r = s.Crop!.Value;
                s.Params.Rotation += s.Detection!.RotationCorrection;
                s.Params.CropRect = (r.X, r.Y, r.W, r.H);
                RememberAppliedCropEvidence(s.Frame, before, s.Params, s.Detection!);
                s.Frame.Params = s.Params;
                SetThumbnail(s.Frame, null);
            }
            // The guess carries the strip's levelling angle, so the frame is straightened and the
            // box lands parallel to the film edge instead of across it.
            foreach (var s in guessed)
            {
                s.Params.AutoCropRecovery = AutoCropRecovery.Capture(s.Params);
                s.Params.AutoCropReviewReason = "推测框仅供参考，请直接拖框调整；不满意可撤销。";
                var r = s.Crop!.Value.Rect;
                s.Params.Rotation += s.Crop!.Value.RotationCorrection;
                s.Params.CropRect = (r.X, r.Y, r.W, r.H);
                s.Frame.Params = s.Params;
                SetThumbnail(s.Frame, null);
            }
            if (targets.Contains(CurrentFrame)) LoadParams(CurrentFrame!.Params);
            _sprocketOverlayDirty = true;
            MarkEdit();
            ScheduleRender();
            CommitUndo();
            RestartThumbnails();
            StatusText = Loc.F($"已裁切 {prepared.Length + guessed.Length} 帧，保留原框 {targets.Length-prepared.Length-guessed.Length} 帧。");
            if (failed > 0) StatusText += " " + Loc.F($"读取失败 {failed} 帧。");
            if (guessed.Length > 0) StatusText += " " + Loc.F($"含推测 {guessed.Length} 帧，请检查。");
        }
        catch (OperationCanceledException) { StatusText = Loc.T("135 自动裁切已取消，原裁切未改变。"); }
        catch (Exception ex) { StatusText = Loc.T("135 自动裁切失败：") + ex.Message; }
        finally
        {
            if (ReferenceEquals(_autoCropCts, cts)) _autoCropCts = null;
            OnPropertyChanged(nameof(IsAutoCropping));
            OnPropertyChanged(nameof(CanAutoCrop));
            cts.Dispose();
            if (ReferenceEquals(roll, _roll) && !_disposed) IsBusy = false;
        }
    }
}
