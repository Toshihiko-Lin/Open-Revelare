using Avalonia.Controls;
using Avalonia.Threading;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

public partial class ImportDialog
{
    private readonly ImportLensPreview _lensPreview = new();
    private Task<ImportLensPreviewResult?>? _shownLensTask;
    private bool _lensRefreshQueued, _lensClosed, _lensReading, _lensFailed;
    private ImportLensPreviewResult? _lensResult;
    private string? _lensFile;
    private LensfunCalibration? _importLensfun;
    private LensfunProfile? _importLensfunProfile;

    private void SetUpLensPreview()
    {
        // SizeToContent grows after asynchronous identification / expanding the profile search.
        // The normal WindowOpened clamp only ran while this panel was still hidden.
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_lensClosed && IsVisible) WindowLayout.FitToScreen(this);
        }, DispatcherPriority.Loaded);
        // Coalesce a folder's individual CollectionChanged events into one first-file read.
        Files.CollectionChanged += (_, _) => QueueLensPreview();
        Loc.Changed += UpdateLensPreviewText;
        Closed += (_, _) =>
        {
            _lensClosed = true;
            Loc.Changed -= UpdateLensPreviewText;
        };
        QueueLensPreview();
    }

    private void QueueLensPreview()
    {
        if (_lensRefreshQueued || _lensClosed) return;
        _lensRefreshQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _lensRefreshQueued = false;
            if (_lensClosed) return;
            string? path = Files.FirstOrDefault();
            var task = _lensPreview.Get(path);
            if (ReferenceEquals(task, _shownLensTask)) return;
            _shownLensTask = task;
            _lensFile = path;
            _lensReading = path is not null;
            _lensFailed = false;
            _lensResult = null;
            _importLensfun = null;
            _importLensfunProfile = null;
            LensfunEnableChk.IsChecked = false;
            LensfunEnableChk.IsEnabled = false;
            LensProfileSearch.Text = "";
            LensProfilePicker.ItemsSource = null;
            LensManualSettings.IsExpanded = false;
            LensPreviewPanel.IsVisible = path is not null;
            UpdateLensPreviewText();
            try
            {
                var result = await task;
                // A removed/replaced first file or a closed dialog must never receive old results.
                if (!IsCurrentLensPreview(task, path)) return;
                _lensResult = result;
                LensPreviewPanel.IsVisible = result is not null;
                FilterLensProfiles();
                if (result?.Identification.Profile is { } profile)
                    LensProfilePicker.SelectedItem = profile;
            }
            catch (Exception)
            {
                if (!IsCurrentLensPreview(task, path)) return;
                _lensFailed = true;
            }
            finally
            {
                if (IsCurrentLensPreview(task, path))
                {
                    _lensReading = false;
                    UpdateLensPreviewText();
                }
                else if (!_lensClosed && ReferenceEquals(task, _shownLensTask))
                    _shownLensTask = null;
            }
        });
    }

    private bool IsCurrentLensPreview(Task<ImportLensPreviewResult?> task, string? path)
        => !_lensClosed && ReferenceEquals(task, _shownLensTask) && path == Files.FirstOrDefault();

    private void UpdateLensPreviewText()
    {
        LensPreviewSource.Text = _lensFile is null ? "" : Loc.F($"首张参考：{System.IO.Path.GetFileName(_lensFile)}");
        LensCaptureSummary.Text = "";
        LensProfileDetails.Text = "";
        LensCorrectionStatus.Text = "";
        LensManualSettings.IsVisible = _lensResult is not null;
        if (_lensReading) { LensMatchStatus.Text = Loc.T("正在读取首张拍摄信息并匹配 Lensfun…"); return; }
        if (_lensFailed) { LensMatchStatus.Text = Loc.T("首张拍摄信息读取失败，可继续导入。"); return; }
        if (_lensResult is not { } result) { LensMatchStatus.Text = ""; return; }
        var capture = result.Capture;
        LensCaptureSummary.Text = string.Join(" · ", new[]
        {
            capture.Camera.Length > 0 ? capture.Camera : Loc.T("相机未知"),
            capture.Lens.Length > 0 ? capture.Lens : Loc.T("镜头未知"),
            capture.FocalLength is { } focal ? $"{focal:0.##} mm" : Loc.T("焦距未知"),
            capture.Aperture is { } aperture ? $"f/{aperture:0.##}" : Loc.T("光圈未知"),
        });
        LensMatchStatus.Text = result.Identification.Status switch
        {
            LensfunMatchStatus.Matched => Loc.F($"Lensfun 匹配：{result.Identification.Profile}"),
            LensfunMatchStatus.MissingLens => Loc.T("首张未提供镜头型号，可手动查找配置或直接导入。"),
            LensfunMatchStatus.Ambiguous => Loc.F($"找到 {result.Identification.Candidates.Count} 个同名配置，请展开查看。"),
            _ => Loc.T("Lensfun 未找到匹配配置，可手动查找或直接导入。"),
        };
        UpdateLensProfileDetails();
    }

    private void OnLensProfileSearchChanged(object? sender, TextChangedEventArgs e) => FilterLensProfiles();

    private void FilterLensProfiles()
    {
        if (_lensResult is not { } result) return;
        string search = LensProfileSearch.Text?.Trim() ?? "";
        var profiles = search.Length == 0 && result.Identification.Candidates.Count > 0
            ? result.Identification.Candidates : result.Database.Profiles;
        LensProfilePicker.ItemsSource = profiles.Where(p => search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => p.ToString().Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private void OnLensProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LensProfilePicker.SelectedItem is not LensfunProfile profile || _lensResult is null) return;
        if (!ReferenceEquals(profile, _importLensfunProfile))
        {
            _importLensfunProfile = profile;
            _importLensfun = _lensResult.Database.CreateCalibration(profile, _lensResult.Capture);
            LensfunEnableChk.IsEnabled = _importLensfun is not null;
            LensfunEnableChk.IsChecked = _importLensfun is not null;
        }
        UpdateLensProfileDetails();
    }

    private void OnLensfunEnableChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => UpdateLensProfileDetails();

    private async Task<bool> FinishLensPreviewAsync()
    {
        string? first = Files.FirstOrDefault();
        if (first != _lensFile)
        {
            _importLensfun = null;
            _importLensfunProfile = null;
            LensfunEnableChk.IsChecked = false;
            LensfunEnableChk.IsEnabled = false;
        }
        try
        {
            var result = await _lensPreview.Get(first);
            if (_lensClosed || first != Files.FirstOrDefault()) return false;
            if (!ReferenceEquals(result, _lensResult))
            {
                _lensResult = result;
                FilterLensProfiles();
                if (result?.Identification.Profile is { } profile) LensProfilePicker.SelectedItem = profile;
            }
            if (_importLensfunProfile is null && result?.Identification.Profile is { } matched)
            {
                _lensResult = result;
                _importLensfunProfile = matched;
                _importLensfun = result.Database.CreateCalibration(matched, result.Capture);
                LensfunEnableChk.IsEnabled = _importLensfun is not null;
                LensfunEnableChk.IsChecked = _importLensfun is not null;
            }
        }
        catch (Exception) { /* Metadata failure must not prevent an uncorrected import. */ }
        return !_lensClosed && first == Files.FirstOrDefault();
    }

    private void UpdateLensProfileDetails()
    {
        if (LensProfileDetails is null) return;
        var profile = LensProfilePicker.SelectedItem as LensfunProfile ?? _lensResult?.Identification.Profile;
        LensProfileDetails.Text = profile is null ? "" : Loc.F($"畸变：{(profile.HasDistortion ? Loc.T("有数据") : Loc.T("无数据"))} · 暗角：{(profile.HasVignetting ? Loc.T("有数据") : Loc.T("无数据"))}");
        LensProfileDetails.IsVisible = profile is not null;
        if (LensCorrectionStatus is null) return;
        LensCorrectionStatus.Text = _importLensfun is null
            ? (profile is null ? "" : Loc.T("当前拍摄参数缺失或无可用校正，仍可直接导入。"))
            : LensfunEnableChk.IsChecked != true ? Loc.T("Lensfun 校正已关闭。")
            : Loc.F($"导入后应用：畸变{(_importLensfun.Distortion is null ? Loc.T("无可用参数") : _importLensfun.Distortion.Model)} · 暗角{(LccChk.IsChecked == true ? Loc.T("由 LCC 处理") : _importLensfun.Vignetting is null ? Loc.T("无可用参数") : Loc.T("Lensfun"))}");
    }
}
