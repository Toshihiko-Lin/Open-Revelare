using System;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Slider ↔ pipeline-parameter mappings, ported verbatim from Python
/// <c>gui/frame_edit_panel.py</c> so the controls feel and behave identically.
///
/// The two colour sliders (色温/色调) span only the TWO pure-colour degrees of
/// freedom of a white-balance correction; the third (overall brightness) belongs
/// to the exposure slider. We work in log space with two orthogonal zero-sum
/// basis vectors, so the gains they produce always have geomean == 1 (no
/// brightness change) and the forward/inverse maps are exact inverses — grey-point
/// and auto-WB results land precisely back on the sliders.
/// </summary>
public static class WbMath
{
    // ── White-balance (色温/色调) ────────────────────────────────────────────────
    public const double WbK = 0.002;       // log-gain per slider unit
    public const double WbRange = 250.0;   // slider extent; exp(250·k) ≈ 1.65 gain

    private static readonly double[] ETemp = { 1.0, 0.0, -1.0 };   // warm↔cool (R up / B down)
    private static readonly double[] ETint = { -0.5, 1.0, -0.5 };  // magenta↔green (G vs R+B)

    /// <summary>Sliders (each in [-250,250]) → pure-colour gains with geomean == 1.</summary>
    public static double[] TempTintToGains(double temp, double tint)
    {
        double l0 = temp * WbK * ETemp[0] + tint * WbK * ETint[0];
        double l1 = temp * WbK * ETemp[1] + tint * WbK * ETint[1];
        double l2 = temp * WbK * ETemp[2] + tint * WbK * ETint[2];
        return new[] { Math.Exp(l0), Math.Exp(l1), Math.Exp(l2) };
    }

    /// <summary>
    /// Any gains → (temp, tint, ev). Splits the gains into brightness (geomean,
    /// returned as EV = log2(geomean)) and pure colour (geomean-normalised), then
    /// projects the colour part onto the orthogonal temp/tint basis. Exact inverse
    /// of <see cref="TempTintToGains"/> for the colour part.
    /// </summary>
    public static (double Temp, double Tint, double Ev) GainsToTempTint(double[] gains)
    {
        double g0 = Math.Max(gains[0], 1e-8), g1 = Math.Max(gains[1], 1e-8), g2 = Math.Max(gains[2], 1e-8);
        double geomean = Math.Exp((Math.Log(g0) + Math.Log(g1) + Math.Log(g2)) / 3.0);
        double ev = geomean > 0 ? Math.Log2(geomean) : 0.0;
        double lg0 = Math.Log(g0 / geomean), lg1 = Math.Log(g1 / geomean), lg2 = Math.Log(g2 / geomean);
        double dotTemp = lg0 * ETemp[0] + lg1 * ETemp[1] + lg2 * ETemp[2];
        double dotTint = lg0 * ETint[0] + lg1 * ETint[1] + lg2 * ETint[2];
        double temp = dotTemp / (WbK * (ETemp[0] * ETemp[0] + ETemp[1] * ETemp[1] + ETemp[2] * ETemp[2])); // /2.0
        double tint = dotTint / (WbK * (ETint[0] * ETint[0] + ETint[1] * ETint[1] + ETint[2] * ETint[2])); // /1.5
        return (temp, tint, ev);
    }

    // ── Black / white point (symmetric ±1 sliders, 0 = pass-through) ─────────────
    // _BW_K = 0.5 keeps the old reach: black +1 → black_point -0.5 (raise blacks →
    // matte); −1 → +0.5 (crush blacks); white +1 → white_point 0.5 (brighten →
    // clip); −1 → 1.5 (pull whites down).
    public const double BwK = 0.5;

    public static double BlackSliderToPoint(double b) => -b * BwK;
    public static double BlackPointToSlider(double blackPoint) => Math.Clamp(-blackPoint / BwK, -1.0, 1.0);
    public static double WhiteSliderToPoint(double w) => 1.0 - w * BwK;
    public static double WhitePointToSlider(double whitePoint) => Math.Clamp((1.0 - whitePoint) / BwK, -1.0, 1.0);

}
