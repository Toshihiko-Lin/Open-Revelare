using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public sealed partial class MainViewModel
{
    // Only publish decoder provenance for the frame that supplied the preview.
    private string? _calibrationInfoFramePath;

    public string InputCharacterizationText
    {
        get
        {
            if (CurrentFrame is not { } frame) return Loc.T("输入：未载入");
            if (RawDecode.IsRawExtension(frame.Path))
                return Loc.T("输入：相机原生 RAW · 未做设备颜色表征");
            if (_calibrationInfoFramePath != frame.Path || _previewWorking is null)
                return Loc.T("输入：正在读取色彩来源…");
            if (_previewWorking.Admission == WorkingAdmission.LegacyPartialIccTransform)
                return Loc.T("输入：旧版部分色彩转换 · 可在技术报告中查看");
            return _previewWorking.Source.OriginalEncoding is CharacterizedPixelEncoding
                ? Loc.T("输入：按配置文件解释 · 不代表密度已标定")
                : Loc.T("输入：未表征 · 按已记录的输入约定处理");
        }
    }

    public string DensityCorrectionText => Monochrome
        ? Loc.T("密度：黑白共用端点 · 不应用彩色色度补偿")
        : _decoupleChromaMatrix is not null
            ? Loc.T("密度：端点校准 + 本卷分光色度补偿")
            : Loc.T("密度：端点校准 · 未应用分光色度补偿");

    public string CaptureCorrectionText => _decoupleMatrix is not null
        ? Loc.T("采集校正：已应用 RGB 分光解耦（反相前）")
        : HasCaptureCorrectionReferences
            ? Loc.T("采集校正：未应用 · 校正参考未成功载入")
            : Loc.T("采集校正：未应用 RGB 分光解耦");

    private bool HasCaptureCorrectionReferences =>
        !string.IsNullOrWhiteSpace(_calSourceDir)
        || _calRgbPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) == true;

    public string CaptureCorrectionSourceText => _calRgbPaths is { Length: 3 } rgb
        ? string.Join(Environment.NewLine,
            Loc.F($"R 参考：{rgb[0]}"), Loc.F($"G 参考：{rgb[1]}"), Loc.F($"B 参考：{rgb[2]}"))
        : !string.IsNullOrWhiteSpace(_calSourceDir)
            ? Loc.F($"校正参考目录：{_calSourceDir}")
            : Loc.T("校正来源：未提供 RGB 分光参考图");

    public string DensityCorrectionSourceText => Monochrome
        ? Loc.T("黑白模式共用一组密度端点；已保存的彩色色度补偿不参与当前反相。")
        : _decoupleChromaMatrix is not null
        ? Loc.T("色度补偿来源：本卷内容帧解耦前后的色度统计；不是独立标准密度拟合。")
        : Loc.T("密度来源：当前片基与亮端参数；采样或自动分析记录见下方各项。未建立标准密度映射。");

    public string CaptureCorrectionScopeText => Loc.T(
        "RGB 分光参考须与本卷使用相同设备、光源和采集设置。当前未自动核验硬件或参考内容指纹；更换条件后需重新导入校正参考并校准。LUT 只决定外观，不参与设备标定。");

    private void NotifyCalibrationInfo()
    {
        OnPropertyChanged(nameof(InputCharacterizationText));
        OnPropertyChanged(nameof(DensityCorrectionText));
        OnPropertyChanged(nameof(CaptureCorrectionText));
        OnPropertyChanged(nameof(CaptureCorrectionSourceText));
        OnPropertyChanged(nameof(DensityCorrectionSourceText));
        OnPropertyChanged(nameof(CaptureCorrectionScopeText));
    }
}
