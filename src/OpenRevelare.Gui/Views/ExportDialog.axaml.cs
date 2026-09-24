using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Export options, shown before the destination is picked — the format decides the extension the
/// save dialog should offer, so asking for a filename first would be asking in the wrong order.
///
/// Options that do not apply to the current format are HIDDEN: the dialog shows only the
/// parameters of the file it is about to write. (It used to grey them out instead; in practice
/// the greyed block was noise the eye had to skip every time.)
///
/// The colour space is NOT chosen here any more. It is a render parameter now — Stage 2 runs
/// inside it — so it lives in the main window next to the other things that change the picture,
/// and the file simply inherits what is already on screen. This dialog reports it so the summary
/// still says what will be written.
/// </summary>
public partial class ExportDialog : Window
{
    /// <summary>The roll's output space — reported, not chosen.</summary>
    private readonly ColorSpaceDef _space;

    /// <summary>The roll's HDR limit in stops, zero for an SDR roll — reported, not chosen. It
    /// changes what the two formats mean; see <see cref="ExportOptions.HdrLimitStops"/>.</summary>
    private readonly double _hdrLimitStops;
    private bool IsHdr => _hdrLimitStops > 0d;

    /// <summary>
    /// The user's ordinary display-export preference. A forced state is painted into the
    /// checkbox without destroying this value, so toggling scene-linear on and back off restores
    /// an exact-sRGB omission choice made before the toggle.
    /// </summary>
    private bool _displayEmbedIccPreference = true;

    private bool _syncingIccControl;

    /// <summary>
    /// Expands a template against a REAL frame of the open roll, for the live preview. Supplied by
    /// the caller because the dialog knows nothing about rolls; null when there is no roll to
    /// preview against, and then the preview line simply stays empty.
    /// </summary>
    private readonly Func<string, string>? _namePreview;

    /// <summary>What the user confirmed. Only meaningful once ShowDialog returned true.</summary>
    public ExportOptions Options { get; private set; } = new();

    // Avalonia needs a parameterless constructor for XAML tooling.
    public ExportDialog() : this(rollMode: false, ColorPipeline.DefaultOutput, hdrLimitStops: 0d) { }

    /// <param name="rollMode">A whole roll goes to a folder unattended, so it needs a conflict
    /// policy. A single frame goes through a save dialog that already asked, so it does not.</param>
    /// <param name="space">The roll's output space, for the summary and the hint.</param>
    /// <param name="hdrLimitStops">The roll's HDR limit in stops above SDR white, zero when HDR
    /// is off. With it on, TIFF is the float32 master and JPEG is a gain-map JPEG.</param>
    public ExportDialog(bool rollMode, ColorSpaceDef space, double hdrLimitStops,
                        Func<string, string>? namePreview = null)
    {
        _space = space;
        _hdrLimitStops = hdrLimitStops;
        _namePreview = namePreview;
        InitializeComponent();

        Title = rollMode ? Loc.T("整卷导出选项") : Loc.T("导出选项");
        OkBtn.Content = rollMode ? Loc.T("选择目录 →") : Loc.T("选择位置 →");
        ConflictGroup.IsVisible = rollMode;
        // A single frame goes through a save dialog where the name is typed, so a template would be
        // asking for the same thing twice. It is still COLLECTED below, so the setting a roll export
        // established survives an intervening single export instead of being reset by it.
        NamingGroup.IsVisible = rollMode;
        foreach (OutputSharpen.Level level in ExportOptions.SharpenLevels)
            SharpenBox.Items.Add(new ComboBoxItem { Content = ExportOptions.SharpenName(level) });

        NameTokensLbl.Text = Loc.T(
            "可用变量：{Original} 原文件名 · {Seq} 帧序号 · {Roll} 卷名 · {RollNo} 卷号 · "
            + "{Camera} 机身 · {Film} 胶卷 · {Date} 冲洗日期。没填的字段不留痕迹，"
            + "同一批次内重名一律另存。");

        Load(Settings.Current.Export);
        SyncEnabledState();
    }

