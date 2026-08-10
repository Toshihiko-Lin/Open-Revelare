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
/// </summary>
public partial class ExportDialog : Window
{
    private readonly bool _rollMode;

    /// <summary>
    /// The destination spaces offered, in the order they appear. Not every registered space is
    /// here: ACEScg is a working space, and exporting scene-linear ACEScg from a dialog whose
    /// other options assume a display-referred file would mostly produce files people cannot use.
    /// </summary>
    private static readonly ColorSpaceDef[] Spaces =
    {
        ColorSpaces.Srgb,
        ColorSpaces.AdobeRgb,
        ColorSpaces.DisplayP3,
        ColorSpaces.KodakEnduraPremier,
        ColorSpaces.Kodak2383,
    };

    /// <summary>What each space is for, in the hint under the picker.</summary>
    private static string HintFor(ColorSpaceDef s) => s.Name switch
    {
        "sRGB" => Loc.T("网页与大多数屏幕的通用选择。不确定就选它。"),
        "AdobeRGB" => Loc.T("色域比 sRGB 宽，青绿方向尤其明显，适合送印刷或继续修图。在不做色彩管理的软件里看会偏淡。"),
        "DisplayP3" => Loc.T("现代屏幕（Apple 设备、多数新款显示器）的宽色域，编码曲线与 sRGB 相同。"),
        "KodakEnduraPremier" => Loc.T("柯达 Endura Premier 相纸色域——渲染成暗房放大照片的观感。这是「相纸味」的正确做法：走真实的色域变换，而不是把饱和度整体拧大。"),
        "Kodak2383" => Loc.T("柯达 2383 拷贝片色域——院线放映的观感，ECN-2 电影负片的对口选择。"),
        _ => "",
    };

    /// <summary>What the user confirmed. Only meaningful once ShowDialog returned true.</summary>
    public ExportOptions Options { get; private set; } = new();

    // Avalonia needs a parameterless constructor for XAML tooling.
    public ExportDialog() : this(rollMode: false) { }

    /// <param name="rollMode">A whole roll goes to a folder unattended, so it needs a conflict
    /// policy. A single frame goes through a save dialog that already asked, so it does not.</param>
    public ExportDialog(bool rollMode)
    {
        _rollMode = rollMode;
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

        ColorSpaceBox.ItemsSource = Spaces.Select(DisplayName).ToList();
        int spaceIdx = Array.FindIndex(Spaces,
            s => s.Name.Equals(saved.ColorSpace, StringComparison.OrdinalIgnoreCase));
        ColorSpaceBox.SelectedIndex = spaceIdx >= 0 ? spaceIdx : 0;
        GamutBox.SelectedIndex = saved.GamutMapping == GamutMapping.Clip ? 1 : 0;

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
        ColorSpace = Spaces[Math.Clamp(ColorSpaceBox.SelectedIndex, 0, Spaces.Length - 1)].Name,
        GamutMapping = GamutBox.SelectedIndex == 1 ? GamutMapping.Clip : GamutMapping.Desaturate,
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
        "KodakEnduraPremier" => Loc.T("Kodak Endura Premier（相纸）"),
        "Kodak2383" => Loc.T("Kodak 2383（拷贝片）"),
        _ => s.Name,
    };

    private void SyncEnabledState()
    {
        // Guard: the IsCheckedChanged handlers fire while InitializeComponent is still wiring
        // controls up, before every named field exists.
        if (SummaryLbl is null || TiffGroup is null || JpegGroup is null
            || ColorSpaceBox is null || GamutRow is null || ColorSpaceHint is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        TiffGroup.IsEnabled = !jpeg;
        JpegGroup.IsEnabled = jpeg;
        LongEdgeRow.IsEnabled = DownsampleChk.IsChecked == true;
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();

        ColorSpaceDef space = Spaces[Math.Clamp(ColorSpaceBox.SelectedIndex, 0, Spaces.Length - 1)];
        ColorSpaceHint.Text = HintFor(space);

        // The mapper only has work to do when the destination is NARROWER than the source
        // somewhere. Every space offered here is wider than sRGB — which is what the render
        // arrives in — so as things stand nothing ever falls outside and the choice is inert.
        // It is disabled rather than removed because it stops being inert the moment the working
        // space becomes ACEScg, which is where this is heading.
        GamutRow.IsEnabled = false;

        SummaryLbl.Text = Collect().Summary();
    }

    private void OnColorSpaceChanged(object? sender, SelectionChangedEventArgs e) => SyncEnabledState();
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
