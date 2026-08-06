using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Linq;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>偏好设置 — port of Python's 设置 → 偏好设置. Persists via <see cref="Settings"/>.
///
/// Only preferences with a working C# backend are exposed (RAW backend / FBDD / decode
/// concurrency / disk cache / theme). A setting that persists but drives nothing
/// is worse than a missing one — the user changes it, sees no effect, and stops trusting the
/// dialog — so 快速预览解码 was removed here when its last live call site disappeared (the roll
/// warm-up now shares one full-quality decode with the thumbnail pass).</summary>
public sealed class PreferencesDialog : Window
{
    private readonly ComboBox _backend = new();
    private readonly ComboBox _fbdd = new();
    private readonly ComboBox _theme = new();
    private readonly ComboBox _concurrency = new();
    private readonly TextBlock _dngNote = new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _concurrencyNote = new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _cacheOn = new() { Content = "启用 DNG 转换磁盘缓存（仅本次会话，退出自动删除）" };
    private readonly TextBox _cacheDir = new() { Watermark = "留空 = 和源文件同目录", MinWidth = 250 };
    private readonly ComboBox _cacheBudget = new();
    private readonly TextBlock _cacheNote = new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _sheetDir = new() { Watermark = @"留空 = %LOCALAPPDATA%\OpenRevelare\sheets", MinWidth = 250 };
    private readonly ComboBox _sheetBudget = new();
    private readonly TextBlock _sheetNote = new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>Index 0 is 自动; index N is a manual limit of N. Mirrors ImageIo's HardCap.</summary>
    private const int MaxConcurrency = 8;

