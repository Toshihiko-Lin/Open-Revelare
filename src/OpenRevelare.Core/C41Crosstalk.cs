namespace OpenRevelare.Core;

/// <summary>
/// The chroma crosstalk direction of dye-density measurement — the shape of what a per-channel
/// inversion cannot recover.
///
/// WHY ANY OF THIS IS NEEDED. Sampling the film base and setting a white point IS a genuine
/// three-channel alignment with a physical basis; it is what t_base / wb_offset / wb_high do, and
/// what "反相 + 三通道对齐" achieves in DaVinci or Photoshop. But those are all PER-CHANNEL
/// operations, and saturation is a relation BETWEEN channels. Aligning the neutral axis to its
/// theoretical optimum moves mean chromatic saturation from 0.7237 to 0.7224 against a reference
/// of 0.8056 (docs/calibration/alignment_vs_chroma.py) — the freedom needed to reach chroma is
/// exactly the freedom already spent holding the neutrals in place. About 11% of the chroma is
/// therefore out of reach of per-channel work. That residual is why the DaVinci/Photoshop route
/// looks sufficient — it restores the visually obvious neutral axis, and 11% reads as "slightly
/// flat" unless measured — and it is what chroma_grade was invented to patch.
///
/// WHERE THIS COMES FROM. Eighteen density correction matrices shipped in DiVERE's
/// config/matrices, solved by users against real scans: Nikon 9000ED and 5000ED, Hasselblad X5;
/// Kodak Gold 200, Portra 160/400, Ultramax 400, Fuji 100, Lucky C200, and the motion-picture
/// stocks 5207 and 5219. No print stage anywhere — these measure the NEGATIVE, which is what this
/// pipeline works on. An earlier version of this file was fitted from ColorChecker datasets that
/// turned out to describe densities on Endura Premier PAPER, and had to be withdrawn; see
/// docs/CALIBRATION.md.
///
/// WHAT THE AGREEMENT SHOWS. Reduced to their chroma action and normalised, ONE direction
/// explains 99.01% of the variance across all eighteen, the worst individual agreement being
/// cosine 0.9798. That holds across scanners, across manufacturers' dye sets, and across
/// processes — 5207 appears developed both C-41 and ECN-2, and both land on this direction. The
/// shape is therefore not a property of C-41 or of any dye formulation. It is what happens
/// whenever a three-layer subtractive dye image is read by three broad sensor channels whose
/// passbands overlap the dyes' absorption bands. That overlap is unavoidable, which is why the
/// effect is universal and why compensating for it is a physical correction rather than a
/// matter of taste.
///
/// WHAT IS NOT UNIVERSAL: strength. Across the same eighteen it runs 0.99 to 1.89, varying with
/// the scanner and with whoever solved it — the same film on the same scanner appears twice, at
/// 1.41 and 1.79. Structurally it works out to target_chroma / negative_chroma, so it is set by
/// which target one declares, not by the film. It stays a parameter; only the direction is fixed
/// here. See docs/calibration/paper_free_crosstalk.py.
/// </summary>
public static class C41Crosstalk
{
    /// <summary>
    /// The consensus direction, row-major, acting on density CHROMA (each channel's deviation
    /// from the pixel's three-channel mean). Unit Frobenius norm, so strength lives entirely in
    /// the scalar it is multiplied by.
    ///
    /// Rows AND columns sum to zero (to 1e-17), giving a luminance null space: it redistributes
    /// chroma between channels and cannot disturb the brightness the inversion established. That
    /// was not imposed — it emerged from the fit, and it is the signature of a real inter-channel
    /// effect rather than a tone adjustment in disguise.
    ///
    /// Its two chroma axes are compressed almost equally (singular values 0.736 and 0.677, ratio
    /// 1.087). Near-isotropic, but not isotropic — and that residual 8.7% is exactly what a
    /// scalar cannot express. It is also far below the 1.52 ratio measured through a paper chain,
    /// which says most of that earlier anisotropy belonged to the paper, not the film.
    /// </summary>
    public static readonly double[,] Direction =
    {
        {  0.47309, -0.19347, -0.27962 },
        { -0.24611,  0.45172, -0.20562 },
        { -0.22698, -0.25826,  0.48524 },
    };

    /// <summary>
    /// Neutral: correct the coupling's SHAPE without changing how saturated the result is.
    ///
    /// The default, and the honest one. Solving each of the eighteen measured matrices for the
    /// strength it implies gives values from -0.43 to +0.48 with a mean of -0.04 — twelve REDUCE
    /// chroma, six increase it. Their consensus is "leave the amount alone", which is also what
    /// the synthetic test in docs/calibration/study_saturation.py found (compensation required:
    /// 0.000). There is no measured basis for a built-in boost, so the default does not apply one.
    /// </summary>
    public const double Neutral = 0.0;

    /// <summary>
    /// A named strength, together with where its number came from.
    /// </summary>
    /// <param name="Name">Stable identifier, stored in project files.</param>
    /// <param name="K">Strength: the result is (I + K · <see cref="Direction"/>) on chroma.</param>
    /// <param name="ChromaGain">What K does to chroma magnitude, for labelling the UI.</param>
    /// <param name="Source">The calibration this was solved from — the roll it is "in the style of".</param>
    public readonly record struct Preset(string Name, double K, double ChromaGain, string Source);

    /// <summary>
    /// Strength presets, each solved from one real calibration in DiVERE's config/matrices.
    ///
    /// These answer "in the style of WHICH roll" explicitly, rather than burying one roll's taste
    /// in a constant — which is how chroma_grade's 3.05 came to look like a law of C-41 when it
    /// was one stock's fit. Every entry here names its source and can be re-derived with
    /// docs/calibration/paper_free_crosstalk.py.
    ///
    /// The ordering matches the stocks' reputations, which is some evidence the numbers capture
    /// real character: Portra soft, Gold denser, Fuji densest.
    ///
    /// HONEST LIMIT: each k carries its scanner along with its film. The same Gold 200 solved on
    /// the same scanner appears at -0.005 and +0.376 in two different users' calibrations. So a
    /// preset is "the look of one calibration", not a measurement of that emulsion.
    /// </summary>
    public static readonly IReadOnlyList<Preset> Presets = new[]
    {
        new Preset("neutral",  Neutral, 1.000, "shape only — no measured basis for a boost"),
        new Preset("portra",   -0.3936, 0.723, "9000ed_pt160 — Portra 160 on Nikon 9000ED"),
        new Preset("soft",     -0.2956, 0.792, "9000ed_pt400 — Portra 400 on Nikon 9000ED"),
        new Preset("median",   -0.0928, 0.935, "median of all eighteen measured calibrations"),
        new Preset("gold",     +0.2095, 1.148, "9000ed_g200_135 — Gold 200 (135) on Nikon 9000ED"),
        new Preset("vivid",    +0.4773, 1.337, "9000ed_fuji100 — Fuji 100 on Nikon 9000ED"),
    };

    /// <summary>The preset with this name, or null. Unknown names fall back rather than throw, so
    /// a project written by a newer build still opens.</summary>
    public static Preset? ByName(string? name) =>
        name is null ? null
        : Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
              is { Name.Length: > 0 } hit ? hit : null;

    /// <summary>
    /// The chroma transform for a given strength: identity plus K times the direction.
    ///
    /// K = 0 gives the identity, i.e. the coupling's shape is applied with no net change in
    /// saturation. Positive K increases chroma, negative reduces it.
    /// </summary>
    public static double[,] ForStrength(double k)
    {
        var m = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = (r == c ? 1.0 : 0.0) + k * Direction[r, c];
        return m;
    }
}
