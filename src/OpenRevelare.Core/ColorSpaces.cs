namespace OpenRevelare.Core;

/// <summary>
/// A colour space's ENCODING TRANSFER FUNCTION — the curve that takes linear light to the
/// encoded values a file or a display carries, and back.
///
/// WHY THIS IS A PROPERTY OF THE SPACE RATHER THAN A LOOKUP BY NAME. It used to be neither: the
/// power-curve spaces were resolved through a <c>switch</c> on <see cref="ColorSpaceDef.Name"/>
/// in <c>OutputRender.EncodingGamma</c>, while sRGB and Display P3 were intercepted by an
/// <c>if</c> at the top of Encode/Decode and sent to the piecewise curve instead. Two places
/// each held a partial answer to "what curve does this space use", and they disagreed: the
/// switch's <c>_ => 2.2</c> fallback claimed 2.2 for Display P3, which the interception meant it
/// could never actually receive.
///
/// The disagreement was not academic. <see cref="ColorPipeline.ToOutputSpaceVia"/> re-containers
/// a print-film cube's Rec709 output by decoding with the SOURCE space's curve and encoding with
/// the DESTINATION's, on the stated assumption that the pair round-trips. With Rec709 decoding
/// through a pure 2.4 power and sRGB encoding through the piecewise curve, it did not: the two
/// agree only at 0 and 1, and the piecewise curve's linear toe makes it far brighter in the
/// shadows. A film base that the Kodak 2383 cube correctly rendered at 3.74% luminance left the
/// round trip at 0.50% — an 87% crush — and tripped the ≤2% under-exposure indicator, along with
/// every shadow the cube had placed below 0.0675. Display P3 was hit identically, and could not
/// be rescued by skipping the round trip on matching primaries the way sRGB could, because P3's
/// matrix is genuinely not the identity.
///
/// Stating the curve on the space makes Encode and Decode read the SAME field, so they are
/// inverses by construction and there is no second place for a partial answer to live.
/// </summary>
public enum TransferFunction
{
    /// <summary>
    /// A pure power curve, <c>encoded = linear^(1/gamma)</c>, with the exponent in
    /// <see cref="ColorSpaceDef.Gamma"/>. What BT.1886 (Rec709/Rec2020, gamma 2.4) and Adobe RGB
    /// (563/256) declare.
    /// </summary>
    Power,

    /// <summary>
    /// The IEC 61966-2-1 piecewise curve — a short linear segment near black joined to a
    /// 1.055·x^(1/2.4) − 0.055 power segment. Shared by sRGB and Display P3, which differ only
    /// in primaries. NOT interchangeable with <see cref="Power"/> at gamma 2.4: the linear toe
    /// makes it markedly brighter below ~10% encoded, which is exactly where the round-trip bug
    /// above did its damage.
    /// </summary>
    SrgbPiecewise,

    /// <summary>
    /// No encoding curve at all — the space is scene-linear. ACEScg is the only such space here.
    /// </summary>
    Linear,
}

/// <summary>
/// An RGB colour space, defined the only way that makes it convertible: chromaticity
/// coordinates for the three primaries plus a white point. Everything else — the
/// RGB↔XYZ matrices, the conversion between any two spaces — is derived from these.
///
/// This is the piece the pipeline was missing. Until now the linear data coming out
/// of the inversion had no declared colour space at all; it was implicitly "sRGB
/// because that is what we happen to export". That implicitness is what let
/// <c>chroma_grade</c> take root: with no gamut to convert into, a scalar on the
/// chroma vector was the only lever available for "the colours look wrong", and a
/// scalar cannot express what is actually an anisotropic, per-hue gamut relationship.
/// </summary>
/// <param name="Name">Stable identifier, also used in project files.</param>
/// <param name="Red">CIE xy of the red primary.</param>
/// <param name="Green">CIE xy of the green primary.</param>
/// <param name="Blue">CIE xy of the blue primary.</param>
/// <param name="White">CIE xy of the white point.</param>
/// <param name="Transfer">The encoding curve this space carries; see <see cref="TransferFunction"/>.
/// Defaults to <see cref="TransferFunction.Power"/> at <paramref name="Gamma"/>.</param>
/// <param name="Gamma">Exponent for <see cref="TransferFunction.Power"/>. Ignored by the other
/// two. Defaults to 2.2, the value the old name-keyed lookup fell back to.</param>
public readonly record struct ColorSpaceDef(
    string Name,
    (double X, double Y) Red,
    (double X, double Y) Green,
    (double X, double Y) Blue,
    (double X, double Y) White,
    TransferFunction Transfer = TransferFunction.Power,
    double Gamma = 2.2)
{
    /// <summary>
    /// The RGB→XYZ matrix (row-major, [row, col]), built by the standard construction:
    /// scale each primary's normalised XYZ so that RGB = (1,1,1) lands exactly on the
    /// white point. See SMPTE RP 177.
    /// </summary>
    public double[,] ToXyz()
    {
        // Each primary as XYZ with Y normalised to 1.
        double[,] p = new double[3, 3];
        var xy = new[] { Red, Green, Blue };
        for (int c = 0; c < 3; c++)
        {
            var (x, y) = xy[c];
            p[0, c] = x / y;
            p[1, c] = 1.0;
            p[2, c] = (1.0 - x - y) / y;
        }

        // White as XYZ, Y = 1.
        double[] w = { White.X / White.Y, 1.0, (1.0 - White.X - White.Y) / White.Y };

        // Per-primary scale factors s solving p · s = w.
        double[] s = Solve3(p, w);

        double[,] m = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = p[r, c] * s[c];
        return m;
    }

    /// <summary>The XYZ→RGB matrix, the inverse of <see cref="ToXyz"/>.</summary>
    public double[,] FromXyz() => ColorSpaces.Invert3(ToXyz());

    /// <summary>Solves the 3×3 system a·x = b by Cramer's rule.</summary>
    private static double[] Solve3(double[,] a, double[] b)
    {
        double det = Det3(a);
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Degenerate primaries: the three chromaticities are collinear.");

        double[] x = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double[,] t = (double[,])a.Clone();
            for (int r = 0; r < 3; r++) t[r, c] = b[r];
            x[c] = Det3(t) / det;
        }
        return x;
    }

    internal static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
      - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
      + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
}

