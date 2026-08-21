namespace OpenRevelare.Core;

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
public readonly record struct ColorSpaceDef(
    string Name,
    (double X, double Y) Red,
    (double X, double Y) Green,
    (double X, double Y) Blue,
    (double X, double Y) White)
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
        "sRGB", (0.6400, 0.3300), (0.3000, 0.6000), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>Adobe RGB (1998). D65.</summary>
    public static readonly ColorSpaceDef AdobeRgb = new(
        "AdobeRGB", (0.6400, 0.3300), (0.2100, 0.7100), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>
    /// ITU-R BT.709. Shares sRGB's primaries and D65 white exactly — the two differ only in
    /// transfer function (2.4 pure power here, sRGB's piecewise curve there), which is why this
    /// needs its own entry rather than aliasing sRGB: <see cref="OutputRender.EncodingGamma"/>
    /// keys off the name.
    ///
    /// This is the space step 4 of the Cineon workflow names, so it belongs in the picker even
    /// though its gamut is identical to sRGB's.
    /// </summary>
    public static readonly ColorSpaceDef Rec709 = new(
        "Rec709", (0.6400, 0.3300), (0.3000, 0.6000), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>Display P3 — DCI-P3 primaries on a D65 white. </summary>
    public static readonly ColorSpaceDef DisplayP3 = new(
        "DisplayP3", (0.6800, 0.3200), (0.2650, 0.6900), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>
    /// ACEScg (AP1). ACES white (~D60). The default working space: its gamut encloses
    /// every space we might render into, so no intermediate step clips information.
    /// </summary>
    public static readonly ColorSpaceDef AcesCg = new(
        "ACEScg", (0.7130, 0.2930), (0.1650, 0.8300), (0.1280, 0.0440), (0.32168, 0.33767));

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
