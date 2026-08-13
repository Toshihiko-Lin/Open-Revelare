using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private static readonly HashSet<string> RawTiffExt = new(StringComparer.OrdinalIgnoreCase)
    { ".arw", ".nef", ".cr2", ".cr3", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw", ".tif", ".tiff" };

    /// <summary>Scanner output — the inputs that may hold several negatives in one file and so
    /// go through the split pre-pass. Camera RAW is one frame per file and skips it.</summary>
    private static readonly HashSet<string> ScanExt = new(StringComparer.OrdinalIgnoreCase)
    { ".tif", ".tiff" };

    /// <summary>True when <paramref name="path"/> is a scanner TIFF rather than a camera RAW.</summary>
    public static bool IsScan(string path) => ScanExt.Contains(System.IO.Path.GetExtension(path));

    internal static readonly string[] FormatPresets =
    {
        "135", "135 Half-frame", "APS",
        "120 (645)", "120 (6x6)", "120 (6x7)", "120 (6x9)", "120 (6x12)", "120 (6x17)",
        "4x5", "5x7", "8x10",
    };

    public ObservableCollection<string> Files { get; } = new();
    public ImportConfig? Result { get; private set; }

    public ImportDialog()
    {
        InitializeComponent();
        FileList.ItemsSource = Files;
        // Opens on whatever the last import chose (OnAcceptClick writes them back).
        AutoInvertChk.IsChecked = Settings.Current.AutoInvertOnImport;
        SplitChk.IsChecked = Settings.Current.SplitStripsOnImport;
        Files.CollectionChanged += (_, _) =>
        {
            CountLbl.Text = Loc.F($"{Files.Count} 张");
            OkBtn.IsEnabled = Files.Count > 0;
        };
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
        foreach (var f in picked)
            if (f.TryGetLocalPath() is { } p && !Files.Contains(p)) Files.Add(p);
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("添加文件夹（扫描 RAW/TIFF）") });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        foreach (string p in Directory.EnumerateFiles(dir).Where(f => RawTiffExt.Contains(Path.GetExtension(f))).OrderBy(f => f))
            if (!Files.Contains(p)) Files.Add(p);
    }

    private void OnRemoveSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var s in FileList.SelectedItems?.Cast<string>().ToList() ?? new()) Files.Remove(s);
    }

    private void OnClear(object? sender, RoutedEventArgs e) => Files.Clear();

    private void OnSourceChanged(object? sender, RoutedEventArgs e) => CalRow.IsVisible = SrcA.IsChecked == true;

    private async void OnPickCal(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("选择 R/G/B 校正图目录") });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        CalEdit.Text = dir;
        CalDetect.Text = Loc.T("识别中 …");
        try
        {
            var r = await Task.Run(() => DecoupleCalibration.AutoIdentifyRgbFiles(dir));
            CalDetect.Text = Loc.F($"识别到  R: {Path.GetFileName(r.R)}   G: {Path.GetFileName(r.G)}   B: {Path.GetFileName(r.B)}");
        }
        catch (Exception ex) { CalDetect.Text = Loc.T("识别失败：") + ex.Message; }
    }

    private void OnLccToggled(object? sender, RoutedEventArgs e) => LccRow.IsVisible = LccChk.IsChecked == true;

    private async void OnPickLcc(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("选择 LCC 平场参考图"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Loc.T("平场图 (RAW / TIFF)")) { Patterns = ImageIo.OpenPatterns } },
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) LccEdit.Text = p;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
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
        };
        // The dialog's choices become the new defaults, so the next import opens on them.
        if (Settings.Current.AutoInvertOnImport != cfg.AutoInvert
            || Settings.Current.SplitStripsOnImport != cfg.SplitStrips)
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
