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

    /// <summary>The three cursors this dialog shows, built once each.
    ///
    /// <see cref="OnStageMoved"/>'s hover branch fires on every pointer-move, and building a
    /// cursor there accumulated one platform cursor per event — the same defect fixed in the main
    /// window's crop overlay, which was crashing on macOS. Three shared instances,
    /// reference-compared before assignment.</summary>
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SizeNsCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeWeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

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

    // ── view state ────────────────────────────────────────────────────────────────
    //
    // Viewing only. The dividers, the strip's extent and every rect this dialog produces stay in
    // the SOURCE image's own axes, exactly as they were: rotating the preview to read it more
    // easily must not silently re-orient what gets imported, which is the main window's business
    // and is stored per frame. So the transform lives here and nowhere else — Redraw applies it
    // when placing the canvas, and pointer input is mapped back through its inverse before any
    // existing hit-testing sees it. That inverse is the whole trick: everything downstream of
    // ToStage/FromStage keeps working in unrotated, unzoomed coordinates and needed no changes.

    /// <summary>Quarter-turns applied to the PREVIEW for viewing, 0–3 clockwise.</summary>
    private int _quarterTurns;

    /// <summary>Zoom factor on top of the fit-to-stage scale. 1 = fit.</summary>
    private double _zoom = 1.0;

    /// <summary>Pan offset in stage pixels, applied after zoom.</summary>
    private Point _pan;

    /// <summary>True while the user is dragging the picture around rather than an edge.</summary>
    private bool _panning;
    private Point _panFrom;

    private const double MinZoom = 1.0, MaxZoom = 8.0;

    /// <summary>True when the preview is shown turned a quarter or three-quarters, so the stage's
    /// width corresponds to the image's height.</summary>
    private bool Swapped => (_quarterTurns & 1) != 0;

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
        // Straight into layout space, once. Everything below — hit-testing, the normalised
        // conversions, the divider maths — is written against unrotated, unzoomed coordinates and
        // stays correct without knowing the view has been turned.
        Point raw = e.GetPosition(Stage);
        Point pt = FromStage(raw);
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

        // Nothing under the pointer and the picture is bigger than the stage: drag to pan. Only
        // when zoomed in — at fit there is nothing off-screen to reach, and swallowing the drag
        // would just make the canvas feel loose.
        if (_grab == Grab.None && _zoom > MinZoom + 1e-9)
        {
            _panning = true;
            _panFrom = raw;
            if (!ReferenceEquals(Cursor, HandCursor)) Cursor = HandCursor;
        }
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_current is null) return;
        Point raw = e.GetPosition(Stage);

        if (_panning)
        {
            // Pan is in stage pixels and is applied after the turn, so the picture follows the
            // pointer directly whatever the rotation.
            _pan = new Point(_pan.X + (raw.X - _panFrom.X), _pan.Y + (raw.Y - _panFrom.Y));
            ClampPan();
            _panFrom = raw;
            Redraw();
            return;
        }

        Point pt = FromStage(raw);

        if (_grab == Grab.None)
        {
            // Cursor feedback so the (thin) edges are discoverable — including the outer ones,
            // which look like a static frame outline and would otherwise never invite a drag.
            var (what, _) = HitTest(pt);
            // The edge moves along the image's axis, but the cursor has to describe the direction
            // it moves ON SCREEN. A quarter turn swaps those, so the arrow must swap with it —
            // otherwise a turned view offers a north-south cursor for an edge that now slides
            // east-west, which reads as the wrong handle entirely.
            bool alongScreenY = _current.Vertical ^ Swapped;
            Cursor want = what switch
            {
                Grab.None => _zoom > MinZoom + 1e-9 ? HandCursor : ArrowCursor,
                Grab.SideLo or Grab.SideHi => alongScreenY ? SizeWeCursor : SizeNsCursor,
                _ => alongScreenY ? SizeNsCursor : SizeWeCursor,
            };
            if (!ReferenceEquals(Cursor, want)) Cursor = want;
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
        if (_panning)
        {
            _panning = false;
            if (!ReferenceEquals(Cursor, ArrowCursor)) Cursor = ArrowCursor;
            return;
        }
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

        // Only within the strip, plus the tolerance as slack at each side. Distance is measured
        // along the strip alone, so without this a divider is grabbable anywhere on its row of
        // the preview — on a multi-strip scan that means clicking a photograph on strip 2 drags
        // strip 1's divider, which is invisible at the time and corrupts a frame the user was
        // not even looking at. It also matches what DrawDividers now draws.
        double cross = _current.Vertical ? pt.X : pt.Y;
        double cOrigin = _current.Vertical ? _imageRect.X : _imageRect.Y;
        double cExtent = _current.Vertical ? _imageRect.Width : _imageRect.Height;
        double cLo = cOrigin + _current.CrossLo * cExtent - HitTolerance;
        double cHi = cOrigin + _current.CrossHi * cExtent + HitTolerance;
        if (cross < cLo || cross > cHi) return 0;

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

    // ── view transform ────────────────────────────────────────────────────────────

    /// <summary>
    /// Centre of the drawn picture on the stage. Rotation turns about this point, so a quarter
    /// turn keeps whatever the user was looking at in the middle instead of swinging it off the
    /// canvas.
    /// </summary>
    private Point StageCentre => new(Stage.Bounds.Width / 2 + _pan.X, Stage.Bounds.Height / 2 + _pan.Y);

    /// <summary>
    /// Map a point from unrotated image-layout space (the space <see cref="_imageRect"/> and every
    /// overlay is computed in) to where it actually lands on the stage.
    /// </summary>
    private Point ToStage(Point p)
    {
        // Offset from the unrotated layout's centre, scaled, turned, then re-centred.
        double cx = Stage.Bounds.Width / 2, cy = Stage.Bounds.Height / 2;
        double dx = (p.X - cx) * _zoom, dy = (p.Y - cy) * _zoom;
        var (rx, ry) = Turn(dx, dy, _quarterTurns);
        return new Point(StageCentre.X + rx, StageCentre.Y + ry);
    }

    /// <summary>Inverse of <see cref="ToStage"/>: stage pixels back to layout space, which is what
    /// all the hit-testing and the normalised conversions expect.</summary>
    private Point FromStage(Point p)
    {
        double dx = p.X - StageCentre.X, dy = p.Y - StageCentre.Y;
        // Undo the turn by turning the rest of the way round.
        var (rx, ry) = Turn(dx, dy, (4 - (_quarterTurns & 3)) & 3);
        double cx = Stage.Bounds.Width / 2, cy = Stage.Bounds.Height / 2;
        return new Point(cx + rx / _zoom, cy + ry / _zoom);
    }

    /// <summary>Rotate an offset by <paramref name="turns"/> quarter-turns clockwise.</summary>
    private static (double X, double Y) Turn(double x, double y, int turns) => (turns & 3) switch
    {
        1 => (-y, x),
        2 => (-x, -y),
        3 => (y, -x),
        _ => (x, y),
    };

    // ── view controls ─────────────────────────────────────────────────────────────

    private void OnRotateLeftClick(object? sender, RoutedEventArgs e) => Rotate(-1);
    private void OnRotateRightClick(object? sender, RoutedEventArgs e) => Rotate(+1);

    private void Rotate(int delta)
    {
        _quarterTurns = (_quarterTurns + delta + 4) & 3;
        // Pan is in stage pixels and means nothing once the picture turns under it; recentring is
        // both simpler to reason about and what the user expects from a rotate button.
        _pan = default;
        Redraw();
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25, null);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25, null);

    /// <summary>Back to fit-and-unrotated — the state the dialog opens in.</summary>
    private void OnZoomResetClick(object? sender, RoutedEventArgs e)
    {
        _zoom = 1.0;
        _pan = default;
        _quarterTurns = 0;
        Redraw();
    }

    /// <summary>
    /// Change zoom, optionally keeping <paramref name="anchor"/> (a stage point) over the same
    /// part of the picture — what makes wheel-zoom feel like it is zooming where you point rather
    /// than always at the middle.
    /// </summary>
    private void SetZoom(double target, Point? anchor)
    {
        double next = Math.Clamp(target, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 1e-9) return;

        if (anchor is { } a)
        {
            // Keep the layout point under the anchor fixed: solve for the pan that maps it back
            // to the same stage position at the new zoom.
            Point before = FromStage(a);
            _zoom = next;
            Point after = ToStage(before);
            _pan = new Point(_pan.X + (a.X - after.X), _pan.Y + (a.Y - after.Y));
        }
        else _zoom = next;

        if (_zoom <= MinZoom + 1e-9) _pan = default;   // fit again: nothing to pan to
        else ClampPan();
        Redraw();
        UpdateZoomLabel();
    }

    /// <summary>
    /// Keep the picture over the viewer.
    ///
    /// Pan is otherwise unbounded, so the strip can be dragged clean off the grey area and left
    /// as an empty canvas with no way back except 复位. The picture is allowed to move only as
    /// far as its own overhang — the part currently outside the stage — so at fit it cannot move
    /// at all and at 8× it can reach every corner, but never past them.
    /// </summary>
    private void ClampPan()
    {
        if (_imageRect.Width <= 0) return;

        // The drawn picture's half-size on each stage axis, after zoom and any quarter turn.
        double halfW = _imageRect.Width * _zoom / 2, halfH = _imageRect.Height * _zoom / 2;
        if (Swapped) (halfW, halfH) = (halfH, halfW);

        // Slack = how far the picture sticks out past the stage on each side. Negative (picture
        // smaller than the stage) means it should stay centred.
        double slackX = Math.Max(0, halfW - Stage.Bounds.Width / 2);
        double slackY = Math.Max(0, halfH - Stage.Bounds.Height / 2);
        _pan = new Point(Math.Clamp(_pan.X, -slackX, slackX), Math.Clamp(_pan.Y, -slackY, slackY));
    }

    private void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_current is null) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(Stage));
        e.Handled = true;
    }

    private void UpdateZoomLabel() => ZoomLbl.Text = $"{_zoom * 100:F0}%";

    // ── drawing ───────────────────────────────────────────────────────────────────

    private void Redraw()
    {
        Scene.Children.Clear();
        if (_current is null) { UpdateCountLabel(); return; }

        Bitmap? bmp = _current.Preview;
        double stageW = Stage.Bounds.Width, stageH = Stage.Bounds.Height;
        if (bmp is null || stageW <= 1 || stageH <= 1) { UpdateCountLabel(); return; }

        // Letterbox the preview into the stage, preserving aspect.
        //
        // Fitted against the stage's axes as the ROTATED picture will use them: a quarter turn
        // swaps which stage dimension bounds which image dimension, and fitting against the
        // unswapped pair would size a turned strip to the wrong axis and let it run off the
        // canvas. Everything after this stays in unrotated layout space — the turn itself is a
        // RenderTransform applied below, so no overlay has to know about it.
        double fitW = Swapped ? stageH : stageW, fitH = Swapped ? stageW : stageH;
        double scale = Math.Min(fitW / bmp.PixelSize.Width, fitH / bmp.PixelSize.Height);
        double w = bmp.PixelSize.Width * scale, h = bmp.PixelSize.Height * scale;
        _imageRect = new Rect((stageW - w) / 2, (stageH - h) / 2, w, h);

        // One transform for the whole overlay stack, about the stage's centre, so the dividers and
        // boxes stay locked to the picture without any of them computing a rotation themselves.
        // On Scene, never on Stage: Stage is what clips, and a control's RenderTransform applies
        // after its own clip, so transforming it would let the zoomed picture spill outside the
        // grey viewer. Scene is sized to Stage so that centring the transform on Scene's own
        // bounds means centring it on the visible area.
        Scene.Width = stageW;
        Scene.Height = stageH;
        Scene.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_zoom, _zoom),
                new RotateTransform(_quarterTurns * 90),
                new TranslateTransform(_pan.X, _pan.Y),
            },
        };
        Scene.RenderTransformOrigin = RelativePoint.Center;

        var img = new Image { Source = bmp, Width = w, Height = h };
        Canvas.SetLeft(img, _imageRect.X);
        Canvas.SetTop(img, _imageRect.Y);
        Scene.Children.Add(img);

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
            Scene.Children.Add(box);
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
            Scene.Children.Add(bar);
        }

        Tick(aOrigin + _current.Edges[0] * aExtent, cMid, acrossStrip: true);
        Tick(aOrigin + _current.Edges[^1] * aExtent, cMid, acrossStrip: true);
        Tick(aMid, cOrigin + _current.CrossLo * cExtent, acrossStrip: false);
        Tick(aMid, cOrigin + _current.CrossHi * cExtent, acrossStrip: false);
    }

    /// <summary>How far a divider sticks out past each side of its strip, in pixels. Enough to
    /// read as a handle that spans the strip rather than as an edge flush with it, and not so
    /// much that it reaches a neighbouring strip on a multi-strip scan.</summary>
    private const double DividerOverhang = 4;

    /// <summary>
    /// The draggable interior dividers, drawn over the boxes.
    ///
    /// Spans the STRIP, not the whole preview — a divider belongs to one piece of film and says
    /// nothing about what is beside it. Drawn full-width it ran clean across the OTHER strips of
    /// a multi-strip scan, laying strip 1's frame boundaries over strip 2's photographs, where
    /// they are both wrong and impossible to attribute to the strip that owns them.
    /// </summary>
    private void DrawDividers()
    {
        if (_current is null) return;
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

        double cOrigin = _current.Vertical ? _imageRect.X : _imageRect.Y;
        double cExtent = _current.Vertical ? _imageRect.Width : _imageRect.Height;
        double cLo = cOrigin + _current.CrossLo * cExtent - DividerOverhang;
        double cLen = (_current.CrossHi - _current.CrossLo) * cExtent + DividerOverhang * 2;

        for (int i = 1; i < _current.Edges.Count - 1; i++)
        {
            double t = _current.Edges[i];
            var bar = new Border
            {
                Background = brush,
                Width = _current.Vertical ? cLen : 3,
                Height = _current.Vertical ? 3 : cLen,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(bar, _current.Vertical ? cLo : _imageRect.X + t * _imageRect.Width - 1.5);
            Canvas.SetTop(bar, _current.Vertical ? _imageRect.Y + t * _imageRect.Height - 1.5 : cLo);
            Scene.Children.Add(bar);
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
            Scene.Children.Add(box);
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
