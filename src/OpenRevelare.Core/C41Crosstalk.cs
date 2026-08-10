namespace OpenRevelare.Core;

/// <summary>
/// The C-41 dye crosstalk direction — the process's own signature, shared by every stock.
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
/// WHY A SINGLE SHARED MATRIX IS LEGITIMATE — and this is the design's whole premise, not an
/// approximation of convenience. Solving the density-chroma matrix independently for eight
/// modelled C-41 stocks (Gold 200, Portra 160/400/800, Ektar 100, Ultramax 400, plus two DIR
/// variants) gives eight matrices whose DIRECTIONS agree to a cosine similarity of 0.9957–0.9997.
/// Allowing each stock its own full matrix instead of one shared shape plus a per-stock scalar
/// improves the fit by 0.1%. The extra freedom buys nothing: the shape belongs to the PROCESS.
///
/// What differs between stocks is only strength — Portra 160 at 4.21, Gold 200 at 3.45, and the
/// DIR variants down at 3.16–3.21, DIR couplers visibly suppressing chroma without rotating it.
/// That difference is exactly the stock character the pipeline is meant to preserve rather than
/// normalise away, and it arrives on its own: the same matrix applied to a denser or softer
/// negative produces a denser or softer positive. See docs/calibration/universal_crosstalk.py.
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
