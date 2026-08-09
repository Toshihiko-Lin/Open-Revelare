using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Confirm how each scanned strip is cut into frames, before the roll reaches the main window.
///
/// The import runs this to completion first and hands the main window a finished frame list, so
/// there is never a half-split roll to reason about downstream.
///
/// Editing is done on DIVIDERS, not on four-cornered boxes, because that matches both the film
/// and the failure mode. Frames on a strip share their edges and are evenly pitched, so one
/// number per boundary describes the whole strip and makes overlaps and gaps unrepresentable.
/// And detection, when it is wrong, is wrong by a divider — it reports one frame as two when a
/// blown highlight inside the picture reads like bare film base. That costs one double-click
/// here; the same fix on independent boxes would mean editing two of them in agreement.
/// </summary>
public partial class SplitDialog : Window
{
    private const double HitTolerance = 6.0;   // px, for grabbing a divider

    public ObservableCollection<StripPlan> Plans { get; } = new();

    /// <summary>Confirmed plans; null while the dialog is open or if it was cancelled.</summary>
    public IReadOnlyList<StripPlan>? Result { get; private set; }

    /// <summary>
    /// Slack the region decode keeps around each frame, as a fraction of the frame's own size on
    /// every side. Read by the caller after the dialog closes.
    ///
    /// It belongs to this dialog because it belongs to the SPLIT: it is the answer to "how sure am
    /// I about these dividers", which is exactly the question the user is answering on this screen
    /// and nowhere else. Decided here, it is also decided BEFORE the decode that consumes it, so
    /// no frame is ever decoded against a margin the user has already changed their mind about.
    ///
    /// One value for the whole import rather than per strip: the dividers a user distrusts are the
    /// ones the detector guessed on, and that is a property of the batch (a low-contrast roll, a
    /// scanner that leaves bright bands) far more often than of one strip among them.
    /// </summary>
    /// <remarks>Deliberately NOT called Margin: <see cref="Layoutable.Margin"/> already means the
    /// control's own layout inset, and shadowing it would be resolvable from XAML.</remarks>
    public double SplitMargin => MarginSlider.Value;

    private StripPlan? _current;
    private Rect _imageRect;    // where the preview is drawn inside the stage

    /// <summary>
    /// What a drag is moving. The strip's four outer edges are grabbable as well as the interior
    /// dividers — they are ordinary measurements that detection can get wrong, and until they were
    /// draggable a box clipping the photograph was simply unfixable here.
    /// </summary>
    private enum Grab
    {
        None,
        Divider,    // interior boundary, _dragIndex says which
        FirstEnd,   // the strip's start along its length
        LastEnd,    // the strip's end along its length
        SideLo,     // the frames' shared low side edge, across the strip
        SideHi,     // ... and the high one
    }

    private Grab _grab = Grab.None;
    private int _dragIndex = -1;

