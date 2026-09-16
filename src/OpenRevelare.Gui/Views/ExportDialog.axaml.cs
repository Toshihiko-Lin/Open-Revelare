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

    /// <summary>What the user confirmed. Only meaningful once ShowDialog returned true.</summary>
    public ExportOptions Options { get; private set; } = new();

    // Avalonia needs a parameterless constructor for XAML tooling.
    public ExportDialog() : this(rollMode: false, ColorPipeline.DefaultOutput, hdrLimitStops: 0d) { }

    /// <param name="rollMode">A whole roll goes to a folder unattended, so it needs a conflict
    /// policy. A single frame goes through a save dialog that already asked, so it does not.</param>
    /// <param name="space">The roll's output space, for the summary and the hint.</param>
    /// <param name="hdrLimitStops">The roll's HDR limit in stops above SDR white, zero when HDR
    /// is off. With it on, TIFF is the float32 master and JPEG is a gain-map JPEG.</param>
    public ExportDialog(bool rollMode, ColorSpaceDef space, double hdrLimitStops)
    {
        _space = space;
        _hdrLimitStops = hdrLimitStops;
        InitializeComponent();

        Title = rollMode ? Loc.T("整卷导出选项") : Loc.T("导出选项");
        OkBtn.Content = rollMode ? Loc.T("选择目录 →") : Loc.T("选择位置 →");
        ConflictGroup.IsVisible = rollMode;

        Load(Settings.Current.Export);
        SyncEnabledState();
    }

    private void Load(ExportOptions saved)
    {
        FmtJpeg.IsChecked = saved.Format == ExportFormat.Jpeg;
        FmtTiff.IsChecked = saved.Format != ExportFormat.Jpeg;
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
        bool exportLinear = LinearChk.IsChecked == true;
        bool jpeg = FmtJpeg.IsChecked == true;
        ExportIccUiState icc = ResolveIcc(jpeg, exportLinear);
        return new ExportOptions
        {
            Format = jpeg ? ExportFormat.Jpeg : ExportFormat.Tiff16,
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
            || HdrBaseGroup is null || FormatHint is null || SizeLimitRow is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        TiffGroup.IsVisible = !jpeg;
        JpegGroup.IsVisible = jpeg;
        if (jpeg && LinearChk.IsChecked == true) LinearChk.IsChecked = false;
        LongEdgeRow.IsEnabled = DownsampleChk.IsChecked == true;
        SizeLimitRow.IsEnabled = SizeLimitChk.IsChecked == true;
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();

        // Scene-linear and every non-exact-sRGB display export require their exact ICC. Resolve
        // the effective value independently of the control so a stale false preset is harmless.
        bool linear = LinearChk.IsChecked == true;
        bool gainMap = IsHdr && jpeg && !linear;
        bool hdrMaster = IsHdr && !jpeg && !linear;
        ExportIccUiState icc = ResolveIcc(jpeg, linear);
        ColorSpaceHint.IsEnabled = !linear;
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

        FormatHint.Text = IsHdr
            ? Loc.F($"HDR 已开（+{_hdrLimitStops:0.0} 档）：TIFF 是 32-bit float 线性母版；JPEG 是增益图 JPEG，任何看图软件都能打开，HDR 屏上还原高光。")
            : Loc.T("TIFF 保留全部层次，适合存档或继续修图；JPEG 适合直接分享。");

        ColorSpaceHint.Text = linear
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