    private void Load(ExportOptions saved)
    {
        FmtJpeg.IsChecked = saved.Format == ExportFormat.Jpeg;
        FmtDng.IsChecked = saved.Format == ExportFormat.Dng;
        FmtTiff.IsChecked = saved.Format is not (ExportFormat.Jpeg or ExportFormat.Dng);
        CompressBox.SelectedIndex = saved.TiffCompression switch
        {
            TiffIO.CompressionMode.Deflate => 1,
            TiffIO.CompressionMode.None => 2,
            _ => 0,
        };
        QualitySlider.Value = Math.Clamp(saved.JpegQuality, 40, 100);
        HdrBaseBox.SelectedIndex = Math.Max(0, ExportOptions.GainMapBaseSpaces.ToList()
            .FindIndex(space => space == saved.ResolvedHdrBaseSpace));

        _displayEmbedIccPreference = saved.EmbedIcc;
        IccChk.IsChecked = saved.EmbedIcc;
        DownsampleChk.IsChecked = saved.Downsample;
        LongEdgeBox.Value = Math.Clamp(saved.MaxLongEdge, 256, 20000);
        UpscaleChk.IsChecked = saved.AllowUpscale;
        SharpenBox.SelectedIndex = Math.Max(0, ExportOptions.SharpenLevels.ToList().IndexOf(saved.Sharpen));
        NameTemplateBox.Text = string.IsNullOrWhiteSpace(saved.NameTemplate)
            ? ExportNaming.Default
            : saved.NameTemplate;
        SizeLimitChk.IsChecked = saved.LimitFileSize;
        SizeLimitBox.Value = (decimal)Math.Clamp(saved.MaxFileSizeMb, 0.1d, 1000d);
        ConflictOverwrite.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Overwrite;
        ConflictSkip.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Skip;
        ConflictUnique.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Unique;
    }

    /// <summary>The gain-map base the picker currently shows.</summary>
    private ColorSpaceDef SelectedHdrBaseSpace =>
        ExportOptions.GainMapBaseSpaces[Math.Clamp(HdrBaseBox.SelectedIndex, 0, ExportOptions.GainMapBaseSpaces.Count - 1)];

    /// <summary>
    /// The ICC state for what the dialog currently describes. One resolver for the summary, the
    /// checkbox and the hint, so the three cannot disagree about which file they are talking about.
    /// </summary>
    private ExportIccUiState ResolveIcc(bool jpeg, bool linear)
    {
        bool gainMap = IsHdr && jpeg && !linear;
        bool hdrMaster = IsHdr && !jpeg && !linear;
        return ExportIccUiPolicy.Resolve(
            gainMap ? SelectedHdrBaseSpace : _space,
            linear,
            _displayEmbedIccPreference,
            hdrMaster);
    }

    private ExportOptions Collect()
    {
        bool dng = FmtDng.IsChecked == true;
        // Scene-linear ACEScg is a TIFF decision; a DNG is already linear and in its own space, so
        // the checkbox cannot follow the format into it.
        bool exportLinear = !dng && LinearChk.IsChecked == true;
        bool jpeg = FmtJpeg.IsChecked == true;
        ExportIccUiState icc = ResolveIcc(jpeg, exportLinear);
        return new ExportOptions
        {
            Format = dng ? ExportFormat.Dng : jpeg ? ExportFormat.Jpeg : ExportFormat.Tiff16,
            TiffCompression = CompressBox.SelectedIndex switch
            {
                1 => TiffIO.CompressionMode.Deflate,
                2 => TiffIO.CompressionMode.None,
                _ => TiffIO.CompressionMode.Lzw,
            },
            JpegQuality = (int)QualitySlider.Value,
            // Carried, not chosen: the render already landed in this space, at this limit.
            ColorSpace = _space.Name,
            HdrLimitStops = _hdrLimitStops,
            HdrBaseSpace = SelectedHdrBaseSpace.Name,
            ExportLinear = exportLinear,
            // Normalize here as well as in the visual state. A stale false preset must never
            // escape merely because a control event did not run.
            EmbedIcc = icc.EmbedIcc,
            Downsample = DownsampleChk.IsChecked == true,
            MaxLongEdge = (int)(LongEdgeBox.Value ?? 2048),
            AllowUpscale = UpscaleChk.IsChecked == true,
            Sharpen = ExportOptions.SharpenLevels[
                Math.Clamp(SharpenBox.SelectedIndex, 0, ExportOptions.SharpenLevels.Count - 1)],
            NameTemplate = string.IsNullOrWhiteSpace(NameTemplateBox.Text)
                ? ExportNaming.Default
                : NameTemplateBox.Text!,
            LimitFileSize = SizeLimitChk.IsChecked == true,
            MaxFileSizeMb = (double)(SizeLimitBox.Value ?? 10m),
            Conflict = ConflictOverwrite.IsChecked == true ? ExportFile.ConflictPolicy.Overwrite
                     : ConflictSkip.IsChecked == true ? ExportFile.ConflictPolicy.Skip
                     : ExportFile.ConflictPolicy.Unique,
        };
    }

