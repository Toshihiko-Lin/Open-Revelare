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

        Title = rollMode ? "整卷导出选项" : "导出选项";
        OkBtn.Content = rollMode ? "选择目录 →" : "选择位置 →";
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
        EmbedIcc = IccChk.IsChecked == true,
        Downsample = DownsampleChk.IsChecked == true,
        MaxLongEdge = (int)(LongEdgeBox.Value ?? 2048),
        Conflict = ConflictOverwrite.IsChecked == true ? ExportFile.ConflictPolicy.Overwrite
                 : ConflictSkip.IsChecked == true ? ExportFile.ConflictPolicy.Skip
                 : ExportFile.ConflictPolicy.Unique,
    };

    private void SyncEnabledState()
    {
        // Guard: the IsCheckedChanged handlers fire while InitializeComponent is still wiring
        // controls up, before every named field exists.
        if (SummaryLbl is null || TiffGroup is null || JpegGroup is null) return;

        bool jpeg = FmtJpeg.IsChecked == true;
        TiffGroup.IsEnabled = !jpeg;
        JpegGroup.IsEnabled = jpeg;
        LongEdgeRow.IsEnabled = DownsampleChk.IsChecked == true;
        QualityLbl.Text = ((int)QualitySlider.Value).ToString();
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
