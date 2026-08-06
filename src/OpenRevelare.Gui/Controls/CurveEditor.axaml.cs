using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// Full tone-curve editor — port of Python <c>gui/curve_widget.py::CurveWidget</c>:
/// W/R/G/B channel selector, a shared editing canvas, a reset button, and the
/// W-curve hue-preserve toggle. Raises <see cref="CurvesChanged"/> on any edit and
/// <see cref="InteractionEnded"/> when a drag finishes (host uses it to reprocess).
/// </summary>
public partial class CurveEditor : UserControl
{
    private readonly List<Point>[] _channels =
    {
        new(), new(), new(), new(),   // W, R, G, B — points (x,y) in [0,1]
    };
    private static readonly Color[] Colors =
    {
        Color.FromRgb(225, 225, 225), Color.FromRgb(232, 96, 96),
        Color.FromRgb(96, 205, 96), Color.FromRgb(96, 140, 240),
    };
    private ToggleButton[] _buttons = Array.Empty<ToggleButton>();
    private readonly float[]?[] _chanHist = new float[]?[4];   // W(luma), R, G, B backdrops
    private int _active;
    private bool _suppressEmit;   // true while loading a frame's curves (no change events)

    public event EventHandler? CurvesChanged;
    /// <summary>Curve drag begun — same low-latency contract as
    /// <see cref="SliderRow.InteractionStartedEvent"/>.</summary>
    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionEnded;
    public event EventHandler? PreserveHueChanged;

    public CurveEditor()
    {
        InitializeComponent();
        _buttons = new[] { BtnW, BtnR, BtnG, BtnB };
        for (int i = 0; i < _buttons.Length; i++)
        {
            int idx = i;
            _buttons[i].IsCheckedChanged += (_, _) => { if (_buttons[idx].IsChecked == true) SwitchChannel(idx); };
        }
        BtnReset.Click += (_, _) => Canvas.Reset();
        Canvas.PointsChanged += (_, _) => { if (!_suppressEmit) CurvesChanged?.Invoke(this, EventArgs.Empty); };
        Canvas.EditBegan += (_, _) => InteractionStarted?.Invoke(this, EventArgs.Empty);
        Canvas.EditEnded += (_, _) => InteractionEnded?.Invoke(this, EventArgs.Empty);
        PreserveChk.IsCheckedChanged += (_, _) => { if (!_suppressEmit) PreserveHueChanged?.Invoke(this, EventArgs.Empty); };

        SwitchChannel(0);
    }

    private void SwitchChannel(int idx)
    {
        _active = idx;
        for (int i = 0; i < _buttons.Length; i++)
            _buttons[i].IsChecked = i == idx;
        Canvas.CurveColor = Colors[idx];
        Canvas.SetPoints(_channels[idx]);
        Canvas.SetHistogram(_chanHist[idx]);
    }

    /// <summary>Push the render's histogram behind each channel curve (W = luma).</summary>
    public void SetHistogram(HistogramData? d)
    {
        if (d is null)
        {
            for (int i = 0; i < 4; i++) _chanHist[i] = null;
        }
        else
        {
            _chanHist[0] = Normalise(d.L);
            _chanHist[1] = Normalise(d.R);
            _chanHist[2] = Normalise(d.G);
            _chanHist[3] = Normalise(d.B);
        }
        Canvas.SetHistogram(_chanHist[_active]);
    }

    /// <summary>Normalise counts to [0,1] against the 99th-percentile ceiling (spike-robust).</summary>
    private static float[] Normalise(float[] counts)
    {
        var sorted = (float[])counts.Clone();
        Array.Sort(sorted);
        float ceil = sorted[Math.Min((int)(sorted.Length * 0.99), sorted.Length - 1)];
        if (ceil <= 0f) ceil = 1f;
        var outp = new float[counts.Length];
        for (int i = 0; i < counts.Length; i++) outp[i] = Math.Min(counts[i] / ceil, 1f);
        return outp;
    }

    /// <summary>Channel points (0=W,1=R,2=G,3=B) as pipeline (x,y) tuples.</summary>
    public IReadOnlyList<(double X, double Y)> GetChannel(int idx)
    {
        var list = new List<(double, double)>(_channels[idx].Count);
        foreach (Point p in _channels[idx]) list.Add((p.X, p.Y));
        return list;
    }

    public bool PreserveHue => PreserveChk.IsChecked == true;

    /// <summary>Clear every channel's points (used by the panel-wide 重置调整).</summary>
    public void ResetAll()
    {
        foreach (var ch in _channels) ch.Clear();
        Canvas.SetPoints(_channels[_active]);
    }

    /// <summary>Load a frame's four channel curves + hue-preserve flag without raising change events.</summary>
    public void SetAll(IReadOnlyList<(double X, double Y)> m, IReadOnlyList<(double X, double Y)> r,
                       IReadOnlyList<(double X, double Y)> g, IReadOnlyList<(double X, double Y)> b,
                       bool preserveHue)
    {
        _suppressEmit = true;
        Fill(0, m); Fill(1, r); Fill(2, g); Fill(3, b);
        PreserveChk.IsChecked = preserveHue;
        Canvas.SetPoints(_channels[_active]);
        _suppressEmit = false;

        void Fill(int idx, IReadOnlyList<(double X, double Y)> pts)
        {
            _channels[idx].Clear();
            foreach (var p in pts) _channels[idx].Add(new Point(p.X, p.Y));
        }
    }
}
