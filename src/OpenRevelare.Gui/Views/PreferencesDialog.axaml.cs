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
/// concurrency / disk cache / theme / language). A setting that persists but drives nothing
/// is worse than a missing one — the user changes it, sees no effect, and stops trusting the
/// dialog — so 快速预览解码 was removed here when its last live call site disappeared (the roll
/// warm-up now shares one full-quality decode with the thumbnail pass).
///
/// The whole dialog is built in <see cref="Build"/> rather than in the constructor, and built
/// again whenever the language combo changes. Unlike the XAML windows, whose text is BOUND to
/// the translation table and re-labels itself, this one composes its strings in code — so the
/// only way to preview a language switch is to compose them again. <see cref="State"/> carries
/// the user's in-progress, not-yet-saved choices across the rebuild.</summary>
public sealed class PreferencesDialog : Window
{
    private ComboBox _backend = new();
    private ComboBox _fbdd = new();
    private ComboBox _theme = new();
    private ComboBox _language = new();
    private ComboBox _concurrency = new();
    private TextBlock _dngNote = new();
    private TextBlock _concurrencyNote = new();
    private CheckBox _cacheOn = new();
    private CheckBox _cachePersist = new();
    private TextBox _cacheDir = new();
    private ComboBox _cacheBudget = new();
    private TextBlock _cacheNote = new();
    private TextBox _sheetDir = new();
    private ComboBox _sheetBudget = new();
    private TextBlock _sheetNote = new();
    private NumericUpDown _chromaValue = new();
    private TextBlock _chromaNote = new();

    /// <summary>Index 0 is 自动; index N is a manual limit of N. Mirrors ImageIo's HardCap.</summary>
    private const int MaxConcurrency = 8;

    /// <summary>
    /// The one calibrated chroma_grade: Kodak Gold 200 as the control variable, ColorChecker 24
    /// under D55. No sibling presets are offered. The Python build shipped a 淡/标准/浓 triple
    /// (2.6 / 3.05 / 3.5) whose outer two were never measured against a stock — they were round
    /// numbers either side of the real one, and presenting them next to 3.05 implied three
    /// calibrations where there is one. Anyone who needs a different reference can type it, and
    /// a value that has actually been measured against a named stock can join this as a preset.
    /// </summary>
    private const double GoldBaseline = 3.05;

    /// <summary>Everything the user can have changed but not yet saved.</summary>
    private sealed record State(int Backend, int Fbdd, int Theme, int Language, int Concurrency,
                                bool CacheOn, bool CachePersist, string CacheDir, int CacheBudget,
                                string SheetDir, int SheetBudget, double ChromaGrade);

    public PreferencesDialog()
    {
        Width = 460;
        Height = 700;   // grew when the roll-cover and language sections landed; still scrolls
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Settings.Model s = Settings.Current;
        int cb = Array.IndexOf(CacheBudgets, s.CacheBudgetGb);
        int sb = Array.IndexOf(SheetBudgets, s.SheetCacheBudgetGb);
        Build(new State(
            Backend: (int)s.DecodeBackend,
            Fbdd: (int)s.FbddMode,
            Theme: s.Theme == "light" ? 1 : 0,
            Language: s.Language switch { "zh" => 1, "en" => 2, _ => 0 },
            Concurrency: Math.Clamp(s.DecodeConcurrency, 0, MaxConcurrency),
            CacheOn: s.CacheEnabled,
            CachePersist: s.CachePersistent,
            CacheDir: s.CacheDirectory,
            CacheBudget: cb >= 0 ? cb : Array.IndexOf(CacheBudgets, 5),
            SheetDir: s.SheetCacheDirectory,
            SheetBudget: sb >= 0 ? sb : Array.IndexOf(SheetBudgets, 1),
            ChromaGrade: s.RawChromaGrade));
    }

