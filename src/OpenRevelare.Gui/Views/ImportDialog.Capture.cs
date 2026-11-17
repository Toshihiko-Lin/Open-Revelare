using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

public partial class ImportDialog
{
    private DeviceCalibrationProfile[] _captureProfiles = [];
    private DeviceCalibrationProfile? SelectedCaptureProfile => CaptureProfileChoice.SelectedIndex > 0
        && CaptureProfileChoice.SelectedIndex <= _captureProfiles.Length ? _captureProfiles[CaptureProfileChoice.SelectedIndex - 1] : null;
    private CaptureWorkflowMode SelectedCaptureMode => CaptureModeChoice.SelectedIndex == 1
        ? SelectedCaptureProfile?.Kind == CalibrationDeviceKind.CameraCopy
            ? CaptureWorkflowMode.CameraTransmission : CaptureWorkflowMode.ScannerInputIcc
        : CaptureWorkflowMode.RecordUpstream;

    private void ReloadCaptureProfiles()
    {
        string? selected = SelectedCaptureProfile?.Id;
        var store = new DeviceCalibrationStore(Path.Combine(Settings.DataDir, "device-calibrations"));
        _captureProfiles = store.LoadAll();
        CaptureProfileChoice.ItemsSource = new[] { Loc.T("不使用设备校准配置") }
            .Concat(_captureProfiles.Select(p => p.Name + " · " + p.DeviceModel)).ToArray();
        CaptureProfileChoice.SelectedIndex = Math.Max(0, Array.FindIndex(_captureProfiles, p => p.Id == selected) + 1);
        RefreshCaptureMode();
        if (store.LoadErrors.Length > 0) CaptureWorkflowHint.Text += "\n" + string.Join("\n", store.LoadErrors);
    }
    private void OnCaptureProfileChanged(object? sender, SelectionChangedEventArgs e) => RefreshCaptureMode();
    private void RefreshCaptureMode()
    {
        if (CaptureModeChoice is null || CaptureWorkflowHint is null) return;
        bool camera = SelectedCaptureProfile?.Kind == CalibrationDeviceKind.CameraCopy;
        CaptureModeChoice.ItemsSource = new[] { Loc.T("仅记录采集／上游校准流程"),
            Loc.T(camera ? "应用相机暗场／无片场校正" : "应用扫描仪输入 ICC") };
        CaptureModeChoice.SelectedIndex = 0;
        CaptureModeChoice.IsEnabled = SelectedCaptureProfile is not null;
        UpdateCaptureHint();
    }
    private void OnCaptureModeChanged(object? sender, SelectionChangedEventArgs e) => UpdateCaptureHint();
    private void UpdateCaptureHint()
    {
        if (CaptureWorkflowHint is null) return;
        CaptureWorkflowHint.Text = SelectedCaptureProfile is null
            ? Loc.T("先在设备校准中保存配置，再在这里选择。")
            : SelectedCaptureMode == CaptureWorkflowMode.RecordUpstream
                ? Loc.T("随工程保存配置和原始文件摘要，沿用当前输入；本软件不重复应用上游校准。")
                : SelectedCaptureMode == CaptureWorkflowMode.CameraTransmission
                    ? Loc.T("确认与参考使用同一机身、光源、镜头及设置。先按 CFA 求相对透射率，再双线性插值为 RGB；无效测量会中止。生成原片旁的浮点 TIFF 副本，后续预览、反转和导出统一读取副本。")
                    : Loc.T("确认扫描设置匹配配置，输入仍为未做颜色转换的设备 RGB。先用输入 ICC 转为线性 ACEScg，生成原片旁的浮点 TIFF 副本；含嵌入 ICC 的文件会中止，已在上游转换时请选择仅记录。ICC 不等于物理密度校准。");
    }
    private async void OnManageCaptureProfiles(object? sender, RoutedEventArgs e)
    {
        await new DeviceCalibrationDialog().ShowDialog(this);
        ReloadCaptureProfiles();
    }
}