/// <summary>
/// The colour spaces the pipeline knows about, and the maths to convert between them.
///
/// Primaries for the film/paper spaces come from DiVERE (MIT, see THIRD_PARTY_NOTICES),
/// which derived them from the respective dye sets; the display spaces carry their
/// standard published values. They are compiled in rather than loaded from JSON so the
/// core has no data-file dependency at runtime.
/// </summary>
public static class ColorSpaces
{
    /// <summary>IEC 61966-2-1. D65.</summary>
    public static readonly ColorSpaceDef Srgb = new(
        "sRGB", (0.6400, 0.3300), (0.3000, 0.6000), (0.1500, 0.0600), (0.3127, 0.3290),
        TransferFunction.SrgbPiecewise);

    /// <summary>Adobe RGB (1998). D65.</summary>
    public static readonly ColorSpaceDef AdobeRgb = new(
        "AdobeRGB", (0.6400, 0.3300), (0.2100, 0.7100), (0.1500, 0.0600), (0.3127, 0.3290),
        TransferFunction.Power, 563.0 / 256.0);

    /// <summary>
    /// ITU-R BT.709. Shares sRGB's primaries and D65 white EXACTLY — the two differ only in
    /// transfer function: BT.1886's pure 2.4 power here, IEC 61966-2-1's piecewise curve there.
    ///
    /// That difference is the entire content of this entry, and stating it as
    /// <see cref="TransferFunction"/> on the space is what keeps Encode and Decode inverses. It
    /// used to be inferred from the NAME in two separate places that disagreed; see the remarks
    /// on <see cref="TransferFunction"/> for the shadow crush that produced.
    ///
    /// This is the space step 4 of the Cineon workflow names, and the space every print-film cube
    /// renders into, so it belongs in the picker even though its gamut is identical to sRGB's.
    /// </summary>
    public static readonly ColorSpaceDef Rec709 = new(
        "Rec709", (0.6400, 0.3300), (0.3000, 0.6000), (0.1500, 0.0600), (0.3127, 0.3290),
        TransferFunction.Power, 2.4);

    /// <summary>
    /// Display P3 — DCI-P3 primaries on a D65 white, carrying sRGB's piecewise TRC. That curve is
    /// the space's definition, not a convenience: Apple's Display P3 is specified as sRGB's
    /// transfer function over wider primaries.
    /// </summary>
    public static readonly ColorSpaceDef DisplayP3 = new(
        "DisplayP3", (0.6800, 0.3200), (0.2650, 0.6900), (0.1500, 0.0600), (0.3127, 0.3290),
        TransferFunction.SrgbPiecewise);

    /// <summary>
    /// ACEScg (AP1). ACES white (~D60). The default working space: its gamut encloses
    /// every space we might render into, so no intermediate step clips information.
    /// </summary>
    public static readonly ColorSpaceDef AcesCg = new(
        "ACEScg", (0.7130, 0.2930), (0.1650, 0.8300), (0.1280, 0.0440), (0.32168, 0.33767),
        TransferFunction.Linear);

    /// <summary>Every registered space, keyed by <see cref="ColorSpaceDef.Name"/>.</summary>
    public static readonly IReadOnlyDictionary<string, ColorSpaceDef> All =
        new[] { Srgb, Rec709, AdobeRgb, DisplayP3, AcesCg }
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks a space up by name, falling back to <paramref name="fallback"/> for names
    /// written by a newer version (project files must survive a downgrade).
    /// </summary>
    public static ColorSpaceDef ByName(string? name, ColorSpaceDef fallback) =>
        name is not null && All.TryGetValue(name, out var s) ? s : fallback;

    // ---- Chromatic adaptation -------------------------------------------------