    public SplitDialog()
    {
        InitializeComponent();
        FileList.ItemsSource = Plans;
        UpdateMarginLabel();
        // The preview is letterboxed into the stage, so the layout has to be redone whenever
        // the stage resizes — including the first time it is measured, which happens after
        // the constructor and would otherwise leave the canvas empty until the first click.
        Stage.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == BoundsProperty) Redraw();
        };
    }

    public SplitDialog(IEnumerable<StripPlan> plans) : this()
    {
        foreach (StripPlan p in plans) Plans.Add(p);
        if (Plans.Count > 0) FileList.SelectedIndex = 0;
        UpdateTotal();
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        _current = FileList.SelectedItem as StripPlan;
        SkipChk.IsChecked = _current?.Skipped ?? false;
        Redraw();
    }

    // ── editing ───────────────────────────────────────────────────────────────────

    private void OnFewerClick(object? sender, RoutedEventArgs e) => Step(-1);
    private void OnMoreClick(object? sender, RoutedEventArgs e) => Step(+1);

    private void Step(int delta)
    {
        if (_current is null || _current.Skipped) return;
        _current.SetFrameCount(_current.FrameCount + delta);
        AfterEdit();
    }

    private void OnSkipToggled(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _current.Skipped = SkipChk.IsChecked == true;
        AfterEdit();
    }

    private void OnSkipAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (StripPlan p in Plans) p.Skipped = true;
        SkipChk.IsChecked = true;
        AfterEdit();
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_current is null || _current.Skipped || _imageRect.Width <= 0) return;
        Point pt = e.GetPosition(Stage);
        double pos = ToNormalised(pt);

        if (e.ClickCount >= 2)
        {
            // Double-click ON a divider removes it, anywhere else adds one. Deleting is the
            // common repair (a frame reported as two), so it gets the easier target.
            int hit = NearestDivider(pt, out double distPx);
            if (hit > 0 && distPx <= HitTolerance * 2) { _current.RemoveDividerNear(_current.Edges[hit]); }
            // ... but not on an outer edge. Those cannot be added or removed, only moved, so a
            // double-click there is someone missing the edge they meant to drag — dropping a new
            // divider a hair inside the strip's end is never what they wanted.
            else if (HitTest(pt).What != Grab.None) return;
            else _current.AddDivider(pos);
            AfterEdit();
            return;
        }

        (_grab, _dragIndex) = HitTest(pt);
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_current is null) return;
        Point pt = e.GetPosition(Stage);

        if (_grab == Grab.None)
        {
            // Cursor feedback so the (thin) edges are discoverable — including the outer ones,
            // which look like a static frame outline and would otherwise never invite a drag.
            var (what, _) = HitTest(pt);
            Cursor = new Cursor(what switch
            {
                Grab.None => StandardCursorType.Arrow,
                Grab.SideLo or Grab.SideHi =>
                    _current.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth,
                _ => _current.Vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast,
            });
            return;
        }

        switch (_grab)
        {
            case Grab.Divider: _current.MoveDivider(_dragIndex, ToNormalised(pt)); break;
            case Grab.FirstEnd: _current.MoveEnd(last: false, ToNormalised(pt)); break;
            case Grab.LastEnd: _current.MoveEnd(last: true, ToNormalised(pt)); break;
            case Grab.SideLo: _current.MoveSide(high: false, ToCross(pt)); break;
            case Grab.SideHi: _current.MoveSide(high: true, ToCross(pt)); break;
        }
        Redraw();
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_grab == Grab.None) return;
        _grab = Grab.None;
        _dragIndex = -1;
        AfterEdit();
    }

    /// <summary>
    /// What lies under the pointer, nearest first.
    ///
    /// Interior dividers win ties against the outer edges: they are the ones the user came to
    /// adjust, they are far more numerous, and where an end divider sits close to the strip's end
    /// grabbing the wrong one silently reshapes a frame.
    /// </summary>
    private (Grab What, int Index) HitTest(Point pt)
    {
        if (_current is null || _current.Skipped || _imageRect.Width <= 0) return (Grab.None, -1);

        int div = NearestDivider(pt, out double dd);
        if (div > 0 && dd <= HitTolerance) return (Grab.Divider, div);

        double along = _current.Vertical ? pt.Y : pt.X;
        double aOrigin = _current.Vertical ? _imageRect.Y : _imageRect.X;
        double aExtent = _current.Vertical ? _imageRect.Height : _imageRect.Width;
        double cross = _current.Vertical ? pt.X : pt.Y;
        double cOrigin = _current.Vertical ? _imageRect.X : _imageRect.Y;
        double cExtent = _current.Vertical ? _imageRect.Width : _imageRect.Height;

        var cands = new (Grab What, double Px, double Value)[]
        {
            (Grab.FirstEnd, aOrigin + _current.Edges[0] * aExtent, along),
            (Grab.LastEnd, aOrigin + _current.Edges[^1] * aExtent, along),
            (Grab.SideLo, cOrigin + _current.CrossLo * cExtent, cross),
            (Grab.SideHi, cOrigin + _current.CrossHi * cExtent, cross),
        };

        var best = (What: Grab.None, D: double.MaxValue);
        foreach (var (what, px, v) in cands)
        {
            double d = Math.Abs(px - v);
            if (d < best.D) best = (what, d);
        }
        return best.D <= HitTolerance ? (best.What, -1) : (Grab.None, -1);
    }

    /// <summary>Pointer position → position ACROSS the strip, in [0,1] of the source image.</summary>
    private double ToCross(Point pt)
    {
        if (_current is null || _imageRect.Width <= 0) return 0;
        return _current.Vertical
            ? Math.Clamp((pt.X - _imageRect.X) / _imageRect.Width, 0, 1)
            : Math.Clamp((pt.Y - _imageRect.Y) / _imageRect.Height, 0, 1);
    }

    private void AfterEdit()
    {
        Redraw();
        UpdateTotal();
    }

    // ── geometry ──────────────────────────────────────────────────────────────────

    /// <summary>Pointer position → position along the strip, in [0,1] of the source image.</summary>
    private double ToNormalised(Point pt)
    {
        if (_current is null || _imageRect.Width <= 0) return 0;
        return _current.Vertical
            ? Math.Clamp((pt.Y - _imageRect.Y) / _imageRect.Height, 0, 1)
            : Math.Clamp((pt.X - _imageRect.X) / _imageRect.Width, 0, 1);
    }

    /// <summary>Index of the divider nearest the pointer, with its distance in pixels.
    /// Returns 0 (never a draggable divider — that is the strip's own end) when there is none.</summary>
    private int NearestDivider(Point pt, out double distancePx)
    {
        distancePx = double.MaxValue;
        if (_current is null || _imageRect.Width <= 0) return 0;

        double along = _current.Vertical ? pt.Y : pt.X;
        double origin = _current.Vertical ? _imageRect.Y : _imageRect.X;
        double extent = _current.Vertical ? _imageRect.Height : _imageRect.Width;

        int best = 0;
        for (int i = 1; i < _current.Edges.Count - 1; i++)
        {
            double px = origin + _current.Edges[i] * extent;
            double d = Math.Abs(px - along);
            if (d < distancePx) { distancePx = d; best = i; }
        }
        return best;
    }

    // ── drawing ───────────────────────────────────────────────────────────────────

    private void Redraw()
    {
        Stage.Children.Clear();
        if (_current is null) { UpdateCountLabel(); return; }

        Bitmap? bmp = _current.Preview;
        double stageW = Stage.Bounds.Width, stageH = Stage.Bounds.Height;
        if (bmp is null || stageW <= 1 || stageH <= 1) { UpdateCountLabel(); return; }

        // Letterbox the preview into the stage, preserving aspect.
        double scale = Math.Min(stageW / bmp.PixelSize.Width, stageH / bmp.PixelSize.Height);
        double w = bmp.PixelSize.Width * scale, h = bmp.PixelSize.Height * scale;
        _imageRect = new Rect((stageW - w) / 2, (stageH - h) / 2, w, h);

        var img = new Image { Source = bmp, Width = w, Height = h };
        Canvas.SetLeft(img, _imageRect.X);
        Canvas.SetTop(img, _imageRect.Y);
        Stage.Children.Add(img);

        if (_current.Skipped)
        {
            HintLbl.Text = Loc.T("这条整张导入，不分割。");
            UpdateCountLabel();
            return;
        }

        DrawMarginBoxes();   // under the frame outlines, so the solid edge stays readable
        DrawFrameBoxes();
        DrawDividers();
        DrawEndHandles();

        HintLbl.Text = _current.IsFallback
            ? Loc.T("未能自动识别这条的画幅边界，下面是等分的猜测——请手动调整。")
            : Loc.F($"识别到 {_current.FrameCount} 格。");
        UpdateCountLabel();
    }

    /// <summary>One outline per frame, so the user sees what will be imported.</summary>
    private void DrawFrameBoxes()
    {
        if (_current is null) return;
        var stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xD5, 0x98));
        for (int i = 0; i < _current.Edges.Count - 1; i++)
        {
            double lo = _current.Edges[i], hi = _current.Edges[i + 1];
            Rect r = _current.Vertical
                ? new Rect(_imageRect.X + _current.CrossLo * _imageRect.Width,
                           _imageRect.Y + lo * _imageRect.Height,
                           (_current.CrossHi - _current.CrossLo) * _imageRect.Width,
                           (hi - lo) * _imageRect.Height)
                : new Rect(_imageRect.X + lo * _imageRect.Width,
                           _imageRect.Y + _current.CrossLo * _imageRect.Height,
                           (hi - lo) * _imageRect.Width,
                           (_current.CrossHi - _current.CrossLo) * _imageRect.Height);

            var box = new Border
            {
                Width = Math.Max(1, r.Width - 2),
                Height = Math.Max(1, r.Height - 2),
                BorderBrush = stroke,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, r.X + 1);
            Canvas.SetTop(box, r.Y + 1);
            Stage.Children.Add(box);
        }
    }

    /// <summary>
    /// Short tick marks on the strip's four outer edges, at their midpoints.
    ///
    /// The frame outlines already draw those edges, but as a plain rectangle they read as a static
    /// annotation — nothing distinguishes them from a label, and a user who can see the box cutting
    /// into their photograph will not think to try dragging it. The ticks borrow the dividers'
    /// colour, which is the dialog's existing vocabulary for "this one moves".
    /// </summary>
    private void DrawEndHandles()
    {
        if (_current is null) return;
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        double aOrigin = _current.Vertical ? _imageRect.Y : _imageRect.X;
        double aExtent = _current.Vertical ? _imageRect.Height : _imageRect.Width;
        double cOrigin = _current.Vertical ? _imageRect.X : _imageRect.Y;
        double cExtent = _current.Vertical ? _imageRect.Width : _imageRect.Height;

        // Centre of the strip on each axis, so a tick sits in the middle of the edge it marks.
        double aMid = aOrigin + (_current.Edges[0] + _current.Edges[^1]) / 2 * aExtent;
        double cMid = cOrigin + (_current.CrossLo + _current.CrossHi) / 2 * cExtent;
        const double len = 22, thick = 3;

        void Tick(double alongPx, double crossPx, bool acrossStrip)
        {
            // acrossStrip: the tick lies along the strip's short axis (marks an END);
            // otherwise it lies along its length (marks a SIDE).
            bool horizontalBar = _current!.Vertical ? acrossStrip : !acrossStrip;
            var bar = new Border
            {
                Background = brush,
                Width = horizontalBar ? len : thick,
                Height = horizontalBar ? thick : len,
                CornerRadius = new CornerRadius(1.5),
                IsHitTestVisible = false,
            };
            double x = _current.Vertical ? crossPx : alongPx;
            double y = _current.Vertical ? alongPx : crossPx;
            Canvas.SetLeft(bar, x - (horizontalBar ? len : thick) / 2);
            Canvas.SetTop(bar, y - (horizontalBar ? thick : len) / 2);
            Stage.Children.Add(bar);
        }

        Tick(aOrigin + _current.Edges[0] * aExtent, cMid, acrossStrip: true);
        Tick(aOrigin + _current.Edges[^1] * aExtent, cMid, acrossStrip: true);
        Tick(aMid, cOrigin + _current.CrossLo * cExtent, acrossStrip: false);
        Tick(aMid, cOrigin + _current.CrossHi * cExtent, acrossStrip: false);
    }

    /// <summary>The draggable interior dividers, drawn over the boxes.</summary>
    private void DrawDividers()
    {
        if (_current is null) return;
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        for (int i = 1; i < _current.Edges.Count - 1; i++)
        {
            double t = _current.Edges[i];
            var bar = new Border
            {
                Background = brush,
                Width = _current.Vertical ? _imageRect.Width : 3,
                Height = _current.Vertical ? 3 : _imageRect.Height,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(bar, _current.Vertical ? _imageRect.X : _imageRect.X + t * _imageRect.Width - 1.5);
            Canvas.SetTop(bar, _current.Vertical ? _imageRect.Y + t * _imageRect.Height - 1.5 : _imageRect.Y);
            Stage.Children.Add(bar);
        }
    }

    private void OnMarginChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Slider raises PropertyChanged for everything it owns; only Value matters here.
        if (e.Property != RangeBase.ValueProperty) return;
        UpdateMarginLabel();
        Redraw();     // the dashed band follows the number
    }

    /// <summary>
    /// State the cost, not just the setting.
    ///
    /// The margin is a trade — every extra bit of room to drag into is preview resolution the frame
    /// does not get, at exactly 1/(1+2m) of an exact cut. A bare "0.15" hides that; the percentage
    /// is what lets someone choose, and it is why 0 reads as a complete answer rather than as off.
    /// </summary>
    private void UpdateMarginLabel()
    {
        double m = MarginSlider.Value;
        MarginLbl.Text = $"{m * 100:F0}%";
        MarginHintLbl.Text = m <= 0.0005
            ? Loc.T("精确切：预览最清晰，但裁切时无法把边往外拉")
            : Loc.F($"裁切时每边可外扩 {m * 100:F0}%，预览分辨率为精确切的 {100.0 / (1 + 2 * m):F0}%");
    }

    /// <summary>
    /// The margin drawn as a dashed box outside each frame — what the crop tool will have to work
    /// with later, shown at the moment the dividers are being decided.
    ///
    /// Clipped to the image, which is not cosmetic: the outermost frames sit against the file edge
    /// and genuinely get less slack (or none) on that side, and a band drawn past the picture would
    /// promise room that will not exist.
    /// </summary>
    private void DrawMarginBoxes()
    {
        if (_current is null || MarginSlider.Value <= 0.0005) return;
        double m = MarginSlider.Value;
        var stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xD5, 0x98), 0.45);
        for (int i = 0; i < _current.Edges.Count - 1; i++)
        {
            double lo = _current.Edges[i], hi = _current.Edges[i + 1];
            double span = hi - lo, pad = span * m;
            double alo = Math.Max(0.0, lo - pad), ahi = Math.Min(1.0, hi + pad);
            double clo = _current.CrossLo, chi = _current.CrossHi;
            double cpad = (chi - clo) * m;
            double aclo = Math.Max(0.0, clo - cpad), achi = Math.Min(1.0, chi + cpad);

            Rect r = _current.Vertical
                ? new Rect(_imageRect.X + aclo * _imageRect.Width,
                           _imageRect.Y + alo * _imageRect.Height,
                           (achi - aclo) * _imageRect.Width,
                           (ahi - alo) * _imageRect.Height)
                : new Rect(_imageRect.X + alo * _imageRect.Width,
                           _imageRect.Y + aclo * _imageRect.Height,
                           (ahi - alo) * _imageRect.Width,
                           (achi - aclo) * _imageRect.Height);

            // Rectangle, not Border: only a Shape carries StrokeDashArray, and the dashes are what
            // distinguish "room available" from the solid frame edge itself.
            var box = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(1, r.Width),
                Height = Math.Max(1, r.Height),
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeDashArray = new AvaloniaList<double>(4, 3),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            Stage.Children.Add(box);
        }
    }

    private void UpdateCountLabel() => CountLbl.Text = _current is null ? "—" : _current.FrameCount.ToString();

    private void UpdateTotal()
    {
        int frames = Plans.Sum(p => p.FrameCount);
        TotalLbl.Text = Loc.F($"共 {Plans.Count} 个文件 → {frames} 帧");
        // The list shows each plan's count, and those change as the user edits.
        FileList.InvalidateVisual();
    }

    // ── result ────────────────────────────────────────────────────────────────────

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Result = Plans.ToList();
        Close(true);
    }
}
