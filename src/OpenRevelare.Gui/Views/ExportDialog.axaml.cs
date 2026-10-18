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
    private bool _applyingDevice;
    private bool _loadingFilmFrame = true;
    private readonly IReadOnlyList<RollFrame>? _filmFrames;
    private readonly RollFilmCaptureEditor? _filmEditor;
    private int _filmFrameIndex;


    /// <summary>
    /// The scheme last selected in the naming picker. Kept so that choosing 自定义… can seed the
    /// template box with the scheme that was in force instead of with an empty line.
    /// </summary>
    private int _lastSchemeIndex;

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
                        Func<string, string>? namePreview = null, string? sourceExifSummary = null,
                        IReadOnlyList<RollFrame>? frames = null)
    {
        _filmFrames = rollMode && frames is { Count: > 0 } ? frames : null;
        _filmEditor = _filmFrames is null ? null : new RollFilmCaptureEditor(_filmFrames.Count);
        _space = space;
        _hdrLimitStops = hdrLimitStops;
        _namePreview = namePreview;
        InitializeComponent();
        SourceExifSummaryLbl.Text = string.IsNullOrWhiteSpace(sourceExifSummary)
            ? Loc.T("源文件没有可显示的设备信息；导出时按每帧来源处理。") : sourceExifSummary;
        LoadDevices(CameraLibraryBox, Settings.Current.FilmCameras ??= [], "选用已保存机身");
        LoadDevices(LensLibraryBox, Settings.Current.FilmLenses ??= [], "选用已保存镜头");

        Title = rollMode ? Loc.T("整卷导出选项") : Loc.T("导出选项");
        OkBtn.Content = rollMode ? Loc.T("选择目录 →") : Loc.T("选择位置 →");
        ConflictGroup.IsVisible = rollMode;
        // Naming shows in BOTH modes. It used to be roll-only, on the grounds that a single frame
        // types its name in the save dialog anyway — but that dialog opens with the name this
        // scheme produced (MainViewModel.SuggestedExportName), so hiding the scheme left a single
        // export governed by a setting it could neither see nor change: to change it you had to
        // open the roll-export dialog. What a single frame does NOT need is the conflict policy,
        // because its save dialog does ask before replacing a file.
        SingleNameHintRow.IsVisible = !rollMode;
        foreach (OutputSharpen.Level level in ExportOptions.SharpenLevels)
            SharpenBox.Items.Add(new ComboBoxItem { Content = ExportOptions.SharpenName(level) });

        // The schemes, then the way out: 自定义… is last and is the only place the token syntax
        // appears. Its index is ExportOptions.NameTemplates.Count, which is what CustomNameIndex
        // reads — nothing else may be appended after it.
        foreach (string template in ExportOptions.NameTemplates)
            NamePresetBox.Items.Add(new ComboBoxItem { Content = ExportOptions.NameTemplateName(template) });
        NamePresetBox.Items.Add(new ComboBoxItem { Content = ExportOptions.NameTemplateName(null) });

        NameTokensLbl.Text = Loc.T(
            "{Original} 原文件名 · {Seq} 帧序号 · {Roll} 卷名 · {RollNo} 卷号 · "
            + "{Camera} 机身 · {Film} 胶卷 · {Date} 冲洗日期；空字段连同相邻分隔符一并省略。");

        Load(Settings.Current.Export);
        _loadingFilmFrame = false;
        FilmFrameNavigator.IsVisible = _filmEditor is not null;
        if (_filmEditor is not null) LoadFilmFrame();
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
        FmtHeic.IsChecked = saved.Format == ExportFormat.Heic;
        FmtTiff.IsChecked = saved.Format is not (ExportFormat.Jpeg or ExportFormat.Dng or ExportFormat.Heic);
        CompressBox.SelectedIndex = saved.TiffCompression switch
        {
            TiffIO.CompressionMode.Deflate => 1,
            TiffIO.CompressionMode.None => 2,
            _ => 0,
        };
        QualitySlider.Value = Math.Clamp(saved.JpegQuality, 40, 100);
        HeicQualitySlider.Value = Math.Clamp(saved.HeicQuality, 40, 100);
        KeepExifChk.IsChecked = saved.KeepSourceExif;
        HdrBaseBox.SelectedIndex = Math.Max(0, ExportOptions.GainMapBaseSpaces.ToList()
            .FindIndex(space => space == saved.ResolvedHdrBaseSpace));

        _displayEmbedIccPreference = saved.EmbedIcc;
        IccChk.IsChecked = saved.EmbedIcc;
        DownsampleChk.IsChecked = saved.Downsample;
        LongEdgeBox.Value = Math.Clamp(saved.MaxLongEdge, 256, 20000);
        UpscaleChk.IsChecked = saved.AllowUpscale;
        SharpenBox.SelectedIndex = Math.Max(0, ExportOptions.SharpenLevels.ToList().IndexOf(saved.Sharpen));
        // A saved template that is one of the schemes selects that scheme; anything else is the
        // user's own and selects 自定义…, with the text kept verbatim. The box is filled either
        // way, so picking 自定义… later starts from the scheme that was in force rather than blank.
        string savedTemplate = string.IsNullOrWhiteSpace(saved.NameTemplate)
            ? ExportNaming.Default
            : saved.NameTemplate.Trim();
        int preset = ExportOptions.NameTemplates.ToList()
            .FindIndex(t => string.Equals(t, savedTemplate, StringComparison.OrdinalIgnoreCase));
        NameTemplateBox.Text = savedTemplate;
        NamePresetBox.SelectedIndex = preset >= 0 ? preset : CustomNameIndex;
        _lastSchemeIndex = Math.Max(0, preset);
        SizeLimitChk.IsChecked = saved.LimitFileSize;
        SizeLimitBox.Value = (decimal)Math.Clamp(saved.MaxFileSizeMb, 0.1d, 1000d);
        ConflictOverwrite.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Overwrite;
        ConflictSkip.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Skip;
        ConflictUnique.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Unique;
    }

    /// <summary>The 自定义… item's index: after the schemes, which is where the ctor appends it.</summary>
    private static int CustomNameIndex => ExportOptions.NameTemplates.Count;

    private bool IsCustomName => NamePresetBox.SelectedIndex >= CustomNameIndex;

    /// <summary>
    /// The template in force: the selected scheme's, or whatever the user typed under 自定义…. Blank
    /// falls back to the default rather than to an empty name.
    /// </summary>
    private string SelectedNameTemplate
    {
        get
        {
            if (!IsCustomName && NamePresetBox.SelectedIndex >= 0)
                return ExportOptions.NameTemplates[NamePresetBox.SelectedIndex];
            return string.IsNullOrWhiteSpace(NameTemplateBox.Text)
                ? ExportNaming.Default
                : NameTemplateBox.Text!;
        }
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
        bool hdrMaster = IsHdr && FmtTiff.IsChecked == true && !linear;
        return ExportIccUiPolicy.Resolve(
            gainMap ? SelectedHdrBaseSpace : _space,
            linear,
            _displayEmbedIccPreference,
            hdrMaster);
    }

    private void SaveFilmFrame()
    {
        if (!_loadingFilmFrame && !_applyingDevice && _filmEditor is not null)
            _filmEditor.Update(_filmFrameIndex, CollectFilmCapture());
    }

    private void LoadFilmFrame()
    {
        if (_filmEditor is null || _filmFrames is null) return;
        _loadingFilmFrame = true;
        try
        {
            FilmCaptureMetadata film = _filmEditor.Get(_filmFrameIndex);
            CameraLibraryBox.SelectedIndex = 0;
            LensLibraryBox.SelectedIndex = 0;
            CameraMakeBox.Text = film.Camera.Make; CameraModelBox.Text = film.Camera.Model;
            CameraSerialBox.Text = film.Camera.SerialNumber;
            LensMakeBox.Text = film.Lens.Make; LensModelBox.Text = film.Lens.Model;
            LensSerialBox.Text = film.Lens.SerialNumber;
            FilmIsoBox.Text = film.Iso; FilmApertureBox.Text = film.FNumber;
            FilmShutterBox.Text = film.ExposureTime; FilmFocalBox.Text = film.FocalLength;
            FilmDateBox.Text = film.DateTimeOriginal; FilmArtistBox.Text = film.Artist;
            FilmCopyrightBox.Text = film.Copyright;
            CameraSyncChk.IsChecked = _filmEditor.CameraSync(_filmFrameIndex);
            LensSyncChk.IsChecked = _filmEditor.LensSync(_filmFrameIndex);
            var frame = _filmFrames[_filmFrameIndex];
            FilmFrameLabel.Text = $"{_filmFrameIndex + 1} / {_filmFrames.Count} · {frame.Label}";
            FilmFramePreview.Source = frame.Thumbnail;
            PreviousFilmFrameBtn.IsEnabled = _filmFrameIndex > 0;
            NextFilmFrameBtn.IsEnabled = _filmFrameIndex + 1 < _filmFrames.Count;
            ExifErrorLbl.IsVisible = false;
        }
        finally { _loadingFilmFrame = false; }
        SyncEnabledState();
        _ = RefreshFilmSourceSummaryAsync(_filmFrameIndex);
    }

    private async Task RefreshFilmSourceSummaryAsync(int index)
    {
        if (_filmFrames is null) return;
        SourceExifSummaryLbl.Text = Loc.T("正在读取源 EXIF …");
        string summary;
        try
        {
            summary = await Task.Run(() => SourceExif.Read(_filmFrames[index].Path)?.CaptureSummary)
                ?? Loc.T("源文件没有可显示的设备信息；导出时按每帧来源处理。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { summary = Loc.T("源 EXIF 暂无法读取；关闭来源保留后仍可导出。"); }
        if (_filmFrameIndex == index) SourceExifSummaryLbl.Text = summary;
    }

    private void MoveFilmFrame(int delta)
    {
        if (_filmFrames is null) return;
        SaveFilmFrame();
        _filmFrameIndex = Math.Clamp(_filmFrameIndex + delta, 0, _filmFrames.Count - 1);
        LoadFilmFrame();
    }
    private void OnPreviousFilmFrame(object? sender, RoutedEventArgs e) => MoveFilmFrame(-1);
    private void OnNextFilmFrame(object? sender, RoutedEventArgs e) => MoveFilmFrame(1);
    private void OnDeviceSyncChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingFilmFrame || _filmEditor is null) return;
        SaveFilmFrame();
        _filmEditor.SetSync(_filmFrameIndex, CameraSyncChk.IsChecked == true, LensSyncChk.IsChecked == true);
        LoadFilmFrame();
    }

    private ExportOptions Collect()
    {
        SaveFilmFrame();
        bool dng = FmtDng.IsChecked == true;
        // Scene-linear ACEScg is a TIFF decision; a DNG is already linear and in its own space, so
        // the checkbox cannot follow the format into it.
        bool exportLinear = FmtTiff.IsChecked == true && LinearChk.IsChecked == true;
        bool jpeg = FmtJpeg.IsChecked == true;
        bool heic = FmtHeic.IsChecked == true;
        ExportIccUiState icc = ResolveIcc(jpeg, exportLinear);
        return new ExportOptions
        {
            Format = dng ? ExportFormat.Dng : heic ? ExportFormat.Heic : jpeg ? ExportFormat.Jpeg : ExportFormat.Tiff16,
            TiffCompression = CompressBox.SelectedIndex switch
            {
                1 => TiffIO.CompressionMode.Deflate,
                2 => TiffIO.CompressionMode.None,
                _ => TiffIO.CompressionMode.Lzw,
            },
            JpegQuality = (int)QualitySlider.Value,
            HeicQuality = (int)HeicQualitySlider.Value,
            KeepSourceExif = KeepExifChk.IsChecked == true,
            FrameFilmCaptures = FilmInfoChk.IsChecked == true ? _filmEditor?.Snapshot() : null,
            FilmCapture = _filmEditor is null && FilmInfoChk.IsChecked == true && HasFilmCaptureDetails()
                ? CollectFilmCapture() : null,
            // Carried, not chosen: the render already landed in this space, at this limit.
            ColorSpace = _space.Name,
            HdrLimitStops = _hdrLimitStops,
            HdrBaseSpace = SelectedHdrBaseSpace.Name,
            ExportLinear = exportLinear,
            // Normalize here as well as in the visual state. A stale false preset must never
            // escape merely because a control event did not run.
            EmbedIcc = heic || icc.EmbedIcc,
            Downsample = DownsampleChk.IsChecked == true,
            MaxLongEdge = (int)(LongEdgeBox.Value ?? 2048),
            AllowUpscale = UpscaleChk.IsChecked == true,
            Sharpen = ExportOptions.SharpenLevels[
                Math.Clamp(SharpenBox.SelectedIndex, 0, ExportOptions.SharpenLevels.Count - 1)],
            NameTemplate = SelectedNameTemplate,
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
            || LongEdgeRow is null || PreviewRow is null
            || NamePresetBox is null || CustomNameRow is null || FmtHeic is null
            || HeicQualitySlider is null || HeicQualityLbl is null
            || JpegQualityRow is null || HeicQualityRow is null
            || JpegFormatDetail is null || HeicFormatDetail is null || TiffFormatDetail is null
            || KeepExifChk is null || FilmInfoChk is null || FilmEditorGroup is null
            || FilmCopyrightBox is null || ExifErrorLbl is null || FilmInfoEntry is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        bool heic = FmtHeic.IsChecked == true;
        bool dng = FmtDng.IsChecked == true;
        FilmInfoEntry.Content = FilmInfoChk.IsChecked == true
            && (HasFilmCaptureDetails() || _filmEditor?.Snapshot().Any(f => f != new FilmCaptureMetadata()) == true)
            ? Loc.T("编辑胶片拍摄信息（已填写） →")
            : Loc.T("编辑胶片拍摄信息 →");
        TiffGroup.IsVisible = !jpeg && !dng && !heic;
        JpegGroup.IsVisible = jpeg || heic;
        JpegQualityRow.IsVisible = jpeg;
        HeicQualityRow.IsVisible = heic;
        if ((jpeg || dng || heic) && LinearChk.IsChecked == true) LinearChk.IsChecked = false;
        bool linear = LinearChk.IsChecked == true;
        JpegFormatDetail.Text = IsHdr ? Loc.T("HDR 增益图 · 兼容 SDR") : Loc.T("分享 · 广泛兼容");
        HeicFormatDetail.Text = IsHdr ? Loc.T("HDR PQ · 10-bit 高效压缩") : Loc.T("10-bit · 高效压缩");
        TiffFormatDetail.Text = IsHdr || linear ? Loc.T("32-bit float · 线性母版") : Loc.T("16-bit · 无损存档");

        // Every row below is shown or hidden, never greyed: the dialog shows the parameters of the
        // file it is about to write and nothing else. What a hidden row would have said is in the
        // summary strip, which always describes the whole file.
        LongEdgeRow.IsVisible = DownsampleChk.IsChecked == true;
        SizeLimitRow.IsVisible = SizeLimitChk.IsChecked == true;
        // The token syntax is the one thing in here that has to be read rather than recognised, so
        // it exists only for whoever asked for it by name.
        CustomNameRow.IsVisible = IsCustomName;

        // Three of the four deliveries have nothing for an unsharp mask to act on. Same rule as
        // ExportOptions.SharpenApplies, read off the controls rather than off a collected options
        // object, so the dialog cannot disagree with the export. Where the row goes away because
        // of a choice just made — scene-linear, DNG — that choice's own hint says so.
        SharpenRow.IsVisible = !dng && !linear && !IsHdr;

        RefreshNamePreview();
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();
        HeicQualityLbl.Text = ((int)HeicQualitySlider.Value).ToString();

        // Scene-linear and every non-exact-sRGB display export require their exact ICC. Resolve
        // the effective value independently of the control so a stale false preset is harmless.
        bool gainMap = IsHdr && jpeg && !linear;
        bool hdrMaster = IsHdr && FmtTiff.IsChecked == true && !linear;
        ExportIccUiState icc = ResolveIcc(jpeg, linear);
        // A DNG states its colour as ColorMatrix1 — the DNG way — so there is no ICC decision to
        // offer; nor is there one when the profile is forced. The summary reports what was embedded.
        IccRow.IsVisible = !dng && !heic && icc.CanChange;
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

        ExportOptions selected = Collect();
        FormatHint.Text = dng
            ? Loc.T("在 Lightroom / Camera Raw 中继续调色；反相与帧编辑已烘焙，不含显示曲线。")
                + (IsHdr ? " " + Loc.T("HDR 卷按 SDR 写出。") : "")
            : heic
            ? (IsHdr ? Loc.T("保留 HDR 高光；需要支持 HDR HEIC 的查看器。")
                : Loc.T("体积较小，适合支持 HEIC 的相册与设备。"))
            : jpeg
            ? (IsHdr ? Loc.T("普通查看器显示 SDR，支持增益图的 HDR 查看器可还原高光。")
                : selected.ResolvedColorSpace != ColorSpaces.Srgb
                    ? Loc.F($"JPEG 沿用本卷的 {selected.ColorSpace}；如需 sRGB，请先在编辑器中明确切换整卷渲染空间。")
                    : Loc.T("适合直接分享，兼容照片查看器和网页。"))
            : linear
            ? Loc.T("场景线性 ACEScg TIFF 不包含成片调整；归档时仍需保留原片、工程和参考。")
            : IsHdr
            ? Loc.T("保留扩展范围与色域，适合存档或继续后期处理。")
            : Loc.T("无损保留成片层次，适合存档或继续修图。");

        if (dng)
            FormatHint.Text += " " + Loc.T("系统相册可能仅显示缩略图，完整图像请用 RAW 软件打开。");
        else if (!jpeg && !heic)
            FormatHint.Text += " " + Loc.T("手机相册对 TIFF 支持有限，跨设备分享建议使用 JPEG。");

        ColorSpaceHint.Text = dng
            ? (IsHdr
                ? Loc.F($"{DisplayName(_space)} 原色写入 DNG 标签；HDR 卷按 SDR 导出，高光母版用 TIFF。")
                : Loc.F($"{DisplayName(_space)} 原色写入 DNG 标签（ColorMatrix1）。"))
            : heic && IsHdr
            ? Loc.T("BT.2020 PQ，参考白 203 nits；保留 HDR 高光。")
            : linear
            ? Loc.T("场景线性 ACEScg，输出空间不参与。")
            : hdrMaster
                ? Loc.T("线性扩展 sRGB，不裁色域；输出空间不参与。")
                : Loc.F($"{DisplayName(_space)}，在主窗口选定。");

        SummaryLbl.Text = selected.Summary();
    }
    private void OnFormatChanged(object? sender, RoutedEventArgs e) => SyncEnabledState();
    /// <summary>
    /// The name the first exported file would get, under the template as it currently reads. Shown
    /// because a template is only checkable against its output: "{Roll}_{Seq}" is not obviously
    /// "Portra400-03_001.tiff" until it says so.
    /// </summary>
    private void RefreshNamePreview()
    {
        if (NamePreviewLbl is null || NameTemplateBox is null || PreviewRow is null
            || NamePresetBox is null) return;
        // No roll to expand against, so there is nothing to show and no row to show it in.
        PreviewRow.IsVisible = _namePreview is not null;
        if (_namePreview is null) { NamePreviewLbl.Text = ""; return; }

        // Just the name: the 实例 label says what it is, so the line does not have to.
        NamePreviewLbl.Text = $"{_namePreview(SelectedNameTemplate)}.{Collect().Extension}";
    }

    private void OnTemplateChanged(object? sender, TextChangedEventArgs e) => RefreshNamePreview();

    /// <summary>
    /// A naming scheme was picked. Choosing 自定义… seeds the template box with the scheme that was
    /// in force, so a custom template starts from something that works — unless the box already
    /// holds a template the user wrote, which looking at the schemes and coming back must not lose.
    /// </summary>
    private void OnNamePresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NamePresetBox is not null && NameTemplateBox is not null)
        {
            if (!IsCustomName)
            {
                if (NamePresetBox.SelectedIndex >= 0) _lastSchemeIndex = NamePresetBox.SelectedIndex;
            }
            else
            {
                string text = (NameTemplateBox.Text ?? "").Trim();
                bool authored = text.Length > 0 && !ExportOptions.NameTemplates.Any(
                    t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase));
                if (!authored)
                    NameTemplateBox.Text = ExportOptions.NameTemplates[
                        Math.Clamp(_lastSchemeIndex, 0, ExportOptions.NameTemplates.Count - 1)];
            }
        }
        SyncEnabledState();
    }

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

    private void OnOpenFilmInfoClick(object? sender, RoutedEventArgs e) => FilmInfoDrawer.IsPaneOpen = true;
    private void OnCloseFilmInfoClick(object? sender, RoutedEventArgs e) => FilmInfoDrawer.IsPaneOpen = false;
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private static void LoadDevices(ComboBox box, List<CaptureDevice> devices, string prompt)
    {
        box.IsVisible = devices.Count > 0;
        box.Items.Add(new ComboBoxItem { Content = Loc.T(prompt) });
        foreach (CaptureDevice device in devices)
            box.Items.Add(new ComboBoxItem { Content = device.Label, Tag = device });
        box.SelectedIndex = 0;
    }

    private FilmCaptureMetadata CollectFilmCapture() => new()
    {
        Camera = new CaptureDevice { Make = CameraMakeBox.Text?.Trim() ?? "", Model = CameraModelBox.Text?.Trim() ?? "", SerialNumber = CameraSerialBox.Text?.Trim() ?? "" },
        Lens = new CaptureDevice { Make = LensMakeBox.Text?.Trim() ?? "", Model = LensModelBox.Text?.Trim() ?? "", SerialNumber = LensSerialBox.Text?.Trim() ?? "" },
        Iso = FilmIsoBox.Text?.Trim() ?? "", FNumber = FilmApertureBox.Text?.Trim() ?? "",
        ExposureTime = FilmShutterBox.Text?.Trim() ?? "", FocalLength = FilmFocalBox.Text?.Trim() ?? "",
        DateTimeOriginal = FilmDateBox.Text?.Trim() ?? "", Artist = FilmArtistBox.Text?.Trim() ?? "",
        Copyright = FilmCopyrightBox.Text?.Trim() ?? "",
    };

    private bool HasFilmCaptureDetails()
    {
        FilmCaptureMetadata film = CollectFilmCapture();
        return !film.Camera.IsEmpty || !film.Lens.IsEmpty
            || !string.IsNullOrWhiteSpace(film.Camera.SerialNumber)
            || !string.IsNullOrWhiteSpace(film.Lens.SerialNumber)
            || !string.IsNullOrWhiteSpace(film.Iso)
            || !string.IsNullOrWhiteSpace(film.FNumber)
            || !string.IsNullOrWhiteSpace(film.ExposureTime)
            || !string.IsNullOrWhiteSpace(film.FocalLength)
            || !string.IsNullOrWhiteSpace(film.DateTimeOriginal)
            || !string.IsNullOrWhiteSpace(film.Artist)
            || !string.IsNullOrWhiteSpace(film.Copyright);
    }

    private void EnableFilmInfoIfEdited()
    {
        if (!_loadingFilmFrame && !_applyingDevice && FilmInfoChk is not null && FilmCopyrightBox is not null
            && HasFilmCaptureDetails())
            FilmInfoChk.IsChecked = true;
    }

    private void ApplyDevice(ComboBox box, TextBox make, TextBox model, TextBox serial)
    {
        if (_loadingFilmFrame) return;
        if (box.SelectedItem is not ComboBoxItem { Tag: CaptureDevice device }) return;
        _applyingDevice = true;
        try { make.Text = device.Make; model.Text = device.Model; serial.Text = device.SerialNumber; }
        finally { _applyingDevice = false; }
        SyncEnabledState();
    }

    private void OnCameraSelected(object? sender, SelectionChangedEventArgs e)
    {
        ApplyDevice(CameraLibraryBox, CameraMakeBox, CameraModelBox, CameraSerialBox);
        EnableFilmInfoIfEdited();
    }
    private void OnLensSelected(object? sender, SelectionChangedEventArgs e)
    {
        ApplyDevice(LensLibraryBox, LensMakeBox, LensModelBox, LensSerialBox);
        EnableFilmInfoIfEdited();
    }
    private void OnCameraEdited(object? sender, TextChangedEventArgs e)
    {
        if (!_loadingFilmFrame && !_applyingDevice && CameraLibraryBox?.SelectedItem is ComboBoxItem { Tag: CaptureDevice device }
            && device != CollectFilmCapture().Camera) CameraLibraryBox.SelectedIndex = 0;
        EnableFilmInfoIfEdited();
        SyncEnabledState();
    }
    private void OnLensEdited(object? sender, TextChangedEventArgs e)
    {
        if (!_loadingFilmFrame && !_applyingDevice && LensLibraryBox?.SelectedItem is ComboBoxItem { Tag: CaptureDevice device }
            && device != CollectFilmCapture().Lens) LensLibraryBox.SelectedIndex = 0;
        EnableFilmInfoIfEdited();
        SyncEnabledState();
    }
    private void OnFilmFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (ExifErrorLbl is not null) ExifErrorLbl.IsVisible = false;
        EnableFilmInfoIfEdited();
        SyncEnabledState();
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Options = Collect();
        int validatingIndex = -1;
        try
        {
            Options.FilmCapture?.Validate();
            if (Options.FrameFilmCaptures is { } films)
                for (int i = 0; i < films.Length; i++)
                {
                    validatingIndex = i;
                    films[i].Validate();
                }
        }
        catch (FormatException ex)
        {
            if (validatingIndex >= 0)
            {
                _filmFrameIndex = validatingIndex;
                LoadFilmFrame();
            }
            ExifErrorLbl.Text = ex.Message switch
            {
                var m when m.StartsWith("ISO", StringComparison.Ordinal) => Loc.T("ISO 必须是 1–65535 之间的整数。"),
                var m when m.StartsWith("Capture time", StringComparison.Ordinal) => Loc.T("拍摄时间格式须为 YYYY-MM-DD HH:mm:ss。"),
                var m when m.StartsWith("Aperture", StringComparison.Ordinal) => Loc.T("光圈须为有效正数。"),
                var m when m.StartsWith("Focal length", StringComparison.Ordinal) => Loc.T("焦距须为有效正数。"),
                var m when m.StartsWith("Shutter speed", StringComparison.Ordinal) => Loc.T("快门须为正数或有效分数，例如 1/125。"),
                _ => ex.Message,
            };
            ExifErrorLbl.IsVisible = true;
            FilmInfoDrawer.IsPaneOpen = true;
            Dispatcher.UIThread.Post(() => ExifErrorLbl.BringIntoView(), DispatcherPriority.Loaded);
            return;
        }
        // Remember on confirm, not on every keystroke: a dialog the user cancelled should not
        // have changed anything.
        Settings.Current.Export = Options.Clone();
        foreach (var film in Options.FrameFilmCaptures
            ?? (Options.FilmCapture is { } single ? new[] { single } : Array.Empty<FilmCaptureMetadata>()))
        {
            CaptureDeviceLibrary.Remember(Settings.Current.FilmCameras, film.Camera);
            CaptureDeviceLibrary.Remember(Settings.Current.FilmLenses, film.Lens);
        }
        Settings.Save();
        Close(true);
    }
}