    public PreferencesDialog()
    {
        Title = "偏好设置";
        Width = 460;
        Height = 640;   // grew when the roll-cover section landed; still scrolls on a short screen
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _backend.ItemsSource = new[] { "自动（Windows 有 DNG Converter 则用之）", "LibRaw", "Adobe DNG Converter" };
        _fbdd.ItemsSource = new[] { "关闭", "轻度", "完全" };
        _theme.ItemsSource = new[] { "深色", "浅色" };

        var concurrencyItems = new string[MaxConcurrency + 1];
        concurrencyItems[0] = "自动（按可用内存）";
        for (int i = 1; i <= MaxConcurrency; i++) concurrencyItems[i] = $"{i} 张同时解码";
        _concurrency.ItemsSource = concurrencyItems;

        Settings.Model s = Settings.Current;
        _backend.SelectedIndex = (int)s.DecodeBackend;
        _fbdd.SelectedIndex = (int)s.FbddMode;
        _theme.SelectedIndex = s.Theme == "light" ? 1 : 0;
        _concurrency.SelectedIndex = Math.Clamp(s.DecodeConcurrency, 0, MaxConcurrency);
        _dngNote.Text = RawDecode.IsDngConverterAvailable()
            ? "已检测到 Adobe DNG Converter。"
            : "未检测到 Adobe DNG Converter（选 DNG 后端将回退到 LibRaw）。";
        _concurrency.SelectionChanged += (_, _) => UpdateConcurrencyNote();
        UpdateConcurrencyNote();

        _cacheBudget.ItemsSource = CacheBudgets.Select(g => $"{g} GB（约 {g * 1024 / 349} 帧 60MP）").ToArray();
        int bi = Array.IndexOf(CacheBudgets, s.CacheBudgetGb);
        _cacheBudget.SelectedIndex = bi >= 0 ? bi : Array.IndexOf(CacheBudgets, 5);
        _cacheOn.IsChecked = s.CacheEnabled;
        _cacheDir.Text = s.CacheDirectory;
        _cacheOn.IsCheckedChanged += (_, _) => UpdateCacheNote();
        _cacheBudget.SelectionChanged += (_, _) => UpdateCacheNote();
        _cacheDir.TextChanged += (_, _) => UpdateCacheNote();
        UpdateCacheNote();

        _sheetBudget.ItemsSource = SheetBudgets.Select(g => $"{g} GB（约 {g * 1024 * 1024 / 300} 卷）").ToArray();
        int si = Array.IndexOf(SheetBudgets, s.SheetCacheBudgetGb);
        _sheetBudget.SelectedIndex = si >= 0 ? si : Array.IndexOf(SheetBudgets, 1);
        _sheetDir.Text = s.SheetCacheDirectory;
        _sheetBudget.SelectionChanged += (_, _) => UpdateSheetNote();
        _sheetDir.TextChanged += (_, _) => UpdateSheetNote();
        UpdateSheetNote();

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "偏好设置", FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(Row("RAW 解码后端", _backend));
        panel.Children.Add(_dngNote);
        panel.Children.Add(Row("FBDD 色度降噪（去马赛克前）", _fbdd));
        panel.Children.Add(Row("RAW 并发解码数", _concurrency));
        panel.Children.Add(_concurrencyNote);
        var browse = new Button { Content = "浏览…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "选择缓存目录", AllowMultiple = false });
            if (dirs.FirstOrDefault()?.TryGetLocalPath() is { } d) { _cacheDir.Text = d; UpdateCacheNote(); }
        };
        var clear = new Button { Content = "恢复默认", Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { _cacheDir.Text = ""; UpdateCacheNote(); };
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        dirRow.Children.Add(_cacheDir); dirRow.Children.Add(browse); dirRow.Children.Add(clear);

        panel.Children.Add(new TextBlock { Text = "磁盘缓存", FontWeight = FontWeight.SemiBold,
                                           Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(_cacheOn);
        panel.Children.Add(dirRow);
        panel.Children.Add(Row("缓存上限", _cacheBudget));
        panel.Children.Add(_cacheNote);

        var sheetBrowse = new Button { Content = "浏览…", Margin = new Thickness(6, 0, 0, 0) };
        sheetBrowse.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "选择印样存放目录", AllowMultiple = false });
            if (dirs.FirstOrDefault()?.TryGetLocalPath() is { } d) { _sheetDir.Text = d; UpdateSheetNote(); }
        };
        var sheetClear = new Button { Content = "恢复默认", Margin = new Thickness(6, 0, 0, 0) };
        sheetClear.Click += (_, _) => { _sheetDir.Text = ""; UpdateSheetNote(); };
        var sheetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        sheetRow.Children.Add(_sheetDir); sheetRow.Children.Add(sheetBrowse); sheetRow.Children.Add(sheetClear);

        panel.Children.Add(new TextBlock { Text = "卷目录缩略图（印样）", FontWeight = FontWeight.SemiBold,
                                           Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(sheetRow);
        panel.Children.Add(Row("存放上限", _sheetBudget));
        panel.Children.Add(_sheetNote);

        panel.Children.Add(Row("界面主题", _theme));

        var save = new Button { Content = "保存", Margin = new Thickness(0, 14, 6, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 14, 0, 0) };
        save.Click += (_, _) => { Persist(); Close(); };
        cancel.Click += (_, _) => Close();
        // Live theme preview as the user picks.
        _theme.SelectionChanged += (_, _) => App.ApplyTheme(_theme.SelectedIndex == 1 ? "light" : "dark");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel };
    }

    /// <summary>Show what the current choice actually means — for 自动 that is a live number,
    /// and without it the setting is a black box the user has no way to sanity-check.</summary>
    private void UpdateConcurrencyNote()
    {
        var (auto, free) = ImageIo.AutoConcurrencyInfo();
        string freeText = free is long f
            ? $"当前可用内存 {f / 1073741824.0:F1} GB"
            : "本平台无法读取可用内存，退回按总内存估算";

        _concurrencyNote.Text = _concurrency.SelectedIndex <= 0
            ? $"{freeText} → 自动使用 {auto} 张并发。每张解码中的 RAW 约占 1.2 GB，"
              + "导入过程中会随可用内存变化自动升降。"
            : $"手动固定 {_concurrency.SelectedIndex} 张（当前自动会选 {auto} 张）。"
              + $"{freeText}；每张解码中的 RAW 约占 1.2 GB，设高了会让机器换页变慢。";
    }

    private static readonly int[] CacheBudgets = { 2, 5, 10, 20, 50 };

    /// <summary>Show where the disk is going and how much of it — a cache the user cannot see
    /// the size or location of is exactly how a system drive fills up unnoticed.</summary>
    private void UpdateCacheNote()
    {
        if (_cacheOn.IsChecked != true)
        {
            _cacheNote.Text = "已关闭：每次解码都会重跑 Adobe 转换（60MP 约 3.5 秒/次），"
                            + "放大查看时也无法只解码局部。";
            return;
        }
        int gb = CacheBudgets[Math.Clamp(_cacheBudget.SelectedIndex, 0, CacheBudgets.Length - 1)];
        string where = string.IsNullOrWhiteSpace(_cacheDir.Text)
            ? @"跟随源文件：<素材目录>\.revelare-cache\（不会写系统盘）"
            : _cacheDir.Text!.Trim() + @"\.revelare-cache\";
        _cacheNote.Text =
            $"位置：{where}" + Environment.NewLine +
            $"每帧约 349 MB（60MP）；上限 {gb} GB ≈ {gb * 1024 / 349} 帧，超出按最近最少使用淘汰。" + Environment.NewLine +
            $"本次会话当前占用 {DngCache.CurrentBytes() / 1048576.0:F0} MB。退出时自动删除；" +
            "异常退出留下的目录会在下次启动时清掉。";
    }

    private static readonly int[] SheetBudgets = { 1, 2, 5, 10 };

    /// <summary>The catalog's own footprint, stated in rolls rather than bytes — one contact
    /// sheet per roll is a number the user can reason about; "MB" is not.</summary>
    private void UpdateSheetNote()
    {
        int gb = SheetBudgets[Math.Clamp(_sheetBudget.SelectedIndex, 0, SheetBudgets.Length - 1)];
        string where = string.IsNullOrWhiteSpace(_sheetDir.Text)
            ? @"%LOCALAPPDATA%\OpenRevelare\sheets\"
            : _sheetDir.Text!.Trim();
        _sheetNote.Text =
            $"位置：{where}" + Environment.NewLine +
            $"每卷一张印样（2048 长边 JPEG），约 300 KB；上限 {gb} GB ≈ {gb * 1024 * 1024 / 300} 卷，"
            + "超出按最早写入淘汰，被淘汰的卷下次打开自动重画。" + Environment.NewLine +
            $"当前占用 {SheetStore.TotalBytes() / 1048576.0:F1} MB。可安全删除——只是封面，不含任何调整。";
    }

    private static Control Row(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 0);
        control.MinWidth = 220;
        Grid.SetColumn(control, 1);
        g.Children.Add(t);
        g.Children.Add(control);
        return g;
    }

    private void Persist()
    {
        Settings.Model s = Settings.Current;
        s.DecodeBackend = (RawDecode.RawBackend)Math.Max(0, _backend.SelectedIndex);
        s.FbddMode = (RawDecode.FbddMode)Math.Max(0, _fbdd.SelectedIndex);
        s.Theme = _theme.SelectedIndex == 1 ? "light" : "dark";
        s.DecodeConcurrency = Math.Max(0, _concurrency.SelectedIndex);
        s.CacheEnabled = _cacheOn.IsChecked ?? true;
        s.CacheDirectory = (_cacheDir.Text ?? "").Trim();
        s.CacheBudgetGb = CacheBudgets[Math.Clamp(_cacheBudget.SelectedIndex, 0, CacheBudgets.Length - 1)];
        s.SheetCacheDirectory = (_sheetDir.Text ?? "").Trim();
        s.SheetCacheBudgetGb = SheetBudgets[Math.Clamp(_sheetBudget.SelectedIndex, 0, SheetBudgets.Length - 1)];
        Settings.Save();
        App.ApplyTheme(s.Theme);
    }
}
