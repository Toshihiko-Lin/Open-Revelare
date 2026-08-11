using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Import-time sprocket confirmation — port of Python gui/sprocket_dialog.py. Shows the
/// first frame with a live green mask (luma &gt; threshold) so the user can dial the
/// threshold to cover sprockets/light-board but not the film base or picture. Result:
/// 确认并继续 → (true, threshold); 这卷无齿孔(跳过) → (true, null); close/Esc → (false, _).
/// </summary>
public partial class SprocketDialog : Window
{
    private readonly int _w, _h;
    private readonly float[] _luma;      // per-pixel mean of the linear negative
    private readonly float[] _baseDisp;  // gamma-encoded display buffer (sRGB [0,1], interleaved)

    public bool ResultEnabled { get; private set; }
    public double? ResultThreshold { get; private set; }

    public SprocketDialog() { InitializeComponent(); _luma = System.Array.Empty<float>(); _baseDisp = System.Array.Empty<float>(); }

    public SprocketDialog(ImageBuffer preview) : this()
    {
        _w = preview.Width; _h = preview.Height;

        // Display-encoded base; green is baked in per Refresh() (matches Python's
        // _to_overlay_pixmap — one composited image, not a separate layer).
        //
        // The incoming buffer is scene-linear working space, so this is the step-4 conversion, not
        // a bare gamma: the sprocket mask is judged on luma and geometry, but the operator is
        // still looking at a picture and it should not be in the wrong primaries.
        _baseDisp = (float[])preview.Data.Clone();
        ColorPipeline.ToOutputSpace(_baseDisp, ColorPipeline.DefaultOutput);

        // Mean luma for the mask threshold (matches Python's image.mean(axis=2)).
        _luma = new float[_w * _h];
        float[] d = preview.Data;
        for (int p = 0; p < _luma.Length; p++)
            _luma[p] = (d[p * 3] + d[p * 3 + 1] + d[p * 3 + 2]) / 3f;

        var (board, filmbase) = Sprocket.MeasureBoardAndFilmbase(preview);
        if (board > 0)
        {
            double gap = board - filmbase;
            string warn = gap < 0.08 ? Loc.T("  ⚠ 间隙较窄，请仔细微调") : "";
            RefLbl.Text = Loc.F($"参考：灯板亮度≈{board:F3}，片基亮端≈{filmbase:F3}，间隙≈{gap:F3}{warn}");
        }
        else
        {
            RefLbl.Text = Loc.T("提示：未检测到灯板（这帧可能没有齿孔，或灯板未过曝）");
        }

        ResultThreshold = Sprocket.EstimateSprocketThreshold(preview);
        Thr.Value = ResultThreshold.Value;
        Thr.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        float thr = (float)Thr.Value;
        ValLbl.Text = thr.ToString("F3");

        // Bake bright red (0.85 red + 0.15 base) into masked pixels, else the base.
        var comp = new float[_baseDisp.Length];
        System.Array.Copy(_baseDisp, comp, comp.Length);
        for (int p = 0; p < _luma.Length; p++)
        {
            if (_luma[p] <= thr) continue;
            int i = p * 3;
            comp[i] = 1f - (1f - comp[i]) * 0.15f;           // R up toward 1
            comp[i + 1] = comp[i + 1] * 0.15f;               // G down
            comp[i + 2] = comp[i + 2] * 0.15f;               // B down
        }
        Disp.Source = BitmapConvert.ToBitmap(new ImageBuffer(_w, _h, comp));
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        ResultEnabled = true;
        ResultThreshold = Thr.Value;
        Close(true);
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        // No threshold, but enabled stays true → auto film-base uses pure-brightness mode.
        ResultEnabled = true;
        ResultThreshold = null;
        Close(true);
    }
}
