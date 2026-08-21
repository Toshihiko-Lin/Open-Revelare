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

    // Built once and shared. Only reassigned when the hovered point actually changes, so this
    // was never the per-event leak the crop overlay had — but a Cursor still wraps a platform
    // resource freed only by its finalizer, and there is no reason to make new ones.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

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
        // A different channel (or frame) is a different curve: whether ITS ends are materialised
        // is its own business, decided the next time this one is clicked.
        _hasEndpoints = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Give the curve real endpoint handles, so the two ends can be dragged like every other point.
    ///
    /// The ends used to be SYNTHESISED at draw time — <see cref="BuildCurveGeometry"/> prepends
    /// (0,0) and appends (1,1) when the point list does not reach the edges — which drew the right
    /// curve but left nothing for <see cref="Hit"/> to find there. Setting a black or white point
    /// on the curve, the most ordinary thing there is to do with one, was simply impossible: a
    /// click near a corner landed on empty canvas and ADDED a stray point instead.
    ///
    /// Materialising them costs nothing downstream: an explicit (0,0)/(1,1) is exactly what the
    /// synthesiser would have inserted, and both this canvas and <c>Stage2.BuildLut</c> anchor
    /// only when the list does NOT already reach the edge, so a curve with real endpoints and one
    /// with implied ones evaluate identically.
    ///
    /// Idempotent, and only ever called on a list already bound to a channel — a curve the user
    /// has moved off the corners keeps the points it has.
    /// </summary>
    private void EnsureEndpoints()
    {
        // Already has endpoints of its own — leave them exactly where the user put them.
        //
        // This guard is the whole method. Without it, every pointer press re-ran the "does the
        // list reach the edge?" test against a curve whose end had been DRAGGED INWARD, decided it
        // did not, and appended a fresh (1,1) — silently converting the white point the user had
        // just set back into an ordinary interior point. Adjusting one end and then reaching for
        // the other therefore always found the first one undone, and the curve bowed because the
        // re-added corner is a knot. The endpoints must be seeded ONCE, on a curve that has none.
        if (_hasEndpoints) return;

        if (_points.Count == 0)
        {
            _points.Add(new Point(0, 0));
            _points.Add(new Point(1, 1));
        }
        else
        {
            // A legacy curve (interior points only, corners implied): give it the corners it has
            // always been drawn with, so they become grabbable without changing what it renders.
            if (_points[0].X > 0.0) _points.Insert(0, new Point(0, 0));
            if (_points[^1].X < 1.0) _points.Add(new Point(1, 1));
        }
        _hasEndpoints = true;
    }

    /// <summary>
    /// Whether <see cref="_points"/> already carries the two endpoint handles.
    ///
    /// Tracked as state rather than re-derived from the geometry because the two are genuinely
    /// indistinguishable: a last point at (0.8, 1.0) is a dragged white point, but it looks exactly
    /// like an interior point of a curve whose corner is implied. Reset by
    /// <see cref="SetPoints"/> — a different channel's list is a different curve — and set once the
    /// ends have been materialised.
    /// </summary>
    private bool _hasEndpoints;

    /// <summary>True when <paramref name="idx"/> is one of the two endpoint handles.</summary>
    private bool IsEndpoint(int idx) => idx == 0 || idx == _points.Count - 1;

    public void Reset()
    {
        // Back to a bare identity: no interior points and both ends at the corners. Left EMPTY
        // rather than re-seeded with endpoints so the channel still serialises as "no curve" —
        // an empty list is what the params treat as untouched, and a stored (0,0)/(1,1) pair
        // would make every reset frame look edited. The endpoints reappear the moment the canvas
        // is clicked, via EnsureEndpoints.
        _points.Clear();
        _dragIdx = _hoverIdx = null;
        _hasEndpoints = false;   // emptied: the ends are seeded again on the next click
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
        // Both buttons act on the endpoints too, so they have to exist before the hit test.
        EnsureEndpoints();
        if (props.IsRightButtonPressed)
        {
            // Endpoints are not deletable: a curve with no start or end has no domain, and the
            // synthesiser would put them straight back on the next repaint anyway. Right-click
            // RESETS them to the corners instead, which is the useful meaning of "remove" here
            // and the only way back to a neutral curve once an end has been dragged.
            if (Hit(pos) is int di)
            {
                if (IsEndpoint(di))
                {
                    _points[di] = di == 0 ? new Point(0, 0) : new Point(1, 1);
                }
                else _points.RemoveAt(di);
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
            // Insert in x order rather than appending. The list used to be sorted only at draw
            // time, which was enough while every point was interchangeable; now that index 0 and
            // index ^1 MEAN the two endpoints, an out-of-order append would make a mid-curve click
            // masquerade as an endpoint and drag the black point around.
            int at = _points.FindIndex(p => p.X > ux);
            if (at < 0) at = _points.Count;
            _points.Insert(at, new Point(ux, uy));
            _dragIdx = at;
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
            if (IsEndpoint(di))
            {
                // An endpoint owns one axis and is pinned on the other, exactly as Lightroom's
                // curve ends behave: the LEFT end may slide right along the floor (raising the
                // black point — everything below it clips to the shadow value) and may lift off
                // the floor (a matte/faded black), but it may never leave x=0 heading left nor
                // cross the neighbour it must stay behind. The right end mirrors that.
                //
                // Clamping against the INNER neighbour rather than against 0/1 is what keeps the
                // x sequence strictly increasing, which BuildLut requires and silently refuses a
                // curve for when it is violated — the curve would just stop applying.
                const double MinGap = 1e-3;
                if (di == 0)
                {
                    double lim = _points.Count > 1 ? _points[1].X - MinGap : 1.0;
                    _points[0] = new Point(Math.Clamp(ux, 0.0, Math.Max(0.0, lim)), uy);
                }
                else
                {
                    double lim = _points.Count > 1 ? _points[^2].X + MinGap : 0.0;
                    _points[di] = new Point(Math.Clamp(ux, Math.Min(1.0, lim), 1.0), uy);
                }
            }
            else
            {
                // Interior points keep their old freedom, but must not slide past an endpoint —
                // now that the ends are real entries, overtaking one would reorder the list.
                double lo = _points[di - 1].X + 1e-3, hi = _points[di + 1].X - 1e-3;
                _points[di] = new Point(lo <= hi ? Math.Clamp(ux, lo, hi) : ux, uy);
            }
            PointsChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
        else
        {
            int? hit = Hit(pos);
            if (hit != _hoverIdx)
            {
                _hoverIdx = hit;
                Cursor = hit is not null ? HandCursor : ArrowCursor;
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
        // The two ends are drawn as squares so they read as what they are — the black and white
        // points, which behave differently from the round interior points (they are pinned to
        // their edge and cannot be deleted).
        for (int i = 0; i < _points.Count; i++)
        {
            Point wp = ToWidget(_points[i].X, _points[i].Y);
            bool active = i == _dragIdx || i == _hoverIdx;
            bool end = IsEndpoint(i) && _points.Count >= 2;

            if (end)
            {
                double s = active ? 5.5 : 4.5;
                var box = new Rect(wp.X - s, wp.Y - s, s * 2, s * 2);
                if (active)
                {
                    ctx.DrawEllipse(new SolidColorBrush(CurveColor, 0.24), null, wp, 9, 9);
                    ctx.DrawRectangle(chanBrush, innerPen, box, 1.5, 1.5);
                }
                else ctx.DrawRectangle(ringFill, new Pen(chanBrush, 2), box, 1.5, 1.5);
                continue;
            }

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

        // Sanitise to strictly-increasing x, then hold outside the ends — the exact rule
        // Stage2.BuildLut applies to a curve with endpoints, so the drawn curve is the one that
        // renders. NO anchoring: the canvas materialises both ends (EnsureEndpoints) and the
        // params it feeds carry CurveHasEndpoints, so the first and last point are the curve's own
        // black and white point. Anchoring them would add a knot that bends the straight line
        // between two dragged ends — see the note in BuildLut.
        var ordered = _points.OrderBy(p => p.X).ToList();
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var p in ordered)
        {
            if (xs.Count > 0 && p.X <= xs[^1] + 1e-6) continue; // drop duplicate/backward x
            xs.Add(p.X); ys.Add(p.Y);
        }
        // An empty channel is the identity, drawn corner to corner by the fallback below.
        if (xs.Count == 0) { xs.Add(0); ys.Add(0); }

        bool first = true;
        if (xs.Count >= 2)
        {
            double x0 = xs[0], x1 = xs[^1];
            double y0 = Math.Clamp(ys[0], 0.0, 1.0), y1 = Math.Clamp(ys[^1], 0.0, 1.0);
            var pchip = new Pchip(xs.ToArray(), ys.ToArray());
            for (int i = 0; i < CurveSamples; i++)
            {
                double t = i / (CurveSamples - 1);
                double v = t <= x0 ? y0
                         : t >= x1 ? y1
                         : Math.Clamp(pchip.Eval(t), 0.0, 1.0);
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
