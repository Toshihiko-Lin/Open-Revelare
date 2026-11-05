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
    private bool _isReadingLensCorrection;
    private int _lensCorrectionGeneration;
    private Func<string, Task<LensCorrectionEstimate?>> _readLensCorrectionAsync =
        path => Task.Run(() => LensMetadata.EstimateCorrections(path));

    public async Task AutoLensCorrectionAsync(bool wholeRoll)
    {
        if (IsReadingLensCorrection || IsBusy || !_paramsLoaded || CurrentFrame is not { } frame) return;
        if (LensfunEnabled)
        {
            StatusText = Loc.T("请先关闭 Lensfun 校正，避免重复应用镜头校正");
            return;
        }
        var roll = _roll;
        var version = _editVersion;
        var generation = _lensCorrectionGeneration;
        var frames = Frames.ToArray();
        var selection = frames.Select(f => f.IsSelected).ToArray();
        bool hadSelection = selection.Any(s => s);
        var targets = wholeRoll
            ? frames.Where(f => !ReferenceEquals(f, frame) && (!hadSelection || f.IsSelected)).ToArray()
            : Array.Empty<RollFrame>();
        IsReadingLensCorrection = true;
        try
        {
            var estimate = await _readLensCorrectionAsync(frame.Path);
            if (_disposed || generation != _lensCorrectionGeneration || !ReferenceEquals(roll, _roll) || !ReferenceEquals(frame, CurrentFrame)
                || !_paramsLoaded || IsBusy || version != _editVersion || LensfunEnabled
                || !frames.SequenceEqual(Frames) || !selection.SequenceEqual(Frames.Select(f => f.IsSelected))) return;
            if (estimate is null || estimate.IsHeuristic || (!estimate.HasDistortion && !estimate.HasVignette))
            {
                LensCorrectionStatusText = estimate?.Summary ?? "";
                StatusText = Loc.T("未找到可应用的内嵌镜头校正参数，保留现有调整");
                return;
            }
            if (CropFrameSize is { W: > 1, H: > 1 } size && estimate.HasDistortion)
            {
                double aspect = (double)(Math.Max(size.W, size.H) - 1) / (Math.Min(size.W, size.H) - 1);
                estimate = estimate with { DistortionK1 = estimate.DistortionK1 * 3.25 / (1 + aspect * aspect) };
            }
            if ((estimate.HasDistortion && (!double.IsFinite(estimate.DistortionK1) || Math.Abs(estimate.DistortionK1) > 0.5))
                || (estimate.HasVignette && (!double.IsFinite(estimate.VignetteAmount) || estimate.VignetteAmount <= -1 || estimate.VignetteAmount > 2)))
            {
                StatusText = Loc.T("未找到可应用的内嵌镜头校正参数，保留现有调整");
                return;
            }
            bool vignette = estimate.HasVignette && !(LccEnabled && LccAvailable);
            if (!estimate.HasDistortion && !vignette) return;
            void Apply(FrameParams p)
            {
                if (estimate.HasDistortion) p.DistortionK1 = estimate.DistortionK1;
                if (vignette) { p.VignetteAmount = estimate.VignetteAmount; p.VignetteFalloff = estimate.VignetteFalloff; }
            }
            var prepared = BatchParameterEdit.Prepare(targets, Apply, frame);
            if (prepared.Any(x => x.Params.LensfunEnabled && x.Params.Lensfun is { IsUsable: true }))
            {
                StatusText = Loc.T("请先关闭 Lensfun 校正，避免重复应用镜头校正");
                return;
            }
            CommitUndo();
            foreach (var (target, next) in prepared) { target.Params = next; SetThumbnail(target, null); }
            if (estimate.HasDistortion) DistortionK1 = estimate.DistortionK1;
            if (vignette) { VignetteAmount = estimate.VignetteAmount; VignetteFalloff = estimate.VignetteFalloff; }
            MarkEdit();
            CommitUndo();
            LensCorrectionStatusText = estimate.Summary;
            StatusText = Loc.F($"已应用内嵌镜头校正：{targets.Length + 1} 帧");
            RestartThumbnails();
            ScheduleRender();
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(frame, CurrentFrame))
                StatusText = Loc.F($"批量操作未应用：{ex.Message}");
        }
        finally { IsReadingLensCorrection = false; }
    }
}
