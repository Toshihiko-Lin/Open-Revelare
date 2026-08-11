namespace OpenRevelare.Core;

/// <summary>
/// Minimal ICC v2/v4 parser for the tags a scanner TIFF needs on load — the
/// read-side counterpart to <see cref="IccProfiles"/>, which only writes.
///
/// Port of tiff_load.py's ICC handling. Two things are read:
///   1. rTRC/gTRC/bTRC — per-channel encoded→linear curves. Scanners use unequal
///      per-channel gammas (Flextight: R 1.62 / G 1.51 / B 1.56), so a single sRGB
///      inverse leaves a nonlinear cross-channel residual that shows up as opposing
///      colour casts at different luminances and no linear white balance can undo.
///   2. rXYZ/gXYZ/bXYZ — the device→PCS primaries, from which
///      M = M_D50→working × [rXYZ|gXYZ|bXYZ] maps device linear RGB into the pipeline's
///      working space. Absent on LUT-only profiles, where the caller must skip the matrix.
///
/// Profile layout (ICC.1:2010): u32 size at 0, tag table at 128 as a u32 count
/// followed by count × (sig u32, offset u32, size u32).
/// </summary>
public static class IccRead
{
    /// <summary>
    /// Max deviation from the diagonal at which a TRC counts as identity. 0.004 is
    /// about one 8-bit step (1/255) and gamma ≈ 1.01 — only a curve indistinguishable
    /// from linear at 8-bit precision is skipped. Real scanner output is either truly
    /// linear or strongly gamma-encoded (deviation &gt; 0.2), so this only absorbs the
    /// rounding residue of a "linear" export.
    /// </summary>
    public const double TrcLinearTolerance = 0.004;

    /// <summary>The ICC profile connection space white, CIE xy (ICC.1:2010: D50).</summary>
    private static readonly (double X, double Y) PcsWhite = (0.3457, 0.3585);

    /// <summary>
    /// PCS (D50-adapted XYZ) → <see cref="ColorPipeline.Working"/> linear RGB.
    ///
    /// Derived rather than tabulated, so it follows the working space if that declaration ever
    /// changes. Two steps, and both are needed: Bradford-adapt D50 to the working white (ACEScg
    /// sits at ~D60, so skipping this tints everything), then XYZ → working RGB.
    /// </summary>
    private static readonly double[,] MD50ToWorking =
        ColorSpaces.Mul(
            ColorPipeline.Working.FromXyz(),
            ColorSpaces.Adaptation(PcsWhite, ColorPipeline.Working.White));

    /// <summary>Number of entries in the per-channel linearisation LUT. Matches the
    /// Python loader: index-lookup keeps max error under half a 16-bit code level.</summary>
    private const int LutN = 65536;

    /// <summary>{tag signature: (offset, size)} from the tag table; empty when malformed.</summary>
    public static Dictionary<string, (int Offset, int Size)> ParseTags(byte[] icc)
    {
        var tags = new Dictionary<string, (int, int)>();
        try
        {
            if (icc.Length < 132) return tags;
            int count = (int)U32(icc, 128);
            // A truncated or hostile profile can claim a huge count; the per-tag bounds
            // check below is what actually keeps us inside the buffer.
            for (int i = 0; i < count; i++)
            {
                int b = 132 + i * 12;
                if (b + 12 > icc.Length) break;
                string sig = System.Text.Encoding.Latin1.GetString(icc, b, 4);
                int off = (int)U32(icc, b + 4), size = (int)U32(icc, b + 8);
                if (off > 0 && size >= 0 && (long)off + size <= icc.Length)
                    tags[sig] = (off, size);
            }
        }
        catch (Exception) { /* malformed profile → treat as having no tags */ }
        return tags;
    }

