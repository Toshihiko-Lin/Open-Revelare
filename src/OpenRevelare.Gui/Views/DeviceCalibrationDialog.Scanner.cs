using System.Text.Json;
using Avalonia.Platform.Storage;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed partial class DeviceCalibrationDialog
{
    private async Task ImportScannerStandardAsset()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("选择 ICC、IT8/Q60、CGATS 或 Argyll TI3 文件"), AllowMultiple = true });
        var pending = new List<CalibrationReference>(); var summaries = new List<string>();
        foreach (var file in files)
        {
            string path = file.TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。"));
            var asset = await Task.Run(() => ScannerStandardAssets.Read(path));
            var reference = await Task.Run(() => DeviceCalibrationStore.Register(asset.Role, path));
            if (!reference.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.T("参考文件已改变，请重新登记。"));
            pending.Add(reference);
            summaries.Add(Path.GetFileName(path) + " · " + asset.Format + "\n"
                + Loc.F($"数据行：{asset.Rows.Length}；字段：{string.Join(", ", asset.Fields)}") + "\n"
                + string.Join("\n", asset.Metadata.Where(kv => kv.Key is "DESCRIPTOR" or "ORIGINATOR" or "DEVICE_CLASS" or "COLOR_REP" or "PCS" or "SERIAL" or "MATERIAL" or "PROD_DATE")
                    .Select(kv => kv.Key + ": " + kv.Value)));
        }
        _references.AddRange(pending); RefreshFiles();
        if (pending.Count == 0) return;
        _status.Text = Loc.T("标准文件已识别并登记，请保存配置。ICC/XYZ/Lab 不会被当作物理密度或自动应用到胶卷。");
        await new InfoDialog(Loc.T("扫描校准文件识别结果"), string.Join("\n\n", summaries) + "\n\n"
            + Loc.T("已检查文件结构及内容类型；这不是本机硬件适用性或测量准确性认证。参考数据仍需匹配实体靶的编号。ICC 若已在扫描输出中使用，不可再次重复应用。")).ShowDialog(this);
    }

    private static readonly JsonSerializerOptions ScannerJson = new() { WriteIndented = true };
    private async Task ExportScannerTemplate()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("导出扫描参考模板"), SuggestedFileName = "scanner-reference.json", DefaultExtension = "json" });
        if (file is null) return;
        string path = file.TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。"));
        if (_references.Any(r => Path.GetFullPath(r.Path).Equals(Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new IOException(Loc.T("不能覆盖已登记的参考文件。"));
        var template = new ScannerReferenceSet { TargetIdentifier = _target.Text ?? "",
            Patches = Enumerable.Range(0, 5).Select(i => new ScannerReferencePatch(0, 0, 0, 0, [0, 0, 0], i >= 3)).ToArray() };
        ExportFile.Write(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(template, ScannerJson)));
        _status.Text = Loc.T("模板已导出。填写实测密度、像素区域、参考定义和误差限值；完成后将 IsTemplate 改为 false，再登记为色靶参考数据。");
    }

    private async Task FitScannerResponse()
    {
        var p = Snapshot(); p.Validate();
        if (p.Kind != CalibrationDeviceKind.Scanner || p.Scanner.Mode != ScannerAcquisitionMode.Transmission
            || p.InputState != CaptureSignalState.LinearDeviceChannels || p.MeasurementTarget != CaptureMeasurementTarget.StandardDensity)
            throw new InvalidOperationException(Loc.T("扫描拟合仅支持透射扫描、线性设备通道和指定参考密度目标。"));
        if (string.IsNullOrWhiteSpace(p.Scanner.Software) || string.IsNullOrWhiteSpace(p.Scanner.ExportSettings)
            || p.Scanner.BlackCorrection == UpstreamCorrectionState.Unknown || p.Scanner.SpatialCorrection == UpstreamCorrectionState.Unknown)
            throw new InvalidOperationException(Loc.T("请先记录扫描软件、导出设置以及驱动扣黑和白校准状态。"));
        CalibrationReference One(CalibrationReferenceRole role)
        {
            var matches = p.References.Where(r => r.Role == role).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(Loc.T("请分别登记一张色靶图像、一张独立验证扫描和一份色靶参考数据。"));
            return matches[0];
        }
        var training = One(CalibrationReferenceRole.TargetImage);
        var validation = One(CalibrationReferenceRole.ValidationImage);
        var data = One(CalibrationReferenceRole.TargetData);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(training.Path).Equals(Path.GetFullPath(validation.Path), comparison))
            throw new InvalidOperationException(Loc.T("验证图必须是另一次扫描的文件，不能重复选择训练图。"));
        _status.Text = Loc.T("正在读取扫描参考并进行独立验证…");
        var result = await Task.Run(() =>
        {
            var required = p with { References = [training, validation, data] };
            if (DeviceCalibrationStore.CheckReferences(required).Length > 0) throw new IOException(Loc.T("参考文件已改变，请重新登记。"));
            if (new FileInfo(data.Path).Length > 1024 * 1024) throw new InvalidDataException("Reference data exceeds 1 MiB.");
            var reference = JsonSerializer.Deserialize<ScannerReferenceSet>(File.ReadAllText(data.Path)) ?? throw new InvalidDataException("Empty scanner reference.");
            if (string.IsNullOrWhiteSpace(p.TargetIdentifier) || reference.TargetIdentifier != p.TargetIdentifier)
                throw new InvalidOperationException(Loc.T("参考数据的色靶编号须与配置中记录的编号一致。"));
            var model = ScannerCaptureCalibration.Fit(TiffIO.LoadScannerMeasurementTiff(training.Path),
                TiffIO.LoadScannerMeasurementTiff(validation.Path), reference);
            if (DeviceCalibrationStore.CheckReferences(required).Length > 0) throw new IOException(Loc.T("参考文件已改变，请重新登记。"));
            return new { Schema = "openrevelare-scanner-density-fit-v1", CreatedUtc = DateTime.UtcNow,
                Profile = p, Model = model, Limitation = "Per-channel response over the trained density range only; not colour crosstalk, standard certification, or roll application." };
        });
        var destination = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("保存扫描响应模型与验证报告"), SuggestedFileName = "scanner-density-model.json", DefaultExtension = "json" });
        if (destination is null) { _status.Text = Loc.T("拟合与验证完成，报告未保存，未应用到胶卷。"); return; }
        string output = destination.TryGetLocalPath() ?? throw new IOException(Loc.T("请选择本地文件。"));
        if (p.References.Any(r => Path.GetFullPath(r.Path).Equals(Path.GetFullPath(output), comparison)))
            throw new IOException(Loc.T("不能覆盖已登记的参考文件。"));
        ExportFile.Write(output, temp => File.WriteAllText(temp, JsonSerializer.Serialize(result, ScannerJson)));
        _status.Text = Loc.T("扫描响应模型与验证报告已保存；仅在所记录参考域和范围内有效，尚未应用到胶卷。");
    }
}
