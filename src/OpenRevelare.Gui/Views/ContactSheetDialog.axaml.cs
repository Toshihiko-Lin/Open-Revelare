using System.Collections.Generic;
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
    private RollNotes? _notes;
    private bool _ready;
    private SheetComposer.Grid? _grid;
    private SheetStyle _gridStyle;

    // The preview composes the whole sheet at a fraction of export width. Every metric in the
    // composer scales with width, so this is the same design, just cheap enough to redo on
    // every keystroke instead of every export.
    private const int PreviewWidth = 1000;

    /// <summary>The look the user settled on — read by the caller to export with.</summary>
    public SheetStyle Style { get; private set; } = Settings.Current.SheetStyle;

    public ContactSheetDialog() { InitializeComponent(); }

    public ContactSheetDialog(IReadOnlyList<ImageBuffer> thumbs, RollNotes notes) : this()
    {
        _thumbs = thumbs;
        _notes = notes;
        DataContext = notes;

        if (Style == SheetStyle.Light) StyleLight.IsChecked = true; else StyleDark.IsChecked = true;
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

    private void Recompose()
    {
        if (_notes is null || _thumbs.Count == 0) return;

        var opt = new SheetComposer.Options { Style = Style };

        // The grid only depends on the style (its gaps carry the paper colour), never on the
        // notes — so typing must not send the thumbnails back through a resize pass.
        if (_grid is null || _gridStyle != Style)
        {
            _grid = SheetComposer.BuildGrid(_thumbs, PreviewWidth, opt);
            _gridStyle = Style;
        }

        var old = Disp.Source as RenderTargetBitmap;
        Disp.Source = SheetComposer.Compose(_grid, _notes, opt);
        old?.Dispose();

        // Report the size the export will be, not the preview's — planning only, no pixels.
        Avalonia.PixelSize size = SheetComposer.SizeFor(_thumbs, 2048);
        InfoLbl.Text = $"{_thumbs.Count} 帧 · 导出 {size.Width}×{size.Height}";
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