    /// <summary>The picker label: the space's own name, plus what it is in one word.</summary>
    private static string DisplayName(ColorSpaceDef s) => s.Name switch
    {
        "sRGB" => Loc.T("sRGB（通用）"),
        "AdobeRGB" => Loc.T("Adobe RGB（宽色域）"),
        "DisplayP3" => Loc.T("Display P3（宽色域）"),
        _ => s.Name,
    };

    private void SyncEnabledState()
    {
        // Guard: the IsCheckedChanged handlers fire while InitializeComponent is still wiring
        // controls up, before every named field exists.
        if (SummaryLbl is null || TiffGroup is null || JpegGroup is null
            || ColorSpaceHint is null || LinearChk is null || IccHint is null
            || HdrBaseGroup is null || FormatHint is null || SizeLimitRow is null
            || UpscaleChk is null || NameTemplateBox is null || NamePreviewLbl is null
            || SharpenBox is null || SharpenHint is null || SharpenRow is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        bool dng = FmtDng.IsChecked == true;
        bool linearPending = !dng && LinearChk.IsChecked == true;
        TiffGroup.IsVisible = !jpeg && !dng;
        JpegGroup.IsVisible = jpeg;
        if ((jpeg || dng) && LinearChk.IsChecked == true) LinearChk.IsChecked = false;
        LongEdgeRow.IsEnabled = DownsampleChk.IsChecked == true;
        UpscaleChk.IsEnabled = DownsampleChk.IsChecked == true;

        // Three of the four deliveries have nothing for an unsharp mask to act on; the row stays
        // visible and says why rather than vanishing, because "where did sharpening go" is a worse
        // question than a greyed control with a reason next to it.
        // Same rule as ExportOptions.SharpenApplies, read off the controls rather than off a
        // collected options object, so the dialog cannot disagree with the export.
        bool sharpenApplies = !dng && !linearPending && !IsHdr;
        SharpenRow.IsEnabled = sharpenApplies;
        SharpenHint.Text = sharpenApplies
            ? Loc.T("在缩放之后、写文件之前做，只动亮度不动色彩——锐化的量是按成品尺寸定的。颗粒也会被锐化，要保留颗粒就选「无」。")
            : dng
                ? Loc.T("DNG 是交给别处继续调色的素材，锐化应当在那一端按最终尺寸做，这里不做。")
                : linearPending
                    ? Loc.T("场景线性输出是中间文件，锐化应当在下游按最终尺寸做，这里不做。")
                    : Loc.T("HDR 渲染没有上界，锐化的过冲会在高光上留下空洞，这里不做。");

        RefreshNamePreview();
        SizeLimitRow.IsEnabled = SizeLimitChk.IsChecked == true;
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();

        // Scene-linear and every non-exact-sRGB display export require their exact ICC. Resolve
        // the effective value independently of the control so a stale false preset is harmless.
        bool linear = LinearChk.IsChecked == true;
        bool gainMap = IsHdr && jpeg && !linear;
        bool hdrMaster = IsHdr && !jpeg && !linear;
        ExportIccUiState icc = ResolveIcc(jpeg, linear);
        ColorSpaceHint.IsEnabled = !linear;
        // A DNG states its colour as ColorMatrix1 — the DNG way — so there is no ICC decision to
        // offer, and showing a disabled checkbox would only invite the question.
        IccChk.IsVisible = !dng;
        IccHint.IsVisible = !dng;
        IccChk.IsEnabled = icc.CanChange;
        if (IccChk.IsChecked != icc.EmbedIcc)
        {
            _syncingIccControl = true;
            try { IccChk.IsChecked = icc.EmbedIcc; }
            finally { _syncingIccControl = false; }
        }

        // The base-space picker exists only for a gain-map JPEG — an SDR roll has no second
        // rendition to relate to, and a TIFF of an HDR roll is the master, not a container.
        HdrBaseGroup.IsVisible = gainMap;

        FormatHint.Text = dng
            ? Loc.T("DNG 写的是去掉显示曲线的线性正片，交给 Lightroom / Camera Raw 等继续调色——到那边 RAW 面板是可用的。不是相机 RAW 的原样封装：反相、镜头校正与帧编辑都已烘焙进去。")
            : IsHdr
            ? Loc.F($"HDR 已开（+{_hdrLimitStops:0.0} 档）：TIFF 是 32-bit float 线性母版；JPEG 是增益图 JPEG，任何看图软件都能打开，HDR 屏上还原高光。")
            : Loc.T("TIFF 保留全部层次，适合存档或继续修图；JPEG 适合直接分享。");

        ColorSpaceHint.Text = dng
            ? (IsHdr
                ? Loc.F($"{DisplayName(_space)} 的原色写进 DNG 标签；HDR 卷按 SDR 渲染导出，高光母版请用 TIFF。")
                : Loc.F($"{DisplayName(_space)} 的原色写进 DNG 标签（ColorMatrix1）。"))
            : linear
            ? Loc.T("场景线性 ACEScg，输出空间不参与。")
            : hdrMaster
                ? Loc.T("HDR 母版为线性扩展 sRGB，不裁色域；输出空间不参与。")
                : gainMap
                    ? Loc.T("增益图 JPEG 的基底空间在下面选。")
                    : Loc.F($"{DisplayName(_space)}，在主窗口选定，导出即所见。");

        IccHint.Text = linear
            ? Loc.T("场景线性文件必须嵌入 ACEScg 配置文件。")
            : hdrMaster
                ? Loc.T("HDR 母版必须嵌入配置文件，否则无法被正确解读。")
                : icc.IsForced
                    ? Loc.T("非 sRGB 文件必须嵌入配置文件，以免被当作 sRGB 显示。")
                    : Loc.T("sRGB 可不嵌入：看图软件默认按 sRGB 处理。");

        SummaryLbl.Text = Collect().Summary();
    }
    private void OnFormatChanged(object? sender, RoutedEventArgs e) => SyncEnabledState();
    /// <summary>
    /// The name the first exported file would get, under the template as it currently reads. Shown
    /// because a template is only checkable against its output: "{Roll}_{Seq}" is not obviously
    /// "Portra400-03_001.tiff" until it says so.
    /// </summary>
    private void RefreshNamePreview()
    {
        if (NamePreviewLbl is null || NameTemplateBox is null) return;
        if (_namePreview is null) { NamePreviewLbl.Text = ""; return; }

        string template = string.IsNullOrWhiteSpace(NameTemplateBox.Text)
            ? ExportNaming.Default
            : NameTemplateBox.Text!;
        NamePreviewLbl.Text = Loc.F($"第一张将写成：{_namePreview(template)}.{Collect().Extension}");
    }

    private void OnTemplateChanged(object? sender, TextChangedEventArgs e) => RefreshNamePreview();

    private void OnDownsampleToggled(object? sender, RoutedEventArgs e) => SyncEnabledState();
    private void OnAnyChanged(object? sender, RoutedEventArgs e) => SyncEnabledState();
    private void OnIccChanged(object? sender, RoutedEventArgs e)
    {
        if (!_syncingIccControl && LinearChk is not null && FmtJpeg is not null && HdrBaseBox is not null)
        {
            ExportIccUiState icc = ResolveIcc(FmtJpeg.IsChecked == true, LinearChk.IsChecked == true);
            if (icc.CanChange)
                _displayEmbedIccPreference = IccChk?.IsChecked == true;
        }
        SyncEnabledState();
    }
    private void OnAnyChanged(object? sender, SelectionChangedEventArgs e) => SyncEnabledState();
    private void OnAnyChanged(object? sender, NumericUpDownValueChangedEventArgs e) => SyncEnabledState();
    private void OnQualityChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => SyncEnabledState();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Options = Collect();
        // Remember on confirm, not on every keystroke: a dialog the user cancelled should not
        // have changed anything.
        Settings.Current.Export = Options.Clone();
        Settings.Save();
        Close(true);
    }
}