    /// <summary>
    /// Decode one TRC tag into (x, y) samples of the encoded→linear mapping, x and y
    /// both in [0,1]. Null when the tag type is not one we can interpret.
    /// </summary>
    public static (double[] X, double[] Y)? DecodeTrc(byte[] icc, int offset, int size)
    {
        try
        {
            if (size < 12) return null;
            string type = System.Text.Encoding.Latin1.GetString(icc, offset, 4);
            if (type == "curv")
            {
                int n = (int)U32(icc, offset + 8);
                if (n == 0)                                    // identity
                    return (new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 });
                if (n == 1)
                {
                    // u8Fixed8 gamma exponent
                    double g = U16(icc, offset + 12) / 256.0;
                    return SampleGamma(g);
                }
                if (offset + 12 + n * 2 > icc.Length || n < 2) return null;
                // The samples ARE the encoded→linear mapping, uniform over [0,1].
                var x = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++)
                {
                    x[i] = (double)i / (n - 1);
                    y[i] = U16(icc, offset + 12 + i * 2) / 65535.0;
                }
                return (x, y);
            }
            if (type == "para")
            {
                int funcType = U16(icc, offset + 8);
                if (funcType == 0)
                {
                    if (offset + 16 > icc.Length) return null;
                    double g = S15Fixed16(icc, offset + 12);
                    return SampleGamma(g);
                }
                // Types 3/4 are sRGB-like; approximate with the standard sRGB curve,
                // as the Python loader does.
                var x = new double[1024];
                var y = new double[1024];
                for (int i = 0; i < 1024; i++)
                {
                    x[i] = i / 1023.0;
                    y[i] = Srgb.SrgbToLinear((float)x[i]);
                }
                return (x, y);
            }
        }
        catch (Exception) { /* unreadable tag → caller falls back */ }
        return null;
    }

    private static (double[] X, double[] Y) SampleGamma(double g)
    {
        var x = new double[1024];
        var y = new double[1024];
        for (int i = 0; i < 1024; i++)
        {
            x[i] = i / 1023.0;
            y[i] = Math.Pow(x[i], g);
        }
        return (x, y);
    }

    /// <summary>
    /// Per-channel encoded→linear LUTs built from the profile's own TRC curves.
    /// Returns null when any channel lacks a usable TRC tag (caller falls back to
    /// the sRGB assumption). <paramref name="allLinear"/> is true when all three
    /// curves are within <see cref="TrcLinearTolerance"/> of identity, meaning the
    /// file is already linear and the LUTs need not be applied.
    /// </summary>
    public static float[][]? BuildTrcLuts(byte[] icc, out bool allLinear)
    {
        allLinear = true;
        var tags = ParseTags(icc);
        var luts = new float[3][];
        string[] sigs = { "rTRC", "gTRC", "bTRC" };
        for (int ch = 0; ch < 3; ch++)
        {
            if (!tags.TryGetValue(sigs[ch], out var t)) { allLinear = false; return null; }
            var curve = DecodeTrc(icc, t.Offset, t.Size);
            if (curve is not ({ } cx, { } cy)) { allLinear = false; return null; }

            // Normalise the linear range to [0,1] so D_min/D_max conventions hold.
            double y0 = cy[0], y1 = cy[^1];
            if (y1 != y0)
                for (int i = 0; i < cy.Length; i++) cy[i] = (cy[i] - y0) / (y1 - y0);

            for (int i = 0; i < cy.Length; i++)
                if (Math.Abs(cy[i] - cx[i]) > TrcLinearTolerance) { allLinear = false; break; }

            var lut = new float[LutN];
            for (int i = 0; i < LutN; i++)
                lut[i] = (float)Interp((double)i / (LutN - 1), cx, cy);
            luts[ch] = lut;
        }
        return luts;
    }

    /// <summary>Linear interpolation over an ascending x grid, clamped at both ends.</summary>
    private static double Interp(double v, double[] xs, double[] ys)
    {
        if (v <= xs[0]) return ys[0];
        if (v >= xs[^1]) return ys[^1];
        int lo = 0, hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] <= v) lo = mid; else hi = mid;
        }
        double span = xs[hi] - xs[lo];
        if (span <= 0) return ys[lo];
        double f = (v - xs[lo]) / span;
        return ys[lo] + f * (ys[hi] - ys[lo]);
    }

    /// <summary>
    /// The 3×3 mapping device linear RGB → <see cref="ColorPipeline.Working"/> linear RGB,
    /// from the profile's rXYZ/gXYZ/bXYZ primaries. Null when any primary tag is missing or
    /// malformed (LUT-only profiles), in which case the caller must skip the matrix.
    ///
    /// The destination is the WORKING space, not sRGB. It landed in sRGB originally, which was
    /// wrong in a way that only showed on profiled scanner TIFFs: those pixels entered the
    /// density maths carrying sRGB primaries, and step 4 then converted them ACEScg → sRGB
    /// (<see cref="ColorPipeline.ToOutputSpace"/>) — undoing a transform nobody had applied.
    /// Reinterpreting sRGB primaries as ACEScg stretches the gamut outward: saturation rose
    /// ~1.13× on red and blue, ~1.4× on green, and neutrals picked up a cast, because the
    /// residual has strong negative off-diagonals rather than being a scalar.
    ///
    /// This is NOT the "external matrix" that <see cref="FrameParams.InputPrimaries"/> warns
    /// about. That warning is about substituting a camera's ColorMatrix for calibrated input
    /// primaries; this matrix only carries the file into the space the pipeline already says
    /// it works in, which is the precondition every later stage assumes.
    /// </summary>
    public static double[,]? ReadMatrix(byte[] icc)
    {
        var tags = ParseTags(icc);
        var devToD50 = new double[3, 3];
        string[] sigs = { "rXYZ", "gXYZ", "bXYZ" };
        for (int c = 0; c < 3; c++)
        {
            if (!tags.TryGetValue(sigs[c], out var t) || t.Size < 20) return null;
            if (System.Text.Encoding.Latin1.GetString(icc, t.Offset, 4) != "XYZ ") return null;
            // Column vector per primary — hence [row, c].
            devToD50[0, c] = S15Fixed16(icc, t.Offset + 8);
            devToD50[1, c] = S15Fixed16(icc, t.Offset + 12);
            devToD50[2, c] = S15Fixed16(icc, t.Offset + 16);
        }
        return Mul3(MD50ToWorking, devToD50);
    }

    /// <summary>Human-readable profile description from 'desc' (v2) or 'dscm' (v4), or null.</summary>
    public static string? Description(byte[] icc)
    {
        var tags = ParseTags(icc);
        foreach (string sig in new[] { "desc", "dscm" })
        {
            if (!tags.TryGetValue(sig, out var t) || t.Size < 12) continue;
            try
            {
                string type = System.Text.Encoding.Latin1.GetString(icc, t.Offset, 4);
                if (type == "desc")
                {
                    // ICCv2 textDescriptionType: u32 ASCII length at +8.
                    int n = (int)U32(icc, t.Offset + 8);
                    if (n <= 0 || t.Offset + 12 + n > icc.Length) continue;
                    string s = System.Text.Encoding.Latin1
                        .GetString(icc, t.Offset + 12, n).Split('\0')[0].Trim();
                    if (s.Length > 0) return s;
                }
                else if (type == "mluc")
                {
                    // ICCv4 multiLocalizedUnicodeType: first record, UTF-16BE.
                    int nRec = (int)U32(icc, t.Offset + 8);
                    if (nRec < 1 || t.Offset + 28 > icc.Length) continue;
                    int recLen = (int)U32(icc, t.Offset + 20);
                    int recOff = (int)U32(icc, t.Offset + 24);
                    if (recLen <= 0 || t.Offset + recOff + recLen > icc.Length) continue;
                    string s = System.Text.Encoding.BigEndianUnicode
                        .GetString(icc, t.Offset + recOff, recLen).Trim('\0', ' ');
                    if (s.Length > 0) return s;
                }
            }
            catch (Exception) { /* unreadable description is not fatal */ }
        }
        return null;
    }

    // ── numeric helpers ──────────────────────────────────────────────────────

    private static uint U32(byte[] b, int i)
        => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    private static double S15Fixed16(byte[] b, int i) => (int)U32(b, i) / 65536.0;

    private static double[,] Mul3(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = a[r, 0] * b[0, c] + a[r, 1] * b[1, c] + a[r, 2] * b[2, c];
        return m;
    }

}
