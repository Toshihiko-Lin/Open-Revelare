using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// New-project / import dialog — port of Python gui/import_dialog.py. Collects the negative
/// files, the copy-stand light path (Path A 窄谱 RGB / Path B 宽谱白光, with Path A needing a
/// R/G/B calibration folder for the decouple matrix), and an optional LCC flat field.
/// </summary>
public partial class ImportDialog : Window
{
    /// <summary>Scanner output — the inputs that may hold several negatives in one file and so
    /// go through the split pre-pass. Camera RAW is one frame per file and skips it.</summary>
    private static readonly HashSet<string> ScanExt = new(StringComparer.OrdinalIgnoreCase)
    { ".tif", ".tiff" };

    /// <summary>
    /// True when <paramref name="path"/> is scanner output rather than a camera RAW — i.e. a file
    /// that may hold a whole strip and so earns the split pre-pass.
    ///
    /// Extension settles it for TIFF, but <c>.fff</c> is written by BOTH a Flextight scanner and
    /// an Imacon digital back, and only one of those can hold several frames. That is the same
    /// split <see cref="RawDecode.IsRawExtension"/> already resolves by reading the file's
    /// photometric tag, so the answer is taken from there rather than decided a second time:
    /// whatever is NOT routed to LibRaw is, by definition, the scanner raster.
    /// </summary>
    public static bool IsScan(string path)
    {
        if (ScanExt.Contains(System.IO.Path.GetExtension(path))) return true;
        // A RAW extension that content-sniffing rejected as RAW is a scanner export (today only
        // .fff reaches this). Anything the decoder still claims is one frame per file.
        return RawDecode.HasRawExtension(path) && !RawDecode.IsRawExtension(path);
    }

    internal static readonly string[] FormatPresets =
    {
        "135", "135 Half-frame", "APS",
        "120 (645)", "120 (6x6)", "120 (6x7)", "120 (6x9)", "120 (6x12)", "120 (6x17)",
        "4x5", "5x7", "8x10",
    };

    public ObservableCollection<string> Files { get; } = new();
    public ImportConfig? Result { get; private set; }
    public bool AddToCurrent => CurrentRollChoice.IsChecked == true;
    private bool _calValid;
    private bool _calChecking;

    public ImportDialog() : this(Array.Empty<string>(), false) { }

