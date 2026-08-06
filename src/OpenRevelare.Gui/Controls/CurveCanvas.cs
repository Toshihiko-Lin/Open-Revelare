using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// Interactive tone-curve canvas for ONE channel — port of Python
/// <c>gui/curve_widget.py::_CurveCanvas</c>. Left-click empty space adds a point,
/// left-drag moves it, right-click deletes it. Points are (x,y) in [0,1] with y up.
/// The preview curve uses the same monotone PCHIP the pipeline's ApplyCurves uses,
/// so what you draw is what renders.
/// </summary>
public sealed class CurveCanvas : Control
{
    private const double Pad = 14, HitRadius = 9, CurveSamples = 256;

    private static readonly IBrush COuter = new SolidColorBrush(Color.FromRgb(21, 23, 26));
    private static readonly IBrush CPlot = new SolidColorBrush(Color.FromRgb(35, 38, 42));
    private static readonly IPen PBorder = new Pen(new SolidColorBrush(Color.FromRgb(52, 55, 60)), 1);
    private static readonly IPen PGrid = new Pen(new SolidColorBrush(Color.FromRgb(46, 49, 54)), 1);
    private static readonly IPen PGridMid = new Pen(new SolidColorBrush(Color.FromRgb(58, 62, 68)), 1);

    private List<Point> _points = new();
    private int? _dragIdx, _hoverIdx;
    private float[]? _hist;   // normalised [0,1] backdrop histogram (x = input level)

    public Color CurveColor { get; set; } = Colors.White;

    /// <summary>Normalised [0,1] histogram drawn behind the curve; null = none.</summary>
    public void SetHistogram(float[]? hist) { _hist = hist; InvalidateVisual(); }

    public event EventHandler? PointsChanged;
    public event EventHandler? EditBegan;
    public event EventHandler? EditEnded;

    public CurveCanvas()
    {
        MinHeight = 200;
        Focusable = true;
    }

    /// <summary>Bind the canvas to a channel's point list (shared reference — edits write through).</summary>
    public void SetPoints(List<Point> pts)
    {
        _points = pts;
        _dragIdx = _hoverIdx = null;
        InvalidateVisual();
    }

