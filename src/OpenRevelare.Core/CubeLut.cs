using System.Globalization;

namespace OpenRevelare.Core;

/// <summary>
/// What encoding a LUT expects on its input, i.e. what has to be true of the data before the
/// cube is sampled.
///
/// This is a property OF THE LUT, not a global constant, and it is declared here rather than
/// assumed because the assumption is exactly what breaks when the second LUT arrives. Print-film
/// emulations (2383, 3513) are authored against Cineon printing density and are the reason this
/// path exists at all; other vendors' cubes are authored against ACEScct or Log-C, and feeding
/// one of those a Cineon-encoded signal produces a plausible-looking but wrong picture — the
/// worst failure mode there is, because nothing errors.
///
/// Only Cineon is implemented today. The enum exists so that adding another is a new case in
/// <see cref="LogEncoding"/> rather than a rethink of where the encoding decision lives.
/// </summary>
public enum LutInputEncoding
{
    /// <summary>
    /// Cineon printing density, 10-bit code values normalised by 1023. Black at code 95, white at
    /// code 1032 — the same two ends <see cref="FrameParams.OutputRange"/> is the span of, which
    /// is what makes the encoding an affine map off the density domain rather than a conversion.
    /// </summary>
    Cineon,
}

/// <summary>
/// A 3D lookup table loaded from an Iridas/Adobe <c>.cube</c> file, plus the encoding its input
/// is authored against.
///
/// WHY A 3D LUT AND NOT MORE COLOUR SPACES. The picker used to offer "Kodak2383" as a
/// <see cref="ColorSpaceDef"/> — three chromaticity coordinates standing in for a print film. It
/// was removed because that is not what a print stock is: its look lives in per-channel density
/// curves and in cross-channel coupling that no set of primaries can express. A cube can express
/// it, because a cube is exactly a sampled arbitrary function of three variables.
///
/// The table is stored as flat interleaved RGB with the RED axis varying fastest, which is the
/// .cube specification's order.
/// </summary>
public sealed class CubeLut
{
    /// <summary>Samples per axis. The cube holds <c>Size³</c> RGB triples.</summary>
    public int Size { get; }

    /// <summary>Flat RGB triples, red-fastest. Length is <c>Size³ · 3</c>.</summary>
    private readonly float[] _data;

    /// <summary>Per-channel input domain floor, from <c>DOMAIN_MIN</c> (default 0,0,0).</summary>
    public float[] DomainMin { get; }

    /// <summary>
    /// Per-channel input domain ceiling, from <c>DOMAIN_MAX</c> (default 1,1,1).
    ///
    /// Print-film cubes routinely declare a max above 1: Cineon's white sits at code 1032, which
    /// is 1.0088 once normalised by 1023, so a cube covering the full Cineon range has to say so.
    /// Ignoring the declaration and assuming [0,1] would clip the top ~9 code values — the
    /// highlight shoulder, which is the part of a print stock people select it for.
    /// </summary>
    public float[] DomainMax { get; }

    /// <summary>What the cube expects on its input; see <see cref="LutInputEncoding"/>.</summary>
    public LutInputEncoding InputEncoding { get; }

    /// <summary>Display name, from the file's <c>TITLE</c> or else its filename.</summary>
    public string Title { get; }

    private CubeLut(int size, float[] data, float[] domainMin, float[] domainMax,
                    LutInputEncoding encoding, string title)
    {
        Size = size;
        _data = data;
        DomainMin = domainMin;
        DomainMax = domainMax;
        InputEncoding = encoding;
        Title = title;
    }