    public ImportDialog(IEnumerable<string> initialPaths, bool canAddToCurrent)
    {
        InitializeComponent();
        FileList.ItemsSource = Files;
        // Opens on whatever the last import chose (OnAcceptClick writes them back).
        AutoInvertChk.IsChecked = Settings.Current.AutoInvertOnImport;
        SplitChk.IsChecked = Settings.Current.SplitStripsOnImport;
        AdvancedImportSettings.IsExpanded = SplitChk.IsChecked == true;
        Files.CollectionChanged += (_, _) =>
        {
            CountLbl.Text = Loc.F($"{Files.Count} 张");
            UpdateReadyUi();
        };
        DestinationRow.IsVisible = canAddToCurrent;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            FileList.Opacity = e.DragEffects == DragDropEffects.Copy ? 0.65 : 1;
            e.Handled = true;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => FileList.Opacity = 1);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            FileList.Opacity = 1;
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? Array.Empty<string>();
            await AddPathsAsync(paths);
        });
        foreach (string path in initialPaths) Files.Add(path);
    }

    private void OnDestinationChanged(object? sender, RoutedEventArgs e)
    {
        if (AdvancedImportSettings is null || AutoImportOptions is null) return;
        AdvancedImportSettings.IsVisible = AutoImportOptions.IsVisible = !AddToCurrent;
        UpdateReadyUi();
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var selection = await Task.Run(() => ImportFiles.Collect(paths));
        var existing = new HashSet<string>(Files, OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in selection.Images)
            if (existing.Add(path)) Files.Add(path);
        if (selection.Rejected.Length + selection.Projects.Length > 0)
            await new InfoDialog(Loc.T("部分文件未加入"),
                Loc.T("请选择 RAW 或 TIFF；工程请在主窗口打开。") + "\n\n" +
                string.Join("\n", selection.Rejected.Concat(selection.Projects).Select(System.IO.Path.GetFileName)))
                .ShowDialog(this);
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("添加底片文件"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("负片 (RAW / TIFF)")) { Patterns = ImageIo.OpenPatterns },
                new FilePickerFileType(Loc.T("所有文件")) { Patterns = new[] { "*" } },
            },
        });
        await AddPathsAsync(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("添加文件夹（扫描 RAW/TIFF）") });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        await AddPathsAsync(new[] { dir });
    }

    private void OnRemoveSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var s in FileList.SelectedItems?.Cast<string>().ToList() ?? new()) Files.Remove(s);
    }

    private void OnClear(object? sender, RoutedEventArgs e) => Files.Clear();

    /// <summary>
    /// A scanner roll no longer answers for its own colour encoding here. TiffInputDetector reads
    /// what the file declares, and the preview shows a correctable notice when it had to fall back
    /// on convention — a decision the user can actually judge, because by then the picture is on
    /// screen. Asking upfront taxed every import for a minority of files.
    /// </summary>
    private void UpdateReadyUi()
    {
        if (OkBtn is null || ReadyHint is null) return;
        string issue = AddToCurrent ? "" : MissingReference(SrcA.IsChecked == true, _calValid, _calChecking,
            LccChk.IsChecked == true, LccEdit.Text);
        OkBtn.IsEnabled = Files.Count > 0 && issue.Length == 0;
        ReadyHint.Text = issue;
    }

    internal static string MissingReference(bool pathA, bool calValid, bool calChecking,
        bool lccEnabled, string? lccPath) => pathA && !calValid
            ? calChecking ? Loc.T("正在识别 R/G/B 校正图…") : Loc.T("请先选择有效的 R/G/B 校正图目录。")
            : lccEnabled && !File.Exists(lccPath)
                ? Loc.T("请先选择 LCC 平场参考图。") : "";

    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (CalRow is null) return;
        CalRow.IsVisible = SrcA.IsChecked == true;
        if (SrcA.IsChecked == true) AdvancedImportSettings.IsExpanded = true;
        UpdateReadyUi();
    }

    private async void OnPickCal(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("选择 R/G/B 校正图目录") });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        CalEdit.Text = dir;
        CalDetect.Text = Loc.T("识别中 …");
        _calValid = false;
        _calChecking = true;
        UpdateReadyUi();
        try
        {
            var r = await Task.Run(() => DecoupleCalibration.AutoIdentifyRgbFiles(dir));
            _calValid = true;
            CalDetect.Text = Loc.F($"识别到  R: {Path.GetFileName(r.R)}   G: {Path.GetFileName(r.G)}   B: {Path.GetFileName(r.B)}");
        }
        catch (Exception ex) { CalDetect.Text = Loc.T("识别失败：") + ex.Message; }
        finally { _calChecking = false; UpdateReadyUi(); }
    }

    private void OnLccToggled(object? sender, RoutedEventArgs e)
    {
        if (LccRow is null) return;
        LccRow.IsVisible = LccChk.IsChecked == true;
        if (LccChk.IsChecked == true) AdvancedImportSettings.IsExpanded = true;
        UpdateReadyUi();
    }

    private async void OnPickLcc(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("选择 LCC 平场参考图"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("平场图 (RAW / TIFF)")) { Patterns = ImageIo.OpenPatterns },
                new FilePickerFileType(Loc.T("所有文件")) { Patterns = new[] { "*" } },
            },
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) LccEdit.Text = p;
        UpdateReadyUi();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        UpdateReadyUi();
        if (!OkBtn.IsEnabled) return;
        // Scans and camera RAW take different routes through the pipeline — a scan may hold a
        // whole strip and gets the split pre-pass, a RAW is one frame and does not — and a roll
        // mixing them would be half-processed either way. Rejecting the mix here is clearer than
        // silently treating one kind as the other.
        int scans = Files.Count(IsScan);
        if (scans > 0 && scans < Files.Count)
        {
            await new InfoDialog(Loc.T("不能混合扫描件和 RAW"),
                    Loc.T("这一批里既有扫描 TIFF 又有相机 RAW，两者的处理管线不同（扫描件可能一个文件装着整条底片，需要先分割）。请分成两卷分别导入。"))
                .ShowDialog(this);
            return;
        }

        var cfg = new ImportConfig
        {
            PathA = SrcA.IsChecked == true,
            CalDir = SrcA.IsChecked == true ? CalEdit.Text : null,
            LccEnabled = LccChk.IsChecked == true,
            LccPath = LccChk.IsChecked == true ? LccEdit.Text : null,
            AutoInvert = AutoInvertChk.IsChecked == true,
            SplitStrips = SplitChk.IsChecked == true,
            // Left at Unspecified on purpose: that is now "detect", not "unanswered".
        };
        // The dialog's choices become the new defaults, so the next import opens on them.
        if (!AddToCurrent && (Settings.Current.AutoInvertOnImport != cfg.AutoInvert
            || Settings.Current.SplitStripsOnImport != cfg.SplitStrips))
        {
            Settings.Current.AutoInvertOnImport = cfg.AutoInvert;
            Settings.Current.SplitStripsOnImport = cfg.SplitStrips;
            Settings.Save();
        }
        cfg.Paths.AddRange(Files);
        Result = cfg;
        Close(true);
    }
}