    public void Reset()
    {
        _points.Clear();
        _dragIdx = _hoverIdx = null;
        PointsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ── coordinate helpers ─────────────────────────────────────────────────────
    private Rect Plot()
    {
        double m = Pad;
        return new Rect(m, m, Math.Max(1, Bounds.Width - 2 * m), Math.Max(1, Bounds.Height - 2 * m));
    }

    private Point ToWidget(double ux, double uy)
    {
        Rect r = Plot();
        return new Point(r.Left + ux * r.Width, r.Bottom - uy * r.Height);
    }

    private (double X, double Y) ToUnit(Point p)
    {
        Rect r = Plot();
        double x = Math.Clamp((p.X - r.Left) / r.Width, 0.0, 1.0);
        double y = Math.Clamp((r.Bottom - p.Y) / r.Height, 0.0, 1.0);
        return (x, y);
    }

    private int? Hit(Point pos)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            Point wp = ToWidget(_points[i].X, _points[i].Y);
            double dx = pos.X - wp.X, dy = pos.Y - wp.Y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius) return i;
        }
        return null;
    }

    // ── interaction ────────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Point pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (Hit(pos) is int di)
            {
                _points.RemoveAt(di);
                _dragIdx = null;
                PointsChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }
        // Left button
        if (Hit(pos) is int idx)
        {
            _dragIdx = idx;
            EditBegan?.Invoke(this, EventArgs.Empty);
        }
        else if (Plot().Contains(pos))
        {
            var (ux, uy) = ToUnit(pos);
            _points.Add(new Point(ux, uy));
            _dragIdx = _points.Count - 1;
            EditBegan?.Invoke(this, EventArgs.Empty);
            PointsChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Point pos = e.GetPosition(this);
        if (_dragIdx is int di)
        {
            var (ux, uy) = ToUnit(pos);
            _points[di] = new Point(ux, uy);
            PointsChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
        else
        {
            int? hit = Hit(pos);
            if (hit != _hoverIdx)
            {
                _hoverIdx = hit;
                Cursor = new Cursor(hit is not null ? StandardCursorType.Hand : StandardCursorType.Arrow);
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_dragIdx is not null)
        {
            _dragIdx = null;
            EditEnded?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    // ── rendering ──────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        Rect r = Plot();
        ctx.FillRectangle(COuter, new Rect(0, 0, Bounds.Width, Bounds.Height));
        ctx.DrawRectangle(CPlot, null, r, 4, 4);

        // Histogram backdrop (behind grid), filled silhouette.
        if (_hist is { Length: > 1 } h)
        {
            var histGeo = new StreamGeometry();
            using (var gc = histGeo.Open())
            {
                gc.BeginFigure(new Point(r.Left, r.Bottom), true);
                for (int i = 0; i < h.Length; i++)
                {
                    double x = r.Left + (double)i / (h.Length - 1) * r.Width;
                    double y = r.Bottom - Math.Clamp(h[i], 0.0, 1.0) * r.Height * 0.92;
                    gc.LineTo(new Point(x, y));
                }
                gc.LineTo(new Point(r.Right, r.Bottom));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(140, 146, 152), 0.22), null, histGeo);
        }

        // Grid (quarter divisions, brighter centre cross).
        for (int i = 1; i < 4; i++)
        {
            double frac = i / 4.0;
            IPen pen = i == 2 ? PGridMid : PGrid;
            double x = r.Left + frac * r.Width, y = r.Top + frac * r.Height;
            ctx.DrawLine(pen, new Point(x, r.Top), new Point(x, r.Bottom));
            ctx.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
        }

        // Identity diagonal (dashed).
        var diagPen = new Pen(new SolidColorBrush(Color.FromRgb(90, 95, 102)), 1)
        { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        ctx.DrawLine(diagPen, ToWidget(0, 0), ToWidget(1, 1));

        // Curve (glow underlay + crisp stroke).
        var geo = BuildCurveGeometry();
        var glow = new Pen(new SolidColorBrush(CurveColor, 0.18), 5);
        var stroke = new Pen(new SolidColorBrush(CurveColor), 1.8);
        ctx.DrawGeometry(null, glow, geo);
        ctx.DrawGeometry(null, stroke, geo);

        // Control points (ring; hover/drag emphasis).
        var ringFill = CPlot;
        var chanBrush = new SolidColorBrush(CurveColor);
        var innerPen = new Pen(new SolidColorBrush(Color.FromRgb(20, 22, 24)), 1.5);
        for (int i = 0; i < _points.Count; i++)
        {
            Point wp = ToWidget(_points[i].X, _points[i].Y);
            bool active = i == _dragIdx || i == _hoverIdx;
            if (active)
            {
                ctx.DrawEllipse(new SolidColorBrush(CurveColor, 0.24), null, wp, 9, 9);
                ctx.DrawEllipse(chanBrush, innerPen, wp, 5.5, 5.5);
            }
            else
            {
                ctx.DrawEllipse(ringFill, new Pen(chanBrush, 2), wp, 4.5, 4.5);
            }
        }

        ctx.DrawRectangle(null, PBorder, r, 4, 4);
    }

    private StreamGeometry BuildCurveGeometry()
    {
        var geo = new StreamGeometry();
        using var gc = geo.Open();

        // Anchor + sanitise to strictly-increasing x (matches Stage2.BuildLut).
        var ordered = _points.OrderBy(p => p.X).ToList();
        var xs = new List<double>();
        var ys = new List<double>();
        if (ordered.Count == 0 || ordered[0].X > 0.0) { xs.Add(0); ys.Add(0); }
        foreach (var p in ordered)
        {
            if (xs.Count > 0 && p.X <= xs[^1] + 1e-6) continue; // drop duplicate/backward x
            xs.Add(p.X); ys.Add(p.Y);
        }
        if (xs[^1] < 1.0) { xs.Add(1); ys.Add(1); }

        bool first = true;
        if (xs.Count >= 2)
        {
            var pchip = new Pchip(xs.ToArray(), ys.ToArray());
            for (int i = 0; i < CurveSamples; i++)
            {
                double t = i / (CurveSamples - 1);
                double v = Math.Clamp(pchip.Eval(t), 0.0, 1.0);
                Point wp = ToWidget(t, v);
                if (first) { gc.BeginFigure(wp, false); first = false; }
                else gc.LineTo(wp);
            }
        }
        else
        {
            gc.BeginFigure(ToWidget(0, 0), false);
            gc.LineTo(ToWidget(1, 1));
        }
        gc.EndFigure(false);
        return geo;
    }
}