    /// <summary>
    /// Parses a <c>.cube</c> file. Throws <see cref="InvalidDataException"/> with a specific
    /// reason when the file is malformed — these messages reach the user, who picked the file.
    /// </summary>
    /// <param name="encoding">What the cube's input is authored against. Not discoverable from
    /// the file: .cube carries no encoding declaration, so it has to be stated by whoever knows
    /// which stock this is.</param>
    public static CubeLut Load(string path, LutInputEncoding encoding = LutInputEncoding.Cineon)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, Path.GetFileNameWithoutExtension(path), encoding);
    }

    /// <summary>
    /// Parses a cube from an already-open reader. Split out from <see cref="Load"/> so the
    /// built-in stocks can be parsed straight from an embedded resource stream — they have no
    /// path on disk to open, and writing them to a temp file just to read them back would be
    /// a filesystem round-trip in the middle of the render path.
    /// </summary>
    /// <param name="fallbackTitle">Used when the cube declares no TITLE, in place of the
    /// filename <see cref="Load"/> would have taken it from.</param>
    public static CubeLut Parse(TextReader reader, string fallbackTitle,
                                LutInputEncoding encoding = LutInputEncoding.Cineon)
    {
        int size = -1;
        string title = fallbackTitle;
        float[] domainMin = { 0f, 0f, 0f };
        float[] domainMax = { 1f, 1f, 1f };
        float[]? data = null;
        int written = 0;

        for (string? raw = reader.ReadLine(); raw is not null; raw = reader.ReadLine())
        {
            // '#' starts a comment anywhere on the line; the spec allows trailing comments.
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            switch (tok[0].ToUpperInvariant())
            {
                case "TITLE":
                    // Quoted string, possibly containing spaces.
                    title = line[tok[0].Length..].Trim().Trim('"');
                    if (title.Length == 0) title = fallbackTitle;
                    continue;

                case "LUT_3D_SIZE":
                    if (tok.Length < 2 || !int.TryParse(tok[1], out size) || size < 2 || size > 256)
                        throw new InvalidDataException($"LUT_3D_SIZE 无效：{line}");
                    data = new float[size * size * size * 3];
                    continue;

                case "LUT_1D_SIZE":
                    // A 1D cube is a valid .cube file but not what this path is for: a print-film
                    // emulation is inherently three-dimensional (its cross-channel coupling is the
                    // look). Rejecting is better than silently applying it per channel.
                    throw new InvalidDataException("这是 1D LUT，此处需要 3D LUT（LUT_3D_SIZE）。");

                case "DOMAIN_MIN":
                    ReadTriple(tok, line, domainMin);
                    continue;

                case "DOMAIN_MAX":
                    ReadTriple(tok, line, domainMax);
                    continue;

                // Resolve's own film-look cubes (and other older exports) state the input domain
                // as a single pair applied to all three channels rather than as DOMAIN_MIN/MAX.
                // Ignoring the keyword would leave the domain at its [0,1] default — which happens
                // to be right for those files, and therefore would go unnoticed until a cube that
                // declares something else silently sampled the wrong part of itself.
                case "LUT_3D_INPUT_RANGE":
                case "LUT_1D_INPUT_RANGE":
                    if (tok.Length < 3
                        || !float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                           out float lo)
                        || !float.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                                           out float hi))
                        throw new InvalidDataException($"需要两个数值：{line}");
                    domainMin[0] = domainMin[1] = domainMin[2] = lo;
                    domainMax[0] = domainMax[1] = domainMax[2] = hi;
                    continue;
            }

            // Anything else must be a data row.
            if (data == null)
                throw new InvalidDataException("文件在 LUT_3D_SIZE 之前就出现了数据行。");
            if (tok.Length < 3)
                throw new InvalidDataException($"数据行不足三个数值：{line}");
            if (written + 3 > data.Length)
                throw new InvalidDataException($"数据行数超过 LUT_3D_SIZE={size} 所要求的 {size * size * size} 行。");

            for (int c = 0; c < 3; c++)
            {
                if (!float.TryParse(tok[c], NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out float v))
                    throw new InvalidDataException($"无法解析数值：{line}");
                data[written++] = v;
            }
        }

        if (data == null || size < 2)
            throw new InvalidDataException("文件里没有 LUT_3D_SIZE。");
        if (written != data.Length)
            throw new InvalidDataException(
                $"数据行数不足：LUT_3D_SIZE={size} 需要 {size * size * size} 行，实际 {written / 3} 行。");

        for (int c = 0; c < 3; c++)
            if (!(domainMax[c] > domainMin[c]))
                throw new InvalidDataException("DOMAIN_MAX 必须大于 DOMAIN_MIN。");

        return new CubeLut(size, data, domainMin, domainMax, encoding, title);
    }

    private static void ReadTriple(string[] tok, string line, float[] into)
    {
        if (tok.Length < 4)
            throw new InvalidDataException($"需要三个数值：{line}");
        for (int c = 0; c < 3; c++)
            if (!float.TryParse(tok[c + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                out into[c]))
                throw new InvalidDataException($"无法解析数值：{line}");
    }

    /// <summary>
    /// Applies the cube to interleaved RGB in place, by tetrahedral interpolation.
    ///
    /// TETRAHEDRAL, NOT TRILINEAR, and the difference is visible rather than academic. Trilinear
    /// blends all eight corners of the enclosing cell, so along the neutral axis — where a print
    /// stock's response is steep and where every skin tone and sky gradient lives — it averages in
    /// six corners that are off-axis. The result is a slight desaturation and a faint blockiness
    /// that follows the LUT grid. Tetrahedral picks the one of six tetrahedra that actually
    /// contains the sample and blends its four corners, which keeps the neutral axis exact
    /// (the diagonal is an edge of every tetrahedron) and costs about the same.
    ///
    /// Input is expected already in the cube's declared domain — see <see cref="LogEncoding"/>.
    /// Values outside it are clamped to the domain, not extrapolated: a cube says nothing about
    /// what lies beyond its corners, and continuing the last cell's gradient invents highlight
    /// detail that the stock does not have.
    /// </summary>
    public void Apply(float[] data)
    {
        int n = Size;
        int last = n - 1;
        float[] lut = _data;

        // Domain → grid coordinate: (v - min) / (max - min) * (n - 1), folded into scale+bias.
        float sr = last / (DomainMax[0] - DomainMin[0]), br = -DomainMin[0] * sr;
        float sg = last / (DomainMax[1] - DomainMin[1]), bg = -DomainMin[1] * sg;
        float sb = last / (DomainMax[2] - DomainMin[2]), bb = -DomainMin[2] * sb;

        // Red varies fastest, per the .cube spec.
        int strideG = n * 3, strideB = n * n * 3;

        Parallel.For(0, data.Length / 3, i =>
        {
            int p = i * 3;

            float fr = Math.Clamp(data[p] * sr + br, 0f, last);
            float fg = Math.Clamp(data[p + 1] * sg + bg, 0f, last);
            float fb = Math.Clamp(data[p + 2] * sb + bb, 0f, last);

            int r0 = (int)fr, g0 = (int)fg, b0 = (int)fb;
            if (r0 > last - 1) r0 = Math.Max(0, last - 1);
            if (g0 > last - 1) g0 = Math.Max(0, last - 1);
            if (b0 > last - 1) b0 = Math.Max(0, last - 1);

            float dr = fr - r0, dg = fg - g0, db = fb - b0;

            int baseIdx = r0 * 3 + g0 * strideG + b0 * strideB;

            // c000 and c111 are corners of every one of the six tetrahedra; the other two vary.
            int c000 = baseIdx;
            int c111 = baseIdx + 3 + strideG + strideB;

            // Which tetrahedron the sample falls in is decided by the ordering of dr, dg, db.
            // Each branch names its two intermediate corners and the weights that go with them.
            int cA, cB;
            float wA, wB, w0, w1;

            if (dr >= dg)
            {
                if (dg >= db)        // dr >= dg >= db
                {
                    cA = baseIdx + 3;                       // R
                    cB = baseIdx + 3 + strideG;             // RG
                    w0 = 1f - dr; wA = dr - dg; wB = dg - db; w1 = db;
                }
                else if (dr >= db)   // dr >= db > dg
                {
                    cA = baseIdx + 3;                       // R
                    cB = baseIdx + 3 + strideB;             // RB
                    w0 = 1f - dr; wA = dr - db; wB = db - dg; w1 = dg;
                }
                else                 // db > dr >= dg
                {
                    cA = baseIdx + strideB;                 // B
                    cB = baseIdx + 3 + strideB;             // RB
                    w0 = 1f - db; wA = db - dr; wB = dr - dg; w1 = dg;
                }
            }
            else
            {
                if (db >= dg)        // db >= dg > dr
                {
                    cA = baseIdx + strideB;                 // B
                    cB = baseIdx + strideG + strideB;       // GB
                    w0 = 1f - db; wA = db - dg; wB = dg - dr; w1 = dr;
                }
                else if (db >= dr)   // dg > db >= dr
                {
                    cA = baseIdx + strideG;                 // G
                    cB = baseIdx + strideG + strideB;       // GB
                    w0 = 1f - dg; wA = dg - db; wB = db - dr; w1 = dr;
                }
                else                 // dg > dr > db
                {
                    cA = baseIdx + strideG;                 // G
                    cB = baseIdx + 3 + strideG;             // RG
                    w0 = 1f - dg; wA = dg - dr; wB = dr - db; w1 = db;
                }
            }

            data[p] = w0 * lut[c000] + wA * lut[cA] + wB * lut[cB] + w1 * lut[c111];
            data[p + 1] = w0 * lut[c000 + 1] + wA * lut[cA + 1] + wB * lut[cB + 1] + w1 * lut[c111 + 1];
            data[p + 2] = w0 * lut[c000 + 2] + wA * lut[cA + 2] + wB * lut[cB + 2] + w1 * lut[c111 + 2];
        });
    }
}
