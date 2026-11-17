using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed partial class DeviceCalibrationDialog : Window
{
    private readonly DeviceCalibrationStore _store = new(Path.Combine(Settings.DataDir, "device-calibrations"));
    private DeviceCalibrationProfile[] _profiles = [];
    private string _id = Guid.NewGuid().ToString("N");
    private readonly List<CalibrationReference> _references = [];
    private readonly ComboBox _saved = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _kind = new() { SelectedIndex = 0 };
    private readonly ComboBox _role = new() { SelectedIndex = 0 };
    private readonly ComboBox _measurement = new() { SelectedIndex = 0 };
    private readonly ComboBox _inputState = new() { SelectedIndex = 0 };
    private readonly TextBox _definition = new(), _light = new(), _decode = new(), _applied = new();
    private readonly TextBox _lens = new(), _aperture = new(), _iso = new(), _exposure = new(), _geometry = new();
    private readonly TextBox _scanSoftware = new(), _scanVersion = new(), _resolution = new(), _scanExposure = new(), _scanExport = new();
    private readonly ComboBox _scanMode = new() { SelectedIndex = 0 }, _scanBlack = new() { SelectedIndex = 0 }, _scanSpatial = new() { SelectedIndex = 0 };
    private readonly ComboBox _scanWorkflow = new() { SelectedIndex = 0 };
    private readonly StackPanel _cameraFields = new() { Spacing = 8 }, _scannerFields = new() { Spacing = 8 };
    private readonly TextBlock _measurementNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _name = new(), _model = new(), _serial = new(), _conditions = new(), _target = new(), _notes = new();
    private readonly TextBlock _guide = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _files = new() { MinHeight = 90, MaxHeight = 160 };
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly WrapPanel _actions;

    public DeviceCalibrationDialog()
    {
        Title = Loc.T("设备校准"); Classes.Add("dialog");
        Width = 780; Height = 780; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _kind.ItemsSource = new[] { Loc.T("相机翻拍"), Loc.T("扫描仪") };
        _role.ItemsSource = new[] { Loc.T("暗场"), Loc.T("无片场"), Loc.T("平场"), Loc.T("色靶图像"), Loc.T("色靶参考数据"), Loc.T("输入 ICC（来源资料）"), Loc.T("光源光谱资料"), Loc.T("相机响应资料"), Loc.T("独立验证扫描") };
        _measurement.ItemsSource = new[] { Loc.T("设备通道相对透射率"), Loc.T("指定光谱响应的透射率"), Loc.T("指定标准的密度") };
        _inputState.ItemsSource = new[] { Loc.T("输入处理未知"), Loc.T("传感器原始采样"), Loc.T("线性设备通道"), Loc.T("已做颜色转换的像素") };
        _scanMode.ItemsSource = new[] { Loc.T("扫描模式未知"), Loc.T("透射扫描"), Loc.T("反射扫描") };
        _scanBlack.ItemsSource = new[] { Loc.T("上游处理未知"), Loc.T("上游未执行"), Loc.T("上游已执行") };
        _scanSpatial.ItemsSource = new[] { Loc.T("上游处理未知"), Loc.T("上游未执行"), Loc.T("上游已执行") };
        _scanWorkflow.ItemsSource = new[] { Loc.T("选择已有校准流程"), "SilverFast IT8", "VueScan IT8", "ArgyllCMS", Loc.T("厂家维护校准"), Loc.T("高级：实测密度响应") };
        _body.Children.Add(new TextBlock { Text = Loc.T("登记设备与参考材料"), Classes = { "dialogTitle" } });
        _body.Children.Add(Action("素材采集引导…", () => new CalibrationCollectionGuide(_kind.SelectedIndex == 0).ShowDialog(this)));
        _body.Children.Add(new TextBlock { Text = Loc.T("设备校准用于物理采集测量。支持相机 dark/open 试算和扫描仪参考响应拟合，尚不应用到胶卷。请先阅读素材采集引导，参考文件保留在原位置。"), TextWrapping = TextWrapping.Wrap });
        Field("已有配置", _saved); Field("采集方式", _kind); Field("配置名称", _name);
        Field("设备型号", _model); Field("设备序列号（可选）", _serial);
        Field("镜头", _lens, _cameraFields); Field("光圈", _aperture, _cameraFields);
        Field("ISO", _iso, _cameraFields); Field("曝光时间", _exposure, _cameraFields);
        Field("倍率／片架／位置", _geometry, _cameraFields);
        Field("扫描软件", _scanSoftware, _scannerFields); Field("软件版本", _scanVersion, _scannerFields);
        Field("校准流程／产物来源", _scanWorkflow, _scannerFields);
        _scannerFields.Children.Add(DialogLayout.Actions(Action("导入标准扫描校准文件…", ImportScannerStandardAsset)));
        Field("扫描模式", _scanMode, _scannerFields); Field("扫描分辨率", _resolution, _scannerFields);
        Field("曝光／模拟增益", _scanExposure, _scannerFields); Field("驱动扣黑", _scanBlack, _scannerFields);
        Field("驱动空间／白校正", _scanSpatial, _scannerFields); Field("导出位深／曲线／颜色设置", _scanExport, _scannerFields);
        _body.Children.Add(_cameraFields); _body.Children.Add(_scannerFields);
        Field("物理测量目标", _measurement); Field("输入数据状态", _inputState);
        Field("参考量定义与来源（可选）", _definition);
        Field("光源与工作模式（可选）", _light);
        Field("解码／扫描软件与设置（可选）", _decode);
        Field("输入已执行的校正（可选）", _applied);
        _body.Children.Add(_measurementNote);
        Field("采集条件", _conditions); Field("色靶批次／序列号（可选）", _target); Field("备注（可选）", _notes);
        _body.Children.Add(_guide);
        _body.Children.Add(DialogLayout.Actions(
            Resource("SilverFast 参考数据", "https://www.silverfast.com/it8calibration/en.html"),
            Resource("Wolf Faust 色靶与数据", "https://www.colorreference.de/targets/index.html"),
            Resource("VueScan 校准说明", "https://www.hamrick.com/vuescan/html/vuesc17.htm")));
        _body.Children.Add(DialogLayout.Actions(Resource("透射阶梯与校准参考", "https://www.stouffer.net/TransPage.htm")));
        Field("参考类型", _role); _body.Children.Add(_files);
        var add = Action("添加参考…", AddReference);
        var remove = Action("移除选中参考", () => { if (_files.SelectedIndex is int i && i >= 0) { _references.RemoveAt(i); RefreshFiles(); } return Task.CompletedTask; });
        _body.Children.Add(DialogLayout.Actions(add, remove));
        _body.Children.Add(DialogLayout.Actions(Action("测量相机样本…", MeasureCameraSample)));
        _scannerFields.Children.Add(new Expander { Header = Loc.T("高级：自定义实测密度响应"), Content =
            DialogLayout.Actions(Action("导出扫描参考模板…", ExportScannerTemplate), Action("拟合扫描仪响应…", FitScannerResponse)) });
        _body.Children.Add(_status);
        var close = new Button { Content = Loc.T("关闭"), IsCancel = true }; close.Click += (_, _) => Close();
        _actions = DialogLayout.Actions(Action("新建", New), Action("导入配置…", Import), Action("导出配置…", Export),
            Action("核验参考", Check), Action("保存配置", Save), close);
        Content = DialogLayout.Compose(_body, _actions);
        _kind.SelectionChanged += (_, _) => UpdateGuide();
        _measurement.SelectionChanged += (_, _) => UpdateMeasurementNote();
        _inputState.SelectionChanged += (_, _) => UpdateMeasurementNote();
        _saved.SelectionChanged += (_, _) => { if (_saved.SelectedIndex >= 0) Load(_profiles[_saved.SelectedIndex]); };
        Opened += (_, _) => { try { Reload(); } catch (Exception ex) { _status.Text = ex.Message; } };
        UpdateGuide();
        UpdateMeasurementNote();
    }

    private void Field(string label, Control control, StackPanel? section = null)
    {
        var parent = section ?? _body;
        parent.Children.Add(new TextBlock { Text = Loc.T(label) });
        control.HorizontalAlignment = HorizontalAlignment.Stretch; parent.Children.Add(control);
    }

    private Button Action(string label, Func<Task> work)
    {
        var button = new Button { Content = Loc.T(label) };
        button.Click += async (_, _) =>
        {
            _body.IsEnabled = false; _actions.IsEnabled = false;
            try { await work(); } catch (Exception ex) { _status.Text = ex.Message; }
            finally { _body.IsEnabled = true; _actions.IsEnabled = true; }
        };
        return button;
    }

    private DeviceCalibrationProfile Snapshot() => new()
    {
        Id = _id, Name = _name.Text?.Trim() ?? "", DeviceModel = _model.Text?.Trim() ?? "",
        Kind = (CalibrationDeviceKind)_kind.SelectedIndex, DeviceSerial = _serial.Text ?? "",
        CaptureConditions = _conditions.Text ?? "", TargetIdentifier = _target.Text ?? "",
        MeasurementTarget = (CaptureMeasurementTarget)_measurement.SelectedIndex,
        InputState = (CaptureSignalState)_inputState.SelectedIndex,
        MeasurementDefinition = _definition.Text ?? "", LightSource = _light.Text ?? "",
        DecodeRecipe = _decode.Text ?? "", AlreadyAppliedCorrections = _applied.Text ?? "",
        Camera = new() { Lens = _lens.Text ?? "", Aperture = _aperture.Text ?? "", Iso = _iso.Text ?? "",
            Exposure = _exposure.Text ?? "", Geometry = _geometry.Text ?? "" },
        Scanner = new() { Software = _scanSoftware.Text ?? "", SoftwareVersion = _scanVersion.Text ?? "",
            Mode = (ScannerAcquisitionMode)_scanMode.SelectedIndex, Resolution = _resolution.Text ?? "",
            ExposureAndGain = _scanExposure.Text ?? "", BlackCorrection = (UpstreamCorrectionState)_scanBlack.SelectedIndex,
            SpatialCorrection = (UpstreamCorrectionState)_scanSpatial.SelectedIndex, ExportSettings = _scanExport.Text ?? "",
            Workflow = (ScannerCalibrationWorkflow)_scanWorkflow.SelectedIndex },
        Notes = _notes.Text ?? "", References = _references.ToArray(),
    };

    private void Load(DeviceCalibrationProfile p)
    {
        _id = p.Id; _name.Text = p.Name; _model.Text = p.DeviceModel; _serial.Text = p.DeviceSerial;
        _kind.SelectedIndex = (int)p.Kind; _conditions.Text = p.CaptureConditions;
        _target.Text = p.TargetIdentifier; _notes.Text = p.Notes;
        _measurement.SelectedIndex = (int)p.MeasurementTarget; _inputState.SelectedIndex = (int)p.InputState;
        _definition.Text = p.MeasurementDefinition; _light.Text = p.LightSource;
        _decode.Text = p.DecodeRecipe; _applied.Text = p.AlreadyAppliedCorrections;
        _lens.Text = p.Camera.Lens; _aperture.Text = p.Camera.Aperture; _iso.Text = p.Camera.Iso;
        _exposure.Text = p.Camera.Exposure; _geometry.Text = p.Camera.Geometry;
        _scanSoftware.Text = p.Scanner.Software; _scanVersion.Text = p.Scanner.SoftwareVersion;
        _scanMode.SelectedIndex = (int)p.Scanner.Mode; _resolution.Text = p.Scanner.Resolution;
        _scanExposure.Text = p.Scanner.ExposureAndGain; _scanBlack.SelectedIndex = (int)p.Scanner.BlackCorrection;
        _scanSpatial.SelectedIndex = (int)p.Scanner.SpatialCorrection; _scanExport.Text = p.Scanner.ExportSettings;
        _scanWorkflow.SelectedIndex = (int)p.Scanner.Workflow;
        _references.Clear(); _references.AddRange(p.References); RefreshFiles();
        _status.Text = Loc.T("参考配置已载入，尚未进行设备校准。编辑后请保存。");
    }
    private void Reload()
    {
        _profiles = _store.LoadAll(); _saved.ItemsSource = _profiles.Select(p => p.Name).ToArray();
        if (_store.LoadErrors.Length > 0) _status.Text = Loc.T("以下配置无法载入：") + "\n" + string.Join("\n", _store.LoadErrors);
    }
    private Button Resource(string label, string url) => Action(label, () =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    });
    private void RefreshFiles() => _files.ItemsSource = _references.Select(r => $"{((string[])_role.ItemsSource!)[(int)r.Role]} · {Path.GetFileName(r.Path)}").ToArray();
    private void UpdateGuide()
    {
        bool camera = _kind.SelectedIndex == 0;
        _cameraFields.IsVisible = camera; _scannerFields.IsVisible = !camera;
        _guide.Text = Loc.T(camera
            ? "相机流程：固定设备、镜头、灯板及曝光 → 采集暗场与无片场 → 试算同条件样本 → 独立参考验证。片基和胶片反转在后续流程处理；试算不应用到胶卷。"
            : "扫描仪流程：确认驱动处理与导出状态 → 执行厂家维护校准 → 扫描已测量的透射阶梯／目标 → 验证响应和范围。取不到暗场时不强求 dark/open；未知或已做颜色处理的数据不能直接作为物理透射率。当前仅登记材料。");
    }
    private void UpdateMeasurementNote()
    {
        _measurementNote.Text = Loc.T(_measurement.SelectedIndex == 0
            ? "dark/open 可建立适用条件下的设备通道相对透射率，仍包含光源和传感器光谱加权。"
            : "需明确参考通道或密度标准、测量来源及归一化尺度；选择目标不会自动获得该测量能力。")
            + "\n" + Loc.T(_inputState.SelectedIndex == 1
                ? "原始采样需明确黑电平所有权；不能重复扣黑。"
                : _inputState.SelectedIndex == 2
                    ? "线性设备通道可能已经扣黑或缩放，需按其处理记录建立匹配参考。"
                    : "处理未知或已经颜色转换的数据，需先核查来源；不能直接按原始 dark/open 公式处理。");
    }
    private Task New()
    {
        _saved.SelectedIndex = -1; Load(new DeviceCalibrationProfile()); return Task.CompletedTask;
    }
    private async Task AddReference()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("选择参考文件"), AllowMultiple = true });
        var role = (CalibrationReferenceRole)_role.SelectedIndex;
        var pending = new List<CalibrationReference>();
        foreach (var file in files)
        {
            string path = file.TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。"));
            pending.Add(await Task.Run(() => DeviceCalibrationStore.Register(role, path)));
        }
        _references.AddRange(pending); RefreshFiles();
        if (pending.Count > 0) _status.Text = Loc.T("参考已登记并计算摘要，请保存配置。尚未验证其测量内容。");
    }
    private Task Save()
    {
        var p = Snapshot(); _store.Save(p); Reload();
        _saved.SelectedIndex = Array.FindIndex(_profiles, saved => saved.Id == p.Id);
        _status.Text = Loc.T("参考配置已保存，尚未执行设备校正。")
            + (_store.LoadErrors.Length == 0 ? "" : "\n" + string.Join("\n", _store.LoadErrors)); return Task.CompletedTask;
    }
    private async Task Check()
    {
        var p = Snapshot(); p.Validate();
        var issues = await Task.Run(() => DeviceCalibrationStore.CheckReferences(p));
        _status.Text = p.References.Length == 0 ? Loc.T("尚未添加参考文件。")
            : issues.Length == 0 ? Loc.T("参考文件存在且摘要一致；这不是色彩或设备适用性验证。")
            : Loc.T("以下参考缺失、已改变或无法读取：") + "\n" + string.Join("\n", issues);
    }
    private async Task MeasureCameraSample()
    {
        var p = Snapshot(); p.Validate();
        if (p.Kind != CalibrationDeviceKind.CameraCopy || p.InputState != CaptureSignalState.RawSensorSamples
            || p.MeasurementTarget != CaptureMeasurementTarget.DeviceRelativeTransmission)
            throw new InvalidOperationException(Loc.T("本次测量仅支持相机原始 Bayer 采样与设备通道相对透射率目标。"));
        var darks = p.References.Where(r => r.Role == CalibrationReferenceRole.Dark).ToArray();
        var opens = p.References.Where(r => r.Role == CalibrationReferenceRole.OpenGate).ToArray();
        if (darks.Length != 1 || opens.Length != 1)
            throw new InvalidOperationException(Loc.T("请登记一张暗场和一张无片场；多帧合成尚未支持。"));
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("选择同条件相机 RAW 样本"), AllowMultiple = false });
        if (files.Count == 0) return;
        string path = files[0].TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。"));
        _status.Text = Loc.T("正在读取原始采样并测量透射率…");
        var summary = await Task.Run(() =>
        {
            var pair = p with { References = [darks[0], opens[0]] };
            if (DeviceCalibrationStore.CheckReferences(pair).Length > 0)
                throw new IOException(Loc.T("暗场或无片场已改变，请重新登记参考。"));
            var sample = CameraCaptureCalibration.ReadRaw(path, p.Id);
            // Budget covers retained CFA arrays, transmission/flags and transient native unpack buffers.
            if (PhysicalMemory.TryGetAvailableBytes(out long free) && free < sample.Samples.LongLength * 16 + (256L << 20))
                throw new InvalidOperationException(Loc.T("当前可用内存不足以测量整幅原始采样。"));
            var dark = CameraCaptureCalibration.ReadRaw(darks[0].Path, p.Id);
            var open = CameraCaptureCalibration.ReadRaw(opens[0].Path, p.Id);
            var result = CameraCaptureCalibration.Measure(sample, dark, open);
            if (DeviceCalibrationStore.CheckReferences(pair).Length > 0)
                throw new IOException(Loc.T("暗场或无片场已改变，请重新登记参考。"));
            return (result.Metadata.Width, result.Metadata.Height, result.UsableSamples,
                Total: result.Transmission.LongLength,
                Above: result.Issues.LongCount(i => i.HasFlag(CaptureSampleIssue.AboveOpenReference)));
        });
        _status.Text = Loc.F($"测量完成：{summary.Width}×{summary.Height} 个 CFA 采样，可计算 {summary.UsableSamples}/{summary.Total}，高于无片参考 {summary.Above}。未应用到胶卷，也未改变原片。");
    }
    private async Task Import()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("导入设备参考配置"), AllowMultiple = false });
        if (files.Count == 0) return;
        var p = DeviceCalibrationStore.Read(files[0].TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。")));
        Load(p with { Id = Guid.NewGuid().ToString("N") });
        _saved.SelectedIndex = -1;
        _status.Text = Loc.T("配置已导入为新草稿，请核验外部参考并保存。");
    }
    private async Task Export()
    {
        var p = Snapshot(); p.Validate();
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("导出配置（不含参考文件）"), SuggestedFileName = "device-calibration.json", DefaultExtension = "json" });
        if (file is null) return;
        DeviceCalibrationStore.Export(p, file.TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。")));
        _status.Text = Loc.T("已导出配置，包含参考的本机完整路径；分享时需另行携带参考文件。");
    }
}
