using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
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
            "{Original} 原文件名 · {Seq} 帧序号 · {Roll} 卷名 · {RollNo} 卷号 · "
            + "{Camera} 机身 · {Film} 胶卷 · {Date} 冲洗日期；空字段连同相邻分隔符一并省略。");

        Load(Settings.Current.Export);
        SyncEnabledState();
    }

    /// <summary>
    /// Cap the dialog against the screen it actually opened on. The height follows the content and
    /// the content is long — a whole-roll export of an HDR roll has five groups open at once, more
    /// than fits on a 1080p screen — and what falls off the bottom is the button row, so the
    /// content scrolls instead. The XAML <c>MaxHeight</c> is only the design cap; this is the real
    /// one, and it is why the content sits in a ScrollViewer.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        double scaling = screen.Scaling > 0d ? screen.Scaling : 1d;
        // Room for the title bar plus a little air; below 400 the dialog would be unusable anyway,
        // so a freakishly short screen gets a window that hangs off rather than one 200px tall.
        double usable = screen.WorkingArea.Height / scaling - 48d;
        if (usable < MaxHeight) MaxHeight = Math.Max(400d, usable);

        // CenterOwner placed the window for the height it asked for, which the cap may just have
        // taken away; the frame can therefore still hang off either edge. Re-clamp once the final
        // frame size is known — before that FrameSize is still the pre-cap one.
        Dispatcher.UIThread.Post(() =>
        {
            PixelRect area = screen.WorkingArea;
            int frame = (int)Math.Ceiling(((FrameSize ?? Bounds.Size).Height) * scaling);
            int y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - frame));
            if (y != Position.Y) Position = new PixelPoint(Position.X, y);
        }, DispatcherPriority.Loaded);
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
            || ColorSpaceHint is null || LinearChk is null || SpaceRow is null
            || HdrBaseGroup is null || FormatHint is null || SizeLimitRow is null
            || UpscaleChk is null || NameTemplateBox is null || NamePreviewLbl is null
            || SharpenBox is null || SharpenRow is null || IccRow is null
            || LongEdgeRow is null || PreviewRow is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        bool dng = FmtDng.IsChecked == true;
        TiffGroup.IsVisible = !jpeg && !dng;
        JpegGroup.IsVisible = jpeg;
        if ((jpeg || dng) && LinearChk.IsChecked == true) LinearChk.IsChecked = false;
        bool linear = LinearChk.IsChecked == true;

        // Every row below is shown or hidden, never greyed: the dialog shows the parameters of the
        // file it is about to write and nothing else. What a hidden row would have said is in the
        // summary strip, which always describes the whole file.
        LongEdgeRow.IsVisible = DownsampleChk.IsChecked == true;
        SizeLimitRow.IsVisible = SizeLimitChk.IsChecked == true;

        // Three of the four deliveries have nothing for an unsharp mask to act on. Same rule as
        // ExportOptions.SharpenApplies, read off the controls rather than off a collected options
        // object, so the dialog cannot disagree with the export. Where the row goes away because
        // of a choice just made — scene-linear, DNG — that choice's own hint says so.
        SharpenRow.IsVisible = !dng && !linear && !IsHdr;

        RefreshNamePreview();
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();

        // Scene-linear and every non-exact-sRGB display export require their exact ICC. Resolve
        // the effective value independently of the control so a stale false preset is harmless.
        bool gainMap = IsHdr && jpeg && !linear;
        bool hdrMaster = IsHdr && !jpeg && !linear;
        ExportIccUiState icc = ResolveIcc(jpeg, linear);
        // A DNG states its colour as ColorMatrix1 — the DNG way — so there is no ICC decision to
        // offer; nor is there one when the profile is forced. The summary reports what was embedded.
        IccRow.IsVisible = !dng && icc.CanChange;
        if (IccChk.IsChecked != icc.EmbedIcc)
        {
            _syncingIccControl = true;
            try { IccChk.IsChecked = icc.EmbedIcc; }
            finally { _syncingIccControl = false; }
        }

        // The base-space picker exists only for a gain-map JPEG — an SDR roll has no second
        // rendition to relate to, and a TIFF of an HDR roll is the master, not a container. For
        // that JPEG the base IS the file's space, so the reported space row would say it twice.
        HdrBaseGroup.IsVisible = gainMap;
        SpaceRow.IsVisible = !gainMap;

        FormatHint.Text = dng
            ? Loc.T("线性正片，不含显示曲线；反相、镜头校正与帧编辑已烘焙其中，不是相机 RAW 的原样封装。在 Lightroom / Camera Raw 中 RAW 面板可用，不做输出锐化。")
            : IsHdr
            ? Loc.F($"HDR +{_hdrLimitStops:0.0} 档：TIFF 为 32-bit float 线性母版，JPEG 为增益图 JPEG，普通看图软件可开、HDR 屏还原高光。两者均不做输出锐化。")
            : Loc.T("TIFF 保留全部层次，适合存档或继续修图；JPEG 适合直接分享。");

        ColorSpaceHint.Text = dng
            ? (IsHdr
                ? Loc.F($"{DisplayName(_space)} 原色写入 DNG 标签；HDR 卷按 SDR 导出，高光母版用 TIFF。")
                : Loc.F($"{DisplayName(_space)} 原色写入 DNG 标签（ColorMatrix1）。"))
            : linear
            ? Loc.T("场景线性 ACEScg，输出空间不参与。")
            : hdrMaster
                ? Loc.T("线性扩展 sRGB，不裁色域；输出空间不参与。")
                : Loc.F($"{DisplayName(_space)}，在主窗口选定。");

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
        if (NamePreviewLbl is null || NameTemplateBox is null || PreviewRow is null) return;
        // No roll to expand against, so there is nothing to show and no row to show it in.
        PreviewRow.IsVisible = _namePreview is not null;
        if (_namePreview is null) { NamePreviewLbl.Text = ""; return; }

        string template = string.IsNullOrWhiteSpace(NameTemplateBox.Text)
            ? ExportNaming.Default
            : NameTemplateBox.Text!;
        // Just the name: the 首张 label says what it is, so the line does not have to.
        NamePreviewLbl.Text = $"{_namePreview(template)}.{Collect().Extension}";
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
