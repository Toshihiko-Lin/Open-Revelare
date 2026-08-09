using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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

    private StripPlan? _current;
    private int _dragIndex = -1;
    private Rect _imageRect;    // where the preview is drawn inside the stage

    public SplitDialog()
    {
        InitializeComponent();
        FileList.ItemsSource = Plans;
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
            if (hit > 0 && distPx <= HitTolerance * 2) _current.RemoveDividerNear(_current.Edges[hit]);
            else _current.AddDivider(pos);
            AfterEdit();
            return;
        }

        int grab = NearestDivider(pt, out double d);
        if (grab > 0 && d <= HitTolerance) _dragIndex = grab;
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_current is null) return;
        Point pt = e.GetPosition(Stage);

        if (_dragIndex <= 0)
        {
            // Cursor feedback so the (thin) dividers are discoverable.
            int near = NearestDivider(pt, out double d);
            Cursor = new Cursor(near > 0 && d <= HitTolerance
                ? (_current.Vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
                : StandardCursorType.Arrow);
            return;
        }

        _current.MoveDivider(_dragIndex, ToNormalised(pt));
        Redraw();
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragIndex > 0) { _dragIndex = -1; AfterEdit(); }
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

        DrawFrameBoxes();
        DrawDividers();

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
