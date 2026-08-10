namespace OpenRevelare.Core;

/// <summary>
/// A chroma-crosstalk direction fitted from DiVERE's modelled ColorChecker data.
///
/// ⚠ NOT what its name suggests, and not usable as a C-41 process constant. Every one of the
/// eight datasets it was fitted from describes densities ON KODAK ENDURA PREMIER PAPER, not on
/// the negative — their own descriptions say so ("…→kodak_endura_premier_d60_uc 打印流程下，相纸
/// 上得到的理论密度") and each declares required_working_colorspace: KodakEnduraPremier. The
/// matrix therefore maps PAPER density to scene, and carries the paper's dye characteristics
/// baked in. OpenRevelare deliberately does not model a print stage, so this is the wrong
/// transform for its pipeline.
///
/// The eight stocks' agreement in direction (cosine 0.9957–0.9997) is consequently weaker
/// evidence than it looked: they share one print chain, so part of that agreement is the paper's,
/// not the film's. Whether C-41 alone has a universal crosstalk direction remains OPEN — DiVERE
/// ships no paper-free film data, so it cannot be settled from this source.
///
/// Kept, disabled by default, because the analysis and the fitting method are sound and become
/// usable the moment paper-free densities exist. Do not enable it expecting a process constant.
///
/// A per-channel inversion cannot recover the chroma that C-41 loses, because the loss happens
/// BETWEEN channels: the three dye layers' absorptions overlap, so each channel's density reading
/// carries some of the others. Measured here, aligning the neutral axis perfectly (which is all
/// t_base / wb_offset / wb_high can do, being per-channel) leaves saturation untouched at 0.7237
/// against a reference of 0.8056 — see docs/calibration/alignment_vs_chroma.py. Roughly 11% of
/// the chroma is simply out of reach of per-channel operations.
///
/// chroma_grade was the previous answer: one isotropic scalar. It cannot be right in principle,
/// because the relationship is anisotropic, and the fitted residual showed it (per-patch error up
/// to 0.206). What the relationship actually is, is the matrix below.
///
/// THE SHARED-SHAPE RESULT, and what survives of it. Fitting the matrix independently per stock
/// gives eight whose directions agree to cosine 0.9957–0.9997, and forcing one shared shape with
/// only a per-stock scalar costs 0.1% of fit quality. That much is a real property of the data.
/// What it is a property OF is the open question: the eight share a print chain as well as a
/// process, so the agreement cannot be attributed to C-41 alone.
///
/// Strength does vary per stock — Portra 160 4.06, Gold 200 3.43, DIR variants 3.15–3.17 — and
/// DIR suppressing magnitude without rotating direction is the one finding here that is hard to
/// explain by the paper. But strength is not a film constant either: it works out to
/// target_chroma / negative_chroma, so it is set by which target one declares, not by C-41.
/// See docs/calibration/universal_crosstalk.py.
/// </summary>
public static class C41Crosstalk
{
    /// <summary>
    /// The shared direction, row-major, acting on density CHROMA (each channel's deviation from
    /// the pixel's three-channel mean). Unit Frobenius norm, so strength lives entirely in the
    /// scalar it is multiplied by.
    ///
    /// Its rows sum to zero (to 1e-16), which is the property that makes it safe here: it moves
    /// chroma without touching the luminance the inversion has already established. That is not
    /// imposed — it fell out of the fit, and it is what a genuine inter-channel effect looks like.
    /// </summary>
    public static readonly double[,] Direction =
    {
        {  0.40784, -0.12200, -0.28584 },
        { -0.14987,  0.42026, -0.27039 },
        { -0.25797, -0.29826,  0.55623 },
    };

    /// <summary>
    /// Strength of the shared fit across all eight stocks (Frobenius norm of the joint solution).
    ///
    /// Per-stock strengths run 3.16–4.21; this is the middle of that range rather than any one
    /// stock's value, which is the point — it is the process baseline, and each stock's departure
    /// from it survives as character. chroma_grade's 3.05 sits just below the lowest measured
    /// stock, which is part of why it read as slightly flat.
    /// </summary>
    public const double Strength = 3.6106;

    /// <summary>
    /// <see cref="Direction"/> scaled by <paramref name="strength"/> — the matrix to hand to the
    /// inversion. Note the inversion multiplies by chroma_grade on top, so pass
    /// <see cref="Direction"/> with chroma_grade carrying the strength, or this with
    /// chroma_grade at 1; do not do both.
    /// </summary>
    public static double[,] Scaled(double strength = Strength)
    {
        var m = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = Direction[r, c] * strength;
        return m;
    }
}