    /// <summary>
    /// Bradford cone response. The standard choice for ICC-compatible adaptation, and
    /// what every profile we read or write assumes.
    /// </summary>
    private static readonly double[,] Bradford =
    {
        {  0.8951,  0.2664, -0.1614 },
        { -0.7502,  1.7135,  0.0367 },
        {  0.0389, -0.0685,  1.0296 },
    };

    private static readonly double[,] BradfordInv = Invert3(Bradford);

    /// <summary>
    /// The von Kries-style adaptation matrix taking XYZ under <paramref name="from"/>
    /// to XYZ under <paramref name="to"/>, in Bradford cone space.
    ///
    /// Needed because our spaces do not share a white point: sRGB/AdobeRGB are D65,
    /// ACEScg and the paper spaces sit at ~D60. Converting between them without
    /// adapting would tint the result.
    /// </summary>
    public static double[,] Adaptation((double X, double Y) from, (double X, double Y) to)
    {
        double[] src = Cone(from), dst = Cone(to);
        double[,] scale =
        {
            { dst[0] / src[0], 0, 0 },
            { 0, dst[1] / src[1], 0 },
            { 0, 0, dst[2] / src[2] },
        };
        return Mul(BradfordInv, Mul(scale, Bradford));

        static double[] Cone((double X, double Y) w)
        {
            double[] xyz = { w.X / w.Y, 1.0, (1.0 - w.X - w.Y) / w.Y };
            return Apply(Bradford, xyz);
        }
    }

    /// <summary>
    /// The 3×3 taking linear RGB in <paramref name="from"/> to linear RGB in
    /// <paramref name="to"/>, white points adapted. This is the whole of a
    /// colour-space conversion for in-gamut colours; out-of-gamut ones additionally
    /// need gamut mapping, which is a separate, deliberately visible step.
    /// </summary>
    public static double[,] Convert(ColorSpaceDef from, ColorSpaceDef to)
    {
        double[,] m = from.ToXyz();
        if (from.White != to.White)
            m = Mul(Adaptation(from.White, to.White), m);
        return Mul(to.FromXyz(), m);
    }

    /// <summary>ICC PCS illuminant. Both the profiles we embed and Skia's colour spaces carry
    /// D50-adapted matrices, so a space's own matrix is adapted before either sees it.</summary>
    public static readonly (double X, double Y) D50 = (0.34567, 0.35850);

    /// <summary>
    /// <paramref name="space"/>'s RGB→XYZ matrix, Bradford-adapted to the D50 PCS.
    ///
    /// The form every colour-management consumer wants: it is what an ICC 'rXYZ'/'gXYZ'/'bXYZ'
    /// triple holds and what Skia's SKColorSpaceXyz expects. One implementation so the embedded
    /// profile and the on-screen preview cannot describe the same space differently.
    /// </summary>
    public static double[,] ToXyzD50(ColorSpaceDef space)
        => Mul(Adaptation(space.White, D50), space.ToXyz());

    /// <summary>
    /// <paramref name="space"/>'s encoding curve as the ICC 'para' type-4 / Skia seven-parameter
    /// form, in the ENCODED→LINEAR direction:
    /// <code>
    ///   linear = (a·x + b)^g + e   for x >= d
    ///   linear = c·x + f           for x &lt;  d
    /// </code>
    /// Returned as <c>{g, a, b, c, d, e, f}</c>, Skia's field order.
    ///
    /// Both curves the pipeline uses are expressible here EXACTLY — sRGB's piecewise TRC included,
    /// which is the point: approximating it with a 2.2 power is the conflation this codebase has
    /// already paid for once (see <see cref="TransferFunction"/>).
    /// </summary>
    public static double[] TransferParameters(ColorSpaceDef space) => space.Transfer switch
    {
        // IEC 61966-2-1, in the encoded→linear direction. The break at 0.04045 and the
        // 1/1.055 / 0.055/1.055 / 1/12.92 constants are the standard's own.
        TransferFunction.SrgbPiecewise => new[]
            { 2.4, 1.0 / 1.055, 0.055 / 1.055, 1.0 / 12.92, 0.04045, 0.0, 0.0 },

        // Scene-linear: the identity.
        TransferFunction.Linear => new[] { 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 },

        // A pure power curve. d = 0 puts the whole domain in the power branch, so c and f are
        // never evaluated.
        _ => new[] { space.Gamma, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
    };

    // ---- Small matrix helpers -------------------------------------------------

    /// <summary>Matrix product a·b, both 3×3.</summary>
    public static double[,] Mul(double[,] a, double[,] b)
    {
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }

    /// <summary>Applies a 3×3 to a 3-vector.</summary>
    public static double[] Apply(double[,] m, double[] v) => new[]
    {
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    };

    /// <summary>Inverts a 3×3 by the adjugate; throws when singular.</summary>
    public static double[,] Invert3(double[,] m)
    {
        double det = ColorSpaceDef.Det3(m);
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Singular matrix.");

        double[,] r = new double[3, 3];
        r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return r;
    }
}
