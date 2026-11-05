using OpenRevelare.Core;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private LensfunCalibration? _lensfunCalibration;
    private bool _lensfunEnabled;
    public bool LensfunEnabled
    {
        get => _lensfunEnabled;
        set
        {
            if (value && !_suppressRender && !_configLoad && !_restoring && HasEmbeddedLensAdjustments())
            {
                StatusText = Loc.T("仍有内嵌基础校正：请先关闭整卷自动校正，再启用 Lensfun。");
                OnPropertyChanged(nameof(LensfunEnabled));
                return;
            }
            if (SetProperty(ref _lensfunEnabled, value)) OnLensfunEnabledChanged(value);
        }
    }
    public bool LensfunAvailable => _lensfunCalibration is { IsUsable: true };
    public string LensfunProfileText => _lensfunCalibration is { } c
        ? Loc.F($"Lensfun 配置：{c.Profile}") : Loc.T("未匹配 Lensfun 镜头配置");
    public string LensfunDetails => _lensfunCalibration is not { } c ? Loc.T("按当前帧拍摄参数匹配；手动镜头校正仍可使用。")
        : Loc.F($"{c.Camera} · {c.FocalLength:0.##} mm · 畸变：{(c.Distortion?.Model ?? Loc.T("无可用参数"))} · 暗角：{(LccEnabled && LccAvailable ? Loc.T("由 LCC 处理") : c.Vignetting is null ? Loc.T("无可用参数") : "Lensfun")}");

    private void NotifyLensfunInfo()
    {
        OnPropertyChanged(nameof(LensfunAvailable));
        OnPropertyChanged(nameof(LensfunProfileText));
        OnPropertyChanged(nameof(LensfunDetails));
    }

    private void AdoptLensfun(LensfunCalibration? calibration, bool enabled)
    {
        _lensfunCalibration = calibration;
        _lensfunEnabled = enabled && calibration is { IsUsable: true };
        OnPropertyChanged(nameof(LensfunEnabled));
        NotifyLensfunInfo();
    }

    private void OnLensfunEnabledChanged(bool value)
    {
        if (_suppressRender || _configLoad || _restoring) return;
        foreach (var frame in Frames) frame.Params.LensfunEnabled = value && frame.Params.Lensfun is { IsUsable: true };
        InvalidateOpticalCalibration();
        NotifyLensfunInfo();
        if (_roll is not null && _paramsLoaded)
        {
            foreach (var frame in Frames) SetThumbnail(frame, null);
            MarkRollDirty();
            RestartThumbnails();
        }
    }
}
