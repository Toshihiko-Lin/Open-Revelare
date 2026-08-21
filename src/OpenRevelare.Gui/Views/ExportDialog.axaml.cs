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
/// Options that do not apply to the current format are DISABLED, not hidden: a control that
/// disappears reads as a feature the app lacks, while a greyed one reads as "not for this
/// format", which is what is actually true.
///
/// The colour space is NOT chosen here any more. It is a render parameter now — Stage 2 runs
/// inside it — so it lives in the main window next to the other things that change the picture,
/// and the file simply inherits what is already on screen. This dialog reports it so the summary
/// still says what will be written.
/// </summary>
public partial class ExportDialog : Window
{
    private readonly bool _rollMode;

    /// <summary>The roll's output space — reported, not chosen.</summary>
    private readonly ColorSpaceDef _space;

    /// <summary>What each space is for, in the hint under the label.</summary>
    private static string HintFor(ColorSpaceDef s) => s.Name switch
    {
        "Rec709" => Loc.T("标准 Cineon 流程的第 4 步目标，Gamma 2.4。色域与 sRGB 相同，反差略高。"),
        "sRGB" => Loc.T("网页与大多数屏幕的通用选择。不确定就选它。"),
        "AdobeRGB" => Loc.T("色域比 sRGB 宽，青绿方向尤其明显，适合送印刷或继续修图。在不做色彩管理的软件里看会偏淡。"),
        // The export dialog describes the FILE, which really does carry the full gamut — unlike the
        // preview, whose sRGB ceiling is stated on the picker instead.
        "DisplayP3" => Loc.T("现代屏幕（Apple 设备、多数新款显示器）的宽色域，编码曲线与 sRGB 相同。"),
        _ => "",
    };

    /// <summary>What the user confirmed. Only meaningful once ShowDialog returned true.</summary>
    public ExportOptions Options { get; private set; } = new();

    // Avalonia needs a parameterless constructor for XAML tooling.
    public ExportDialog() : this(rollMode: false, ColorPipeline.DefaultOutput) { }

    /// <param name="rollMode">A whole roll goes to a folder unattended, so it needs a conflict
    /// policy. A single frame goes through a save dialog that already asked, so it does not.</param>
    /// <param name="space">The roll's output space, for the summary and the hint.</param>
    public ExportDialog(bool rollMode, ColorSpaceDef space)
    {
        _rollMode = rollMode;
        _space = space;
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

        IccChk.IsChecked = saved.EmbedIcc;
        DownsampleChk.IsChecked = saved.Downsample;
        LongEdgeBox.Value = Math.Clamp(saved.MaxLongEdge, 256, 20000);
        ConflictOverwrite.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Overwrite;
        ConflictSkip.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Skip;
        ConflictUnique.IsChecked = saved.Conflict == ExportFile.ConflictPolicy.Unique;
    }

    private ExportOptions Collect() => new()
    {
        Format = FmtJpeg.IsChecked == true ? ExportFormat.Jpeg : ExportFormat.Tiff16,
        TiffCompression = CompressBox.SelectedIndex switch
        {
            1 => TiffIO.CompressionMode.Deflate,
            2 => TiffIO.CompressionMode.None,
            _ => TiffIO.CompressionMode.Lzw,
        },
        JpegQuality = (int)QualitySlider.Value,
        // Carried, not chosen: the render already landed in this space.
        ColorSpace = _space.Name,
        ExportLinear = LinearChk.IsChecked == true,
        EmbedIcc = IccChk.IsChecked == true,
        Downsample = DownsampleChk.IsChecked == true,
        MaxLongEdge = (int)(LongEdgeBox.Value ?? 2048),
        Conflict = ConflictOverwrite.IsChecked == true ? ExportFile.ConflictPolicy.Overwrite
                 : ConflictSkip.IsChecked == true ? ExportFile.ConflictPolicy.Skip
                 : ExportFile.ConflictPolicy.Unique,
    };

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
            || ColorSpaceHint is null || LinearChk is null || IccHint is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        TiffGroup.IsEnabled = !jpeg;
        JpegGroup.IsEnabled = jpeg;
        LongEdgeRow.IsEnabled = DownsampleChk.IsChecked == true;
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();

        // A scene-linear export has no output space and no profile that describes it, so both the
        // space it would have gone to and the embed option stop applying. Disabled rather than
        // hidden: they read as "not for this kind of file", which is what is true.
        bool linear = LinearChk.IsChecked == true;
        ColorSpaceHint.IsEnabled = !linear;
        IccChk.IsEnabled = !linear;

        ColorSpaceHint.Text = linear
            ? Loc.T("场景线性导出不经过第 4 步，因此没有输出色彩空间。")
            : Loc.F($"{DisplayName(_space)}——在主窗口选定，导出即所见。{HintFor(_space)}");

        IccHint.Text = linear
            ? Loc.T("场景线性数据没有对应的显示配置文件，贴任何标签都会误导下游，故不嵌入。")
            : Loc.T("嵌入的配置文件与实际写入的像素一致。");

        SummaryLbl.Text = Collect().Summary();
    }
    private void OnFormatChanged(object? sender, RoutedEventArgs e) => SyncEnabledState();
    private void OnDownsampleToggled(object? sender, RoutedEventArgs e) => SyncEnabledState();
    private void OnAnyChanged(object? sender, RoutedEventArgs e) => SyncEnabledState();
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
