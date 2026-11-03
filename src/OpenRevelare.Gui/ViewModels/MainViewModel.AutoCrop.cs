using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _autoCropCts;

    public bool IsAutoCropping => _autoCropCts is not null;

    public void CancelAutoCrop() => _autoCropCts?.Cancel();

    public async Task AutoCrop135Async(FilmCropMode mode, bool wholeRoll)
    {
        if (CurrentFrame is null || !HasImage || IsBusy || _autoCropCts is not null) return;
        CommitLiveParams(CurrentFrame);
        CommitUndo();
        bool selected = wholeRoll && Frames.Any(f => f.IsSelected);
        var targets = wholeRoll ? Frames.Where(f => !selected || f.IsSelected).ToArray() : new[] { CurrentFrame };
        var snapshots = targets.Select(f => (Frame: f, Params: f.Params.Clone())).ToArray();
        var roll = _roll;
        long version = _editVersion;
        var cts = new CancellationTokenSource();
        _autoCropCts = cts;
        OnPropertyChanged(nameof(IsAutoCropping));
        IsBusy = true;
        int failed = 0;
        try
        {
            var detections = new FilmAutoCrop.Detection?[targets.Length];
            // A frame the detector cannot confirm may still get a starting box; it is applied
            // further down, and only where the frame carries no crop of its own.
            var suggestions = new FilmAutoCrop.Suggestion?[targets.Length];
            for (int i = 0; i < snapshots.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var (frame, p) = snapshots[i];
                StatusText = Loc.F($"135 自动裁切：检测 {i + 1}/{targets.Length} …");
                // A strip-split crop belongs to a scan, not a camera frame. Applying a single
                // 135 rectangle to that whole scan could pick its neighbour instead.
                if (_splitPaths.Contains(frame.Path)) { failed++; continue; }
                try
                {
                    var entry = await PreviewAsync(frame.Path, null).WaitAsync(cts.Token);
                    var outcome = await Task.Run(() =>
                    {
                        var pixels = FilmAutoCrop.Prepare(entry.Preview, p);
                        var d = FilmAutoCrop.Detect(pixels);
                        bool swap = ((p.QuarterTurns % 2) + 2) % 2 == 1;
                        var confirmed = d is null || Math.Abs(p.Rotation + d.RotationCorrection) > StraightenLimit ? null : d with
                        {
                            Width = swap ? entry.SourceHeight : entry.SourceWidth,
                            Height = swap ? entry.SourceWidth : entry.SourceHeight,
                        };
                        // The perforations outlive the exposure edges, so they can still place a
                        // box when the gate itself cannot be read. Its levelling angle goes into
                        // the frame exactly like a confirmed detection's, but the straighten
                        // control cannot represent more than its own range: past that the box
                        // would be placed in a rotation the frame cannot take.
                        var suggestion = confirmed is null ? FilmAutoCrop.Suggest(pixels, mode) : null;
                        if (suggestion is { } sg && Math.Abs(p.Rotation + sg.RotationCorrection) > StraightenLimit)
                            suggestion = null;
                        return (Detection: confirmed, Suggestion: suggestion);
                    }, cts.Token);
                    detections[i] = outcome.Detection;
                    suggestions[i] = outcome.Suggestion;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }
            }
            cts.Token.ThrowIfCancellationRequested();
            // A long RAW decode must not overwrite intervening edits or land in a newly opened roll.
            if (!ReferenceEquals(roll, _roll) || version != _editVersion || targets.Any(f => !Frames.Contains(f)))
            {
                StatusText = Loc.T("135 自动裁切未应用：检测期间参数或胶卷已改变，请重试。");
                return;
            }
            var plan = FilmAutoCrop.Plan(detections, mode);
            var prepared = snapshots.Select((s, i) => (s.Frame, Params: s.Params, Crop: plan[i], Detection: detections[i]))
                .Where(s => s.Crop is not null).ToArray();
            // A guess is a starting point, not a correction: never replace a crop the user already
            // placed by hand, or re-running the command would quietly discard that work.
            var guessed = snapshots
                .Select((s, i) => (s.Frame, Params: s.Params, Crop: plan[i] is null ? suggestions[i] : null))
                .Where(s => s.Crop is not null && s.Frame.Params.CropRect is null).ToArray();
            if (prepared.Length == 0 && guessed.Length == 0)
            {
                StatusText = Loc.T("135 自动裁切：没有可靠的完整画格，已保留原裁切。请使用拖框裁切检查边界。");
                return;
            }
            // Publish once: failure or cancellation during detection leaves every crop intact.
            foreach (var s in prepared)
            {
                var r = s.Crop!.Value;
                s.Params.Rotation += s.Detection!.RotationCorrection;
                s.Params.CropRect = (r.X, r.Y, r.W, r.H);
                s.Frame.Params = s.Params;
                SetThumbnail(s.Frame, null);
            }
            // The guess carries the strip's levelling angle, so the frame is straightened and the
            // box lands parallel to the film edge instead of across it.
            foreach (var s in guessed)
            {
                var r = s.Crop!.Value.Rect;
                s.Params.Rotation += s.Crop!.Value.RotationCorrection;
                s.Params.CropRect = (r.X, r.Y, r.W, r.H);
                s.Frame.Params = s.Params;
                SetThumbnail(s.Frame, null);
            }
            if (prepared.Any(s => ReferenceEquals(s.Frame, CurrentFrame)) ||
                guessed.Any(s => ReferenceEquals(s.Frame, CurrentFrame))) LoadParams(CurrentFrame!.Params);
            _sprocketOverlayDirty = true;
            MarkEdit();
            ScheduleRender();
            CommitUndo();
            RestartThumbnails();
            if (selected) ClearStripSelection();
            StatusText = guessed.Length == 0
                ? Loc.F($"135 自动裁切完成：{prepared.Length} 帧，跳过 {targets.Length - prepared.Length} 帧（含读取失败 {failed} 帧）；同尺寸同方向统一尺度，可撤销。")
                : Loc.F($"135 自动裁切完成：{prepared.Length} 帧可靠，{guessed.Length} 帧按齿孔与片边推测（请用拖框确认）；跳过 {targets.Length - prepared.Length - guessed.Length} 帧，可撤销。");
        }
        catch (OperationCanceledException) { StatusText = Loc.T("135 自动裁切已取消，原裁切未改变。"); }
        catch (Exception ex) { StatusText = Loc.T("135 自动裁切失败：") + ex.Message; }
        finally
        {
            if (ReferenceEquals(_autoCropCts, cts)) _autoCropCts = null;
            OnPropertyChanged(nameof(IsAutoCropping));
            cts.Dispose();
            IsBusy = false;
        }
    }
}