    private State Snapshot() => new(
        Backend: Math.Max(0, _backend.SelectedIndex),
        Fbdd: Math.Max(0, _fbdd.SelectedIndex),
        Theme: Math.Max(0, _theme.SelectedIndex),
        Language: Math.Max(0, _language.SelectedIndex),
        Concurrency: Math.Max(0, _concurrency.SelectedIndex),
        CacheOn: _cacheOn.IsChecked ?? true,
        CachePersist: _cachePersist.IsChecked ?? false,
        CacheDir: _cacheDir.Text ?? "",
        CacheBudget: Math.Max(0, _cacheBudget.SelectedIndex),
        SheetDir: _sheetDir.Text ?? "",
        SheetBudget: Math.Max(0, _sheetBudget.SelectedIndex),
        ChromaGrade: CurrentChromaGrade());

    /// <summary>The live chroma_grade, falling back to the baseline on an empty box.</summary>
    private double CurrentChromaGrade() => (double)(_chromaValue.Value ?? (decimal)GoldBaseline);

    private void Build(State v)
    {
        Title = Loc.T("偏好设置");

        _backend = Combo(new[] { Loc.T("自动（Windows 有 DNG Converter 则用之）"), "LibRaw", "Adobe DNG Converter" }, v.Backend);
        _fbdd = Combo(new[] { Loc.T("关闭", "FBDD"), Loc.T("轻度"), Loc.T("完全") }, v.Fbdd);
        _theme = Combo(new[] { Loc.T("深色"), Loc.T("浅色") }, v.Theme);
        // Each language names ITSELF, untranslated — someone who lands in the wrong one has to be
        // able to find the way back out, and "Chinese" is no help if you only read 中文.
        _language = Combo(new[] { Loc.T("跟随系统"), "中文", "English" }, v.Language);

        var concurrencyItems = new string[MaxConcurrency + 1];
        concurrencyItems[0] = Loc.T("自动（按可用内存）");
        for (int i = 1; i <= MaxConcurrency; i++) concurrencyItems[i] = Loc.F($"{i} 张同时解码");
        _concurrency = Combo(concurrencyItems, v.Concurrency);

        _cacheBudget = Combo(CacheBudgets.Select(g => Loc.F($"{g} GB（约 {g * 1024 / 349} 帧 60MP）")).ToArray(), v.CacheBudget);
        _sheetBudget = Combo(SheetBudgets.Select(g => Loc.F($"{g} GB（约 {g * 1024 * 1024 / 300} 卷）")).ToArray(), v.SheetBudget);

        _chromaValue = new NumericUpDown
        {
            Minimum = 1.0m, Maximum = 6.0m, Increment = 0.05m,
            FormatString = "F2",
            Value = (decimal)v.ChromaGrade,
            MinWidth = 120,
        };

        _dngNote = Note();
        _concurrencyNote = Note();
        _cacheNote = Note();
        _sheetNote = Note();
        _chromaNote = Note();

        _cacheOn = new CheckBox { Content = Loc.T("启用 DNG 转换磁盘缓存"), IsChecked = v.CacheOn };
        _cachePersist = new CheckBox { Content = Loc.T("跨会话保留（退出不删除，下次启动直接命中）"), IsChecked = v.CachePersist };
        _cacheDir = new TextBox { Watermark = Loc.T("留空 = 和源文件同目录"), MinWidth = 250, Text = v.CacheDir };
        _sheetDir = new TextBox { Watermark = Loc.T(@"留空 = %LOCALAPPDATA%\OpenRevelare\sheets"), MinWidth = 250, Text = v.SheetDir };

        _dngNote.Text = RawDecode.IsDngConverterAvailable()
            ? Loc.T("已检测到 Adobe DNG Converter。")
            : Loc.T("未检测到 Adobe DNG Converter（选 DNG 后端将回退到 LibRaw）。");

        _concurrency.SelectionChanged += (_, _) => UpdateConcurrencyNote();
        _cacheOn.IsCheckedChanged += (_, _) => UpdateCacheNote();
        _cachePersist.IsCheckedChanged += (_, _) => UpdateCacheNote();
        _cacheBudget.SelectionChanged += (_, _) => UpdateCacheNote();
        _cacheDir.TextChanged += (_, _) => UpdateCacheNote();
        _sheetBudget.SelectionChanged += (_, _) => UpdateSheetNote();
        _sheetDir.TextChanged += (_, _) => UpdateSheetNote();
        _chromaValue.ValueChanged += (_, _) => UpdateChromaNote();
        UpdateConcurrencyNote();
        UpdateCacheNote();
        UpdateSheetNote();
        UpdateChromaNote();

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Loc.T("偏好设置"), FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(Row(Loc.T("RAW 解码后端"), _backend));
        panel.Children.Add(_dngNote);
        panel.Children.Add(Row(Loc.T("FBDD 色度降噪（去马赛克前）"), _fbdd));
        panel.Children.Add(Row(Loc.T("RAW 并发解码数"), _concurrency));
        panel.Children.Add(_concurrencyNote);

        panel.Children.Add(new TextBlock { Text = Loc.T("色度还原基准（chroma_grade）"), FontWeight = FontWeight.SemiBold,
                                           Margin = new Thickness(0, 14, 0, 2) });
        var chromaReset = new Button { Content = Loc.T("恢复基准"), Margin = new Thickness(6, 0, 0, 0) };
        chromaReset.Click += (_, _) => { _chromaValue.Value = (decimal)GoldBaseline; UpdateChromaNote(); };
        var chromaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        chromaRow.Children.Add(_chromaValue);
        chromaRow.Children.Add(chromaReset);
        panel.Children.Add(chromaRow);
        panel.Children.Add(_chromaNote);
        var browse = new Button { Content = Loc.T("浏览…"), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = Loc.T("选择缓存目录"), AllowMultiple = false });
            if (dirs.FirstOrDefault()?.TryGetLocalPath() is { } d) { _cacheDir.Text = d; UpdateCacheNote(); }
        };
        var clear = new Button { Content = Loc.T("恢复默认"), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { _cacheDir.Text = ""; UpdateCacheNote(); };
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        dirRow.Children.Add(_cacheDir); dirRow.Children.Add(browse); dirRow.Children.Add(clear);

        panel.Children.Add(new TextBlock { Text = Loc.T("磁盘缓存"), FontWeight = FontWeight.SemiBold,
                                           Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(_cacheOn);
        panel.Children.Add(_cachePersist);
        panel.Children.Add(dirRow);
        panel.Children.Add(Row(Loc.T("缓存上限"), _cacheBudget));
        panel.Children.Add(_cacheNote);

        var sheetBrowse = new Button { Content = Loc.T("浏览…"), Margin = new Thickness(6, 0, 0, 0) };
        sheetBrowse.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = Loc.T("选择印样存放目录"), AllowMultiple = false });
            if (dirs.FirstOrDefault()?.TryGetLocalPath() is { } d) { _sheetDir.Text = d; UpdateSheetNote(); }
        };
        var sheetClear = new Button { Content = Loc.T("恢复默认"), Margin = new Thickness(6, 0, 0, 0) };
        sheetClear.Click += (_, _) => { _sheetDir.Text = ""; UpdateSheetNote(); };
        var sheetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        sheetRow.Children.Add(_sheetDir); sheetRow.Children.Add(sheetBrowse); sheetRow.Children.Add(sheetClear);

        panel.Children.Add(new TextBlock { Text = Loc.T("卷目录缩略图（印样）"), FontWeight = FontWeight.SemiBold,
                                           Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(sheetRow);
        panel.Children.Add(Row(Loc.T("存放上限"), _sheetBudget));
        panel.Children.Add(_sheetNote);

        panel.Children.Add(Row(Loc.T("界面主题"), _theme));
        // Deliberately bilingual and NOT translated: this is the row you have to find when the
        // UI is in a language you cannot read.
        panel.Children.Add(Row("界面语言 / Language", _language));

        var save = new Button { Content = Loc.T("保存"), Margin = new Thickness(0, 14, 6, 0) };
        var cancel = new Button { Content = Loc.T("取消"), Margin = new Thickness(0, 14, 0, 0) };
        save.Click += (_, _) => { Persist(); Close(); };
        // Cancel puts the previewed theme AND language back to what is on disk — otherwise
        // "取消" would leave the app in the state the user just backed out of.
        cancel.Click += (_, _) => { App.ApplyTheme(); Loc.Apply(); Close(); };
        // Live theme preview as the user picks.
        _theme.SelectionChanged += (_, _) => App.ApplyTheme(_theme.SelectedIndex == 1 ? "light" : "dark");
        // …and the same for language. Everything already on screen re-labels itself; this dialog
        // has to be composed again, carrying the unsaved choices over.
        _language.SelectionChanged += (_, _) =>
        {
            Loc.Apply(_language.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "auto" });
            Build(Snapshot());
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel };
    }

    private static ComboBox Combo(string[] items, int index) =>
        new() { ItemsSource = items, SelectedIndex = Math.Clamp(index, 0, items.Length - 1) };

    private static TextBlock Note() =>
        new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>Show what the current choice actually means — for 自动 that is a live number,
    /// and without it the setting is a black box the user has no way to sanity-check.</summary>
    private void UpdateConcurrencyNote()
    {
        var (auto, free) = ImageIo.AutoConcurrencyInfo();
        string freeText = free is long f
            ? Loc.F($"当前可用内存 {f / 1073741824.0:F1} GB")
            : Loc.T("本平台无法读取可用内存，退回按总内存估算");

        // One interpolated literal apiece, not a concatenation: `$"…" + "…"` collapses to a plain
        // string before Loc.F could see the composite format, and the overload would not bind.
        _concurrencyNote.Text = _concurrency.SelectedIndex <= 0
            ? Loc.F($"{freeText} → 自动使用 {auto} 张并发。每张解码中的 RAW 约占 1.2 GB，导入过程中会随可用内存变化自动升降。")
            : Loc.F($"手动固定 {_concurrency.SelectedIndex} 张（当前自动会选 {auto} 张）。{freeText}；每张解码中的 RAW 约占 1.2 GB，设高了会让机器换页变慢。");
    }

    private static readonly int[] CacheBudgets = { 2, 5, 10, 20, 50 };

    /// <summary>Show where the disk is going and how much of it — a cache the user cannot see
    /// the size or location of is exactly how a system drive fills up unnoticed.</summary>
    private void UpdateCacheNote()
    {
        if (_cacheOn.IsChecked != true)
        {
            _cacheNote.Text = Loc.T("已关闭：每次解码都会重跑 Adobe 转换（60MP 约 3.5 秒/次），"
                                    + "放大查看时也无法只解码局部。");
            return;
        }
        int gb = CacheBudgets[Math.Clamp(_cacheBudget.SelectedIndex, 0, CacheBudgets.Length - 1)];
        string where = string.IsNullOrWhiteSpace(_cacheDir.Text)
            ? Loc.T(@"跟随源文件：<素材目录>\.revelare-cache\（不会写系统盘）")
            : _cacheDir.Text!.Trim() + @"\.revelare-cache\";
        bool persist = _cachePersist.IsChecked == true;
        _cacheNote.Text =
            Loc.F($"位置：{where}") + Environment.NewLine +
            Loc.F($"每帧约 349 MB（60MP）；上限 {gb} GB ≈ {gb * 1024 / 349} 帧，超出按最近最少使用淘汰。") + Environment.NewLine +
            Loc.F($"当前占用 {DngCache.CurrentBytes() / 1048576.0:F0} MB。") + Environment.NewLine +
            (persist
                ? Loc.T("跨会话保留：重开应用直接命中缓存（命中 418 毫秒 vs 未命中 6.1 秒/帧），"
                        + "上限与淘汰照常生效，随时可关掉或手动删除 .revelare-cache 目录。")
                : Loc.T("仅本次会话：退出时自动删除，异常退出留下的目录会在下次启动时清掉——"
                        + "代价是每次重开应用、重开卷都要重付一遍 Adobe 转换。"));
    }

    /// <summary>
    /// Say what this number actually is, because the obvious reading of it is wrong. It looks like
    /// a saturation knob and is not one: it sets which stock's colour rendering the inversion
    /// treats as the reference, so moving it shifts the whole calibration baseline. Anyone who
    /// wants "richer" wants the SceneBase saturation slider, and the note has to send them there
    /// rather than let them discover it by pushing this to 6.0.
    /// </summary>
    private void UpdateChromaNote()
    {
        double cg = CurrentChromaGrade();
        // Off-baseline is not an error, but it IS uncalibrated, and the note says so rather than
        // dressing the number up as another preset.
        // One interpolated literal apiece — see UpdateConcurrencyNote: concatenating onto an
        // interpolated string collapses it before Loc.F can see the composite format.
        double offPct = Math.Abs(cg - GoldBaseline) / GoldBaseline * 100;
        string dir = cg > GoldBaseline ? Loc.T("浓") : Loc.T("淡");
        string baseline = Math.Abs(cg - GoldBaseline) < 0.005
            ? Loc.T("当前为标定基准值。")
            : Loc.F($"已偏离基准 {offPct:F0}%（偏{dir}），此值未经实测标定。");

        _chromaNote.Text =
            Loc.F($"{GoldBaseline:F2} 是以 Kodak Gold 200 为控制变量、ColorChecker 24 + D55 实测标定的唯一基准值。它决定以哪一种胶卷的色彩表现作为还原基准——不是饱和度滑块：调它会挪动整套标定基准，各卷相对基准的差异（Portra 偏柔、Ektar 偏浓）是被有意保留的风格特征。只想调浓淡请用 SceneBase 的饱和度。") + Environment.NewLine +
            baseline + Loc.T("仅对相机 RAW 生效：扫描件的 ICC 矩阵已展开通道间色度差值，固定按 1.0 导入。")
                     + Environment.NewLine +
            Loc.T("作用于此后新导入的卷；已在卷中的帧保留各自存下的值，可在工程文件或 CLI 中单独改。");
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
            Loc.F($"位置：{where}") + Environment.NewLine +
            Loc.F($"每卷一张印样（2048 长边 JPEG），约 300 KB；上限 {gb} GB ≈ {gb * 1024 * 1024 / 300} 卷，超出按最早写入淘汰，被淘汰的卷下次打开自动重画。") + Environment.NewLine +
            Loc.F($"当前占用 {SheetStore.TotalBytes() / 1048576.0:F1} MB。可安全删除——只是封面，不含任何调整。");
    }

    private static Control Row(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        // Wraps: an English label ("FBDD chroma noise reduction (pre-demosaic)") is three times
        // the width of its Chinese original, and this column is what is left of 460px after the
        // control's 220.
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
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
        s.Language = _language.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "auto" };
        s.DecodeConcurrency = Math.Max(0, _concurrency.SelectedIndex);
        s.CacheEnabled = _cacheOn.IsChecked ?? true;
        s.CachePersistent = _cachePersist.IsChecked ?? false;
        s.CacheDirectory = (_cacheDir.Text ?? "").Trim();
        s.CacheBudgetGb = CacheBudgets[Math.Clamp(_cacheBudget.SelectedIndex, 0, CacheBudgets.Length - 1)];
        s.SheetCacheDirectory = (_sheetDir.Text ?? "").Trim();
        s.SheetCacheBudgetGb = SheetBudgets[Math.Clamp(_sheetBudget.SelectedIndex, 0, SheetBudgets.Length - 1)];
        s.RawChromaGrade = Math.Clamp(CurrentChromaGrade(), 1.0, 6.0);
        Settings.Save();
        App.ApplyTheme(s.Theme);
        Loc.Apply(s.Language);
    }
}
