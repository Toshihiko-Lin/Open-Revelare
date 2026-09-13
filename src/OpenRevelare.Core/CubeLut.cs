using System.Globalization;

namespace OpenRevelare.Core;

/// <summary>
/// What encoding a LUT expects on its input, i.e. what has to be true of the data before the
/// cube is sampled.
///
/// ONLY CINEON, BY DECISION (D-033). This pipeline's image processing is Cineon-based: Stage 1
/// ends in the shape Cineon names, and every cube it renders through is fed that signal. A LUT
/// authored against anything else — ACEScct, Log-C, DaVinci Intermediate, a finished Rec709
/// picture — would produce a plausible-looking but wrong picture, the worst failure mode there
/// is, because nothing errors. So the input side is not a choice the user is offered: a cube is
/// either made for Cineon (Resolve: timeline colour space <c>Cineon Film Log</c>) or it is not
/// usable here, and a header that declares otherwise is refused at parse time
/// (<see cref="CubeLut.NoteDeclaredInput"/>). The output side is where the contract lives
/// (<see cref="LutOutputEncoding"/>).
///
/// Kept as an enum rather than a constant so the declaration has a name and a source
/// (<see cref="LutInputEncodingSource"/>) and so a second member is an addition, not a rethink.
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
/// Where a cube's <see cref="CubeLut.InputEncoding"/> came from.
///
/// <para>
/// The distinction is the whole point of reading the header. A cube that SAYS "Input: Cineon Log"
/// and a cube that says nothing are both rendered as Cineon, but only the first one is evidence;
/// the second is this application's convention standing in for an answer. Collapsing them, which
/// is what the code did before, is what let a Log-C cube render as a plausible wrong picture.
/// </para>
/// </summary>
public enum LutInputEncodingSource
{
    /// <summary>
    /// The file declared nothing about its input, so <see cref="LutInputEncoding.Cineon"/> is
    /// assumed. Unchanged behaviour, and the only assumption still being made — but now it is
    /// reportable rather than invisible.
    /// </summary>
    ConventionalDefault,

    /// <summary>The file's own header names the encoding, and it is one this build implements.</summary>
    Declared,

    /// <summary>Supplied out of band by a caller that knows the stock; outranks the file.</summary>
    CallerSpecified,
}

/// <summary>
/// What the cube's output numbers are characterized as — which display encoding the LUT
/// renders INTO, so the pipeline knows how to decode it and where it may be used.
///
/// <para>
/// The <c>.cube</c> grammar has no field for this. Resolve's film-look exports state it in a
/// header comment and that is read as a prefill; a LUT generated from a grade says nothing, and
/// the roll declares it (<see cref="FrameParams.PrintLutOutput"/>, D-033). A cube whose output
/// is <see cref="Unknown"/> at render time — neither file nor roll says — still fails closed:
/// rendering through it would mean guessing what its numbers are.
/// </para>
///
/// <para>
/// THE OUTPUT DECIDES HOW THE LUT IS USED. The SDR members are display renderings that end in
/// paper white: on an SDR target they ARE the rendering; on an extended target their colour is
/// kept and their tone is opened up by the analytic shoulder family (D-034,
/// <see cref="ColorPipeline.ToExtendedOutputTargetViaPrint"/>). <see cref="Rec2020Pq"/> is an
/// HDR rendering with its own shoulder; it is used on an extended target and stands aside on an
/// SDR one, because an absolute-luminance picture has no SDR member to fall back to.
/// </para>
/// </summary>
public enum LutOutputEncoding
{
    Unknown,

    /// <summary>ITU-R BT.709 primaries under the BT.1886 2.4 power curve. Resolve: <c>Rec.709 Gamma 2.4</c>.</summary>
    Rec709,

    /// <summary>
    /// DCI-P3 primaries on the DCI white under a 2.6 power curve — the cinema projector target,
    /// and the other half of Resolve's shipped film looks. Not Display P3.
    /// </summary>
    DciP3,

    /// <summary>sRGB, IEC 61966-2-1 — primaries and piecewise curve. Resolve: <c>sRGB</c>.</summary>
    Srgb,

    /// <summary>
    /// BT.2020 primaries under ST 2084 PQ, absolute luminance — Resolve: <c>Rec.2100 ST2084</c>.
    /// The only HDR output; decoded to the linear extended carrier at 203 nits = 1.0.
    /// </summary>
    Rec2020Pq,
}

