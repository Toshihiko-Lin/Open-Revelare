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

    // ── Stage-1 highlight endpoint ↔ (brightness, temp, tint) ───────────────────
    //
    // The three highlight densities carry exactly two kinds of information, and the whole point
    // of this split is that they are SEPARABLE — but only in the geometric (log) domain.
    //
    //   geometric mean        → how deep the roll's highlight is  → "brightness"
    //   geomean-1 remainder   → the cast                          → temp / tint
    //
    // Measured: scaling all three endpoints by one factor leaves the between-channel slope ratio
    // identical to the last digit, while ADDING a constant to all three drifts it (R/B 1.07793 →
    // 1.07128 for a +0.20 shift). So brightness must be multiplicative, not additive — an
    // arithmetic "mean" control would quietly recolour the picture, which is exactly the coupling
    // this decomposition exists to remove.
    //
    // The colour half reuses ETemp/ETint above verbatim, so a given temp number means the same
    // thing here as it does in Frame edit. Only the domain differs: Stage 2's gains multiply
    // LINEAR light, while these multiply a DENSITY endpoint. Both are per-channel log-domain
    // moves, which is why one basis serves both.
    //
    // NOTE the sign. A LARGER endpoint means that channel's white arrives later, i.e. the channel
    // renders DARKER — the opposite of a gain. TempTintToGains is therefore applied inverted, so
    // that raising 色温 warms the picture in Stage 1 exactly as it does in Stage 2 rather than
    // cooling it.

    /// <summary>Highlight endpoint triple → (brightness, temp, tint). Brightness is the geometric
    /// mean (a density, same units as the endpoints); temp/tint use the Stage-2 basis.</summary>
    public static (double Brightness, double Temp, double Tint) EndpointToBrightTempTint(double[] endpoint)
    {
        double e0 = Math.Max(endpoint[0], 1e-6), e1 = Math.Max(endpoint[1], 1e-6), e2 = Math.Max(endpoint[2], 1e-6);
        double geomean = Math.Exp((Math.Log(e0) + Math.Log(e1) + Math.Log(e2)) / 3.0);
        // Inverted (geomean/e) so the colour half reads as a GAIN, matching Stage 2's convention.
        var (temp, tint, _) = GainsToTempTint(new[] { geomean / e0, geomean / e1, geomean / e2 });
        return (geomean, temp, tint);
    }

    /// <summary>
    /// (brightness, temp, tint) → the highlight endpoint triple. Exact inverse of
    /// <see cref="EndpointToBrightTempTint"/>, so sampling a highlight and reading the sliders
    /// back lands on the same three densities it measured.
    /// </summary>
    public static double[] BrightTempTintToEndpoint(double brightness, double temp, double tint)
    {
        double[] g = TempTintToGains(temp, tint);
        double b = Math.Max(brightness, 1e-6);
        // Inverse of the above: endpoint = geomean / gain.
        return new[] { b / g[0], b / g[1], b / g[2] };
    }

    /// <summary>
    /// The shadow endpoint's scalar level — its ARITHMETIC mean, not the geometric one.
    ///
    /// The shadow end sits at or near zero on an untouched roll (t_base put the film base there),
    /// so a geometric mean would be undefined or explosive exactly where rolls normally live. The
    /// cast half is deliberately not surfaced: shadow colour is set once by sampling the base and
    /// then left alone, so it lives in the advanced panel rather than on a pair of sliders.
    /// </summary>
    public static double ShadowLevel(double[] shadow) => (shadow[0] + shadow[1] + shadow[2]) / 3.0;

    /// <summary>Move the shadow triple to a new mean level, PRESERVING its per-channel cast —
    /// an additive shift, which is the operation that leaves the between-channel differences
    /// (i.e. the shadow cast) untouched.</summary>
    public static double[] ShadowWithLevel(double[] shadow, double level)
    {
        double d = level - ShadowLevel(shadow);
        return new[] { shadow[0] + d, shadow[1] + d, shadow[2] + d };
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
