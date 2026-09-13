using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Contact-sheet preview. The roll is processed to thumbnails first and shown here for approval;
/// only then does the caller ask for a filename. Processing costs a pass over the whole roll, so
/// committing to a save path before seeing the result is the wrong way round — and a bad frame is
/// far easier to spot on the sheet than in the film strip.
///
/// This is also where the roll's identification fields and the sheet's light/dark look are set,
/// because both are printed into the exported image. The preview re-composes as you type.
///
/// Dialog result: 导出印样… → true; 关闭/Esc → false.
/// </summary>
public partial class ContactSheetDialog : Window
{
    private readonly IReadOnlyList<ImageBuffer> _thumbs = new List<ImageBuffer>();
    private readonly bool _hdrAvailable;
    private RollNotes? _notes;
    private bool _ready;
    private SheetComposer.Grid? _grid;
    private SheetComposer.Options? _gridOpt;

    // The preview composes the whole sheet at a fraction of export width. Every metric in the
    // composer scales with width, so this is the same design, just cheap enough to redo on
    // every keystroke instead of every export.
    private const int PreviewWidth = 1000;

    /// <summary>The look the user settled on — read by the caller to export with.</summary>
    public SheetStyle Style { get; private set; } = Settings.Current.SheetStyle;

    /// <summary>The page proportion the user settled on — likewise read back for the export.</summary>
    public SheetAspect Aspect { get; private set; } = Settings.Current.SheetAspect;

    /// <summary>Which way round that proportion is read.</summary>
    public SheetOrientation Orientation { get; private set; } = Settings.Current.SheetOrientation;

    /// <summary>
    /// Write the HDR sheet (D-031): the frames at the roll's HDR target, the paper at SDR white —
    /// a gain-map JPEG or a float32 TIFF. Its own switch rather than the roll's, because the sheet
    /// is a deliverable of its own: a lab print of an HDR roll is a perfectly good thing to want.
    /// Follows the roll on opening; not remembered, since it is a per-roll fact and not a taste.
    /// The preview here is an SDR surface and shows the SDR sheet either way — the switch changes
    /// what the FILE is.
    /// </summary>
    public bool WriteHdr { get; private set; }

    public ContactSheetDialog() { InitializeComponent(); }

    /// <param name="hdrAvailable">Whether the caller can write an HDR sheet at all — an HDR roll
    /// with its extended cells rendered. Off, the switch is hidden rather than greyed: on an SDR
    /// roll there is nothing to explain.</param>
    public ContactSheetDialog(IReadOnlyList<ImageBuffer> thumbs, RollNotes notes, bool hdrAvailable = false) : this()
    {
        _thumbs = thumbs;
        _notes = notes;
        _hdrAvailable = hdrAvailable;
        DataContext = notes;

        HdrPanel.IsVisible = hdrAvailable;
        WriteHdr = hdrAvailable;
        HdrBox.IsChecked = WriteHdr;

        if (Style == SheetStyle.Light) StyleLight.IsChecked = true; else StyleDark.IsChecked = true;
        AspectBox.SelectedIndex = (int)Aspect;
        if (Orientation == SheetOrientation.Landscape) OrientWide.IsChecked = true;
        else OrientTall.IsChecked = true;
        SyncOrientEnabled();
        _ready = true;

        notes.PropertyChanged += OnNotesChanged;
        Recompose();
    }

    private void OnNotesChanged(object? sender, PropertyChangedEventArgs e) => Recompose();

    private void OnStyleChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Style = StyleLight.IsChecked == true ? SheetStyle.Light : SheetStyle.Dark;
        // Remembered across sessions: which look you print is a standing preference, not a
        // per-roll decision.
        Settings.Current.SheetStyle = Style;
        Settings.Save();
        Recompose();
    }

    private void OnAspectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Aspect = (SheetAspect)Math.Max(0, AspectBox.SelectedIndex);
        Settings.Current.SheetAspect = Aspect;
        Settings.Save();
        SyncOrientEnabled();
        Recompose();
    }

    private void OnOrientChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Orientation = OrientTall.IsChecked == true
            ? SheetOrientation.Portrait : SheetOrientation.Landscape;
        Settings.Current.SheetOrientation = Orientation;
        Settings.Save();
        Recompose();
    }

    private void OnHdrChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        WriteHdr = _hdrAvailable && HdrBox.IsChecked == true;
        // Nothing to recompose: the page is the same, only the file changes. The label says which.
        UpdateInfo();
    }

    /// <summary>A square page is the same page either way round, so the choice is greyed there
    /// rather than left live and inert. The stored preference is untouched — switching back to
    /// 4:3 restores whichever way round the user last wanted it.</summary>
    private void SyncOrientEnabled() => OrientPanel.IsEnabled = Aspect != SheetAspect.Square;

    private void Recompose()
    {
        if (_notes is null || _thumbs.Count == 0) return;

        var opt = new SheetComposer.Options
        {
            Style = Style, Aspect = Aspect, Orientation = Orientation,
        };

        // The grid only depends on the style (its gaps carry the paper colour) and the page
        // proportion (which decides the columns), never on the notes — so typing must not send
        // the thumbnails back through a resize pass.
        if (_grid is null || _gridOpt != opt)
        {
            _grid = SheetComposer.BuildGrid(_thumbs, PreviewWidth, opt);
            _gridOpt = opt;
        }

        var old = Disp.Source as RenderTargetBitmap;
        Disp.Source = SheetComposer.Compose(_grid, _notes, opt);
        old?.Dispose();
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_grid is null || _gridOpt is null) return;
        // Report the size the export will be, not the preview's — planning only, no pixels.
        Avalonia.PixelSize size = SheetComposer.SizeFor(_thumbs, 2048, _gridOpt);
        string text = Loc.F(
            $"{_thumbs.Count} 帧 · {_grid.Layout.Cols}×{_grid.Layout.Rows} · 导出 {size.Width}×{size.Height}");
        if (WriteHdr) text += " · HDR";
        InfoLbl.Text = text;
    }

    protected override void OnClosed(System.EventArgs e)
    {
        if (_notes is not null) _notes.PropertyChanged -= OnNotesChanged;
        (Disp.Source as RenderTargetBitmap)?.Dispose();
        base.OnClosed(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(true);
}
