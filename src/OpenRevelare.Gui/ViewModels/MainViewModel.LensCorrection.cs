using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLensCorrectionStatus))]
    private string _lensCorrectionStatusText = "";

    public bool HasLensCorrectionStatus => !string.IsNullOrWhiteSpace(LensCorrectionStatusText);

    /// <summary>
    /// Reads lens profile (distortion and vignette parameters) from the current frame's RAW metadata,
    /// applies it to the current frame, and optionally broadcasts to all selected frames or the entire roll.
    /// </summary>
    public async Task AutoLensCorrectionAsync(bool wholeRoll)
    {
        if (CurrentFrame is null || string.IsNullOrWhiteSpace(CurrentFrame.Path))
        {
            StatusText = Loc.T("请先选择包含 RAW 图像的帧");
            return;
        }

        string path = CurrentFrame.Path;
        var estimate = await Task.Run(() => LensMetadata.EstimateCorrections(path));
        if (estimate is null)
        {
            StatusText = Loc.T("未在图像元数据中检测到镜头校正参数");
            return;
        }

        CommitUndo();

        DistortionK1 = estimate.DistortionK1;
        VignetteAmount = estimate.VignetteAmount;
        VignetteFalloff = estimate.VignetteFalloff;
        MarkEdit();

        LensCorrectionStatusText = string.IsNullOrEmpty(estimate.LensModel)
            ? estimate.SourceDescription
            : $"{estimate.LensModel} · {estimate.SourceDescription}";

        if (wholeRoll)
        {
            bool hadSelection = Frames.Any(f => f.IsSelected);
            var targets = Frames.Where(f => !ReferenceEquals(f, CurrentFrame) && (!hadSelection || f.IsSelected)).ToList();
            if (targets.Count > 0)
            {
                TryApplyBatch(targets, p =>
                {
                    p.DistortionK1 = estimate.DistortionK1;
                    p.VignetteAmount = estimate.VignetteAmount;
                    p.VignetteFalloff = estimate.VignetteFalloff;
                }, CurrentFrame);

                if (hadSelection) ClearStripSelection();
                RestartThumbnails();
            }

            int count = targets.Count + 1;
            StatusText = hadSelection
                ? Loc.F($"已为选中的 {count} 帧应用镜头校正：{estimate.LensModel}（畸变 {estimate.DistortionK1:+0.000;-0.000;0}，暗角 {estimate.VignetteAmount:0.00}）")
                : Loc.F($"已为全卷（{count} 帧）应用镜头校正：{estimate.LensModel}（畸变 {estimate.DistortionK1:+0.000;-0.000;0}，暗角 {estimate.VignetteAmount:0.00}）");
        }
        else
        {
            StatusText = Loc.F($"已应用镜头校正：{estimate.LensModel}（畸变 {estimate.DistortionK1:+0.000;-0.000;0}，暗角 {estimate.VignetteAmount:0.00}）");
        }

        CommitUndo();
        ScheduleRender();
    }
}