/// <summary>
/// The two facts a LUT needs stated before it can be rendered through: what it expects (always
/// Cineon here) and what it emits. Resolved per roll by <see cref="FrameParams.LutContractFor"/>
/// from the roll's own output declaration, falling back to whatever the cube's header prefilled.
/// </summary>
public readonly record struct LutContract(LutInputEncoding Input, LutOutputEncoding Output)
{
    /// <summary>Whether the cube renders into an HDR encoding, i.e. serves an extended target.</summary>
    public bool IsExtendedOutput => Output == LutOutputEncoding.Rec2020Pq;

    /// <summary>
    /// Whether the cube is applied on a target of the given range: an SDR print serves both
    /// (as the rendering on SDR, as the colour on HDR — D-034); an HDR LUT serves only an
    /// extended target (see <see cref="LutOutputEncoding"/>).
    /// </summary>
    public bool AppliesTo(OutputTarget target) =>
        Output != LutOutputEncoding.Unknown && (target.IsExtended || !IsExtendedOutput);

    public override string ToString() => $"{Input}->{Output}";
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

    /// <summary>
    /// How <see cref="InputEncoding"/> was arrived at. Only
    /// <see cref="LutInputEncodingSource.ConventionalDefault"/> is an assumption; the other two
    /// are statements by the file or by the caller.
    /// </summary>
    public LutInputEncodingSource InputEncodingSource { get; }

    /// <summary>
    /// The characterized encoding produced by the cube. External files default to
    /// <see cref="LutOutputEncoding.Unknown"/> because the file format cannot prove it.
    /// </summary>
    public LutOutputEncoding OutputEncoding { get; }

    /// <summary>Display name, from the file's <c>TITLE</c> or else its filename.</summary>
    public string Title { get; }

    /// <summary>
    /// SHA-256 over the parsed table — size, domain and every sample — as lowercase hex. What a
    /// render recipe records for a cube that came from a file: a path is where the file was on
    /// one machine, this is what it contained, so the same cube fingerprints the same anywhere
    /// (<see cref="OutputRecipe.PrintLutIdentity"/>). Comments and TITLE are excluded on
    /// purpose: they do not change a pixel.
    /// </summary>
    public string ContentIdentity { get; }

    private CubeLut(int size, float[] data, float[] domainMin, float[] domainMax,
                    LutInputEncoding encoding, LutInputEncodingSource encodingSource,
                    LutOutputEncoding outputEncoding, string title)
    {
        Size = size;
        _data = data;
        DomainMin = domainMin;
        DomainMax = domainMax;
        InputEncoding = encoding;
        InputEncodingSource = encodingSource;
        OutputEncoding = outputEncoding;
        Title = title;
        ContentIdentity = HashContent(size, domainMin, domainMax, data);
    }

    private static string HashContent(int size, float[] domainMin, float[] domainMax, float[] data)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> word = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(word, size);
        hash.AppendData(word);
        foreach (float[] block in new[] { domainMin, domainMax, data })
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Parses a <c>.cube</c> file. Throws <see cref="InvalidDataException"/> with a specific
    /// reason when the file is malformed — these messages reach the user, who picked the file.
    /// </summary>
    /// <param name="encoding">Out-of-band characterization from a caller that knows the stock.
    /// Null — the normal case — reads the file's own header, falling back to the Cineon
    /// convention only when the file declares nothing.</param>
    /// <param name="outputEncoding">Out-of-band characterization from a trusted asset manifest.
    /// Leave unknown to use whatever the file's own header declares, which is the normal case.</param>
    public static CubeLut Load(
        string path,
        LutInputEncoding? encoding = null,
        LutOutputEncoding outputEncoding = LutOutputEncoding.Unknown)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, Path.GetFileNameWithoutExtension(path), encoding, outputEncoding);
    }

    /// <summary>
    /// Parses a cube from an already-open reader. Split out from <see cref="Load"/> so the
    /// built-in stocks can be parsed straight from an embedded resource stream — they have no
    /// path on disk to open, and writing them to a temp file just to read them back would be
    /// a filesystem round-trip in the middle of the render path.
    /// </summary>
    /// <param name="fallbackTitle">Used when the cube declares no TITLE, in place of the
    /// filename <see cref="Load"/> would have taken it from.</param>
    /// <param name="encoding">Trusted out-of-band input characterization; null reads the file.</param>
    /// <param name="outputEncoding">Trusted out-of-band characterization. When left unknown, the
    /// file's own header declaration is used if it has one.</param>
    public static CubeLut Parse(
        TextReader reader,
        string fallbackTitle,
        LutInputEncoding? encoding = null,
        LutOutputEncoding outputEncoding = LutOutputEncoding.Unknown)
    {
        if (!Enum.IsDefined(outputEncoding))
            throw new ArgumentOutOfRangeException(nameof(outputEncoding));
        if (encoding is { } caller && !Enum.IsDefined(caller))
            throw new ArgumentOutOfRangeException(nameof(encoding));
        int size = -1;
        string title = fallbackTitle;
        float[] domainMin = { 0f, 0f, 0f };
        float[] domainMax = { 1f, 1f, 1f };
        float[]? data = null;
        int written = 0;
        LutOutputEncoding declaredOutput = LutOutputEncoding.Unknown;
        bool outputDeclarationSeen = false;
        string? declaredInput = null;

        for (string? raw = reader.ReadLine(); raw is not null; raw = reader.ReadLine())
        {
            // '#' starts a comment anywhere on the line; the spec allows trailing comments.
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0)
            {
                // Read the comment before discarding it: BOTH characterizations live there and
                // nowhere else.
                string comment = line[(hash + 1)..];
                NoteDeclaredOutput(comment, ref declaredOutput, ref outputDeclarationSeen);
                NoteDeclaredInput(comment, ref declaredInput);
                line = line[..hash];
            }
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

        // An explicit argument is out-of-band knowledge from a trusted manifest and outranks the
        // file; otherwise the file speaks for itself.
        LutOutputEncoding resolvedOutput = outputEncoding != LutOutputEncoding.Unknown
            ? outputEncoding
            : declaredOutput;

        (LutInputEncoding input, LutInputEncodingSource inputSource) = ResolveInput(encoding, declaredInput);

        return new CubeLut(size, data, domainMin, domainMax, input, inputSource, resolvedOutput, title);
    }

    /// <summary>
    /// Reads a header comment for the cube's declared display encoding, e.g. Resolve's
    /// <c>"# Display: ITU-Rec.709, Gamma 2.4"</c>.
    ///
    /// <para>
    /// This is the whole reason a user's Resolve-exported film look was rejected while the two
    /// built-in stocks were accepted: the built-ins were characterized by a human reading this
    /// very line and hard-coding the answer, while the parser threw the identical line away for
    /// everyone else. Reading it makes the built-ins ordinary rather than special.
    /// </para>
    ///
    /// <para>
    /// Deliberately narrow. <see cref="LutOutputEncoding.Rec709"/> means the 709 primaries AND a
    /// 2.4 display gamma, so both have to be named. A header that names 709 with some other curve
    /// is evidence AGAINST that pair, not for it, and correctly leaves the cube unknown.
    /// </para>
    ///
    /// <para>
    /// The first display line DECIDES, whether or not it matches. Continuing to scan after a
    /// non-matching declaration would let an unrelated later comment that happens to mention 709
    /// and 2.4 override a file that plainly said it was something else — the opposite of reading
    /// what the file declares.
    /// </para>
    /// </summary>
    private static void NoteDeclaredOutput(
        string comment,
        ref LutOutputEncoding declared,
        ref bool seen)
    {
        if (seen) return;

        string text = comment.ToLowerInvariant();
        if (!text.Contains("display", StringComparison.Ordinal)) return;

        seen = true;
        declared = ParseDisplayDeclaration(text);
    }

    /// <summary>
    /// The encodings a header's display line can name. Each needs BOTH halves — primaries and
    /// curve — because each member of <see cref="LutOutputEncoding"/> is a pair, and a line that
    /// names one half with some other partner is evidence against the pair, not for it.
    /// </summary>
    private static LutOutputEncoding ParseDisplayDeclaration(string text)
    {
        bool gamma24 = text.Contains("gamma", StringComparison.Ordinal) && text.Contains("2.4", StringComparison.Ordinal);
        bool gamma26 = text.Contains("gamma", StringComparison.Ordinal) && text.Contains("2.6", StringComparison.Ordinal);
        bool pq = text.Contains("2084", StringComparison.Ordinal) || text.Contains("pq", StringComparison.Ordinal);

        if (text.Contains("709", StringComparison.Ordinal) && gamma24) return LutOutputEncoding.Rec709;
        if (text.Contains("p3", StringComparison.Ordinal) && gamma26) return LutOutputEncoding.DciP3;
        if (text.Contains("srgb", StringComparison.Ordinal)) return LutOutputEncoding.Srgb;
        if ((text.Contains("2020", StringComparison.Ordinal) || text.Contains("2100", StringComparison.Ordinal)) && pq)
            return LutOutputEncoding.Rec2020Pq;
        return LutOutputEncoding.Unknown;
    }

    /// <summary>
    /// Records the cube's declared input encoding from a header comment, e.g. Resolve's
    /// <c>"#   Input: Cineon Log"</c>. The raw text is kept rather than a parsed enum so that
    /// <see cref="ResolveInput"/> can quote the file back to the user when it names something
    /// this build cannot honour.
    ///
    /// <para>
    /// DELIBERATELY STRICT about what counts as a declaration: the comment must begin with the
    /// word "input" and the very next non-space character must be a colon. Print-film cubes are
    /// full of comments that merely mention the word — "Input range", "generated from input.dpx" —
    /// and treating one of those as a declaration would reject a cube that works today. A missed
    /// declaration is safe (it falls back to the same Cineon convention as before); a false one is
    /// a regression, so the asymmetry is resolved in favour of missing.
    /// </para>
    ///
    /// <para>The first declaration DECIDES, for the reason spelled out on
    /// <see cref="NoteDeclaredOutput"/>: a later comment must not override what the file plainly
    /// said first.</para>
    /// </summary>
    private static void NoteDeclaredInput(string comment, ref string? declared)
    {
        if (declared is not null) return;

        string text = comment.TrimStart();
        if (!text.StartsWith("input", StringComparison.OrdinalIgnoreCase)) return;

        string rest = text[5..].TrimStart();
        if (rest.Length == 0 || rest[0] != ':') return;

        declared = rest[1..].Trim();
    }

    /// <summary>
    /// Decides the cube's input encoding from the caller's claim, then the file's, then
    /// convention — and refuses the file outright when it declares an encoding this build does
    /// not implement.
    ///
    /// <para>
    /// REFUSING IS THE POINT. Before this, every cube was fed Cineon-encoded data no matter what
    /// it was authored against, and an ACEScct or Log-C cube therefore produced a picture that was
    /// wrong but entirely plausible — nothing errored, nothing looked broken, and the only symptom
    /// was colour the user had no way to attribute. <see cref="LutInputEncoding"/> has warned about
    /// exactly this since it was written; the file was saying so all along and the parser was
    /// throwing the line away. A cube whose declared input cannot be honoured is as unusable as a
    /// 1D LUT, and is rejected the same way, with the file's own words in the message.
    /// </para>
    /// </summary>
    private static (LutInputEncoding Encoding, LutInputEncodingSource Source) ResolveInput(
        LutInputEncoding? caller,
        string? declared)
    {
        // Out-of-band knowledge outranks the file, exactly as it does for the output side: a
        // caller passing this in is asserting it knows which stock this is.
        if (caller is { } known) return (known, LutInputEncodingSource.CallerSpecified);

        if (declared is null) return (LutInputEncoding.Cineon, LutInputEncodingSource.ConventionalDefault);

        if (declared.Contains("cineon", StringComparison.OrdinalIgnoreCase))
            return (LutInputEncoding.Cineon, LutInputEncodingSource.Declared);

        throw new InvalidDataException(
            CoreText.F($"这个 LUT 的文件头声明它的输入是「{declared}」，而本程序只能提供 Cineon 编码的输入。")
            + CoreText.T("按 Cineon 喂给它会得到一张看起来正常、但颜色是错的图，所以这里直接拒绝。"
                         + "请在 Resolve 里把时间线色彩空间设为 Cineon Film Log 后重新生成 LUT。"));
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

        ParallelSweep.OverPixels(data.Length / 3, (from, to) =>
        {
          for (int p = from; p < to; p += 3)
          {
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
          }
        });
    }
}
