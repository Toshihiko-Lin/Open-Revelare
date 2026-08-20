namespace OpenRevelare.Core;

/// <summary>
/// The colour-managed chain, stated once so every stage agrees on what space it is in.
///
/// The pipeline previously declared nothing. Decoded RAW went straight into the density maths and
/// the result was treated as sRGB because that was what got exported — an assumption, never a
/// conversion. Every colour problem chased in this codebase traces back to that gap, and
/// chroma_grade was the first patch over it: with no gamut to convert into, a scalar on the
/// density chroma was the only lever available.
///
/// The Cineon workflow this pipeline is modelled on has no such parameter, and the reason is
/// structural. Its four steps are:
///
///   1. decode to linear light with the camera profile applied
///   2. linear → Cineon log
///   3. invert and align the three channels IN THE LOG DOMAIN
///   4. log → Rec709: colour space AND gamma together
///
/// Step 4 restores chroma as a consequence of the colour-space conversion. Step 3 needs no chroma
/// operation at all, which is why "反相 + 三通道对齐" looks sufficient in DaVinci. This pipeline
/// did step 4's gamma but not its colour-space half, so the chroma never arrived and had to be
/// faked downstream.
///
/// Naming the spaces is therefore not bookkeeping; it is the fix:
///
///   input primaries  ─┐
///                     ├─ InputTransform ──▶ WORKING SPACE = ACEScg (scene-linear)
///   camera / scanner ─┘                         │
///                                            −log10                    steps 1–3
///                                               ▼
///                                        density domain — inversion, t_base,
///                                        wb_high/offset, grade. NO colour
///                                        operation belongs here: log is a
///                                        change of scale, not of primaries.
///                                               │
///                                             10^x
///                                               ▼
///                                        ACEScg (linear positive)
///                                               │
///                                        ToOutputSpace                 step 4
///                                     primaries AND gamma together
///                                               ▼
///                                    OUTPUT SPACE (display-encoded)
///                                               │
///                                            Stage 2                   adjustments
///                                    levels/contrast/curves/…
///                                               │
///                        ┌──────────────────────┴──────────────────────┐
///                        ▼                                             ▼
///                  exported file                              preview (unmanaged)
///                                     ── same pixels, WYSIWYG ──
///
/// The density domain has no primaries of its own — that is worth stating because it was a real
/// point of confusion. −log10 rescales each channel independently; it cannot change what the
/// channels mean. Whatever primaries entered the log domain are the ones that leave it.
///
/// WHY TWO SPACES AND NOT ONE. They answer different questions. The working space asks "how much
/// colour can survive the inversion", and the answer is "as much as possible", so it is the widest
/// gamut available. The output space asks "what do the adjustment controls MEAN", and that only
/// has an answer in a display-referred space where 0.5 is mid-grey and [0,1] are the endpoints.
/// One space cannot be both; conflating them is what limited the pipeline to sRGB.
/// </summary>
public static class ColorPipeline
{
    /// <summary>
    /// The SCENE-REFERRED working space: steps 1–3 of the Cineon workflow (linear decode, the
    /// log domain, inversion and three-channel alignment) all happen here.
    ///
    /// ACEScg, because its gamut encloses every space we render into. That is the whole point of
    /// putting it upstream: a saturated film dye that falls outside sRGB survives the inversion
    /// intact instead of being clipped before the output transform ever gets to place it. The
    /// density domain has no primaries of its own — −log10 rescales each channel independently —
    /// so whatever enters the log domain is what leaves it, and entering it wide is what keeps
    /// the colour available.
    ///
    /// This is DiVERE's arrangement, and it is why ACEScg belongs HERE rather than at Stage 2:
    /// it is scene-linear, and Stage 2's operations are display-referred by definition.
    /// </summary>
    public static ColorSpaceDef Working => ColorSpaces.AcesCg;

    /// <summary>
    /// The default step-4 target when a roll does not name one — the display space the positive is
    /// converted into, and therefore the space Stage 2 adjusts in and the space the file is
    /// written in.
    ///
    /// sRGB, because it is what an unmanaged viewer assumes and therefore the safe thing to hand
    /// someone. The Cineon workflow's step 4 names Rec709, and that space stays registered for
    /// projects that select it, but the two share primaries exactly — they differ only in transfer
    /// function (Rec709's pure 2.4 against sRGB's piecewise curve, visible only in the shadows) —
    /// so defaulting to Rec709 bought a subtly different picture for no gamut benefit.
    /// </summary>
    public static ColorSpaceDef DefaultOutput => ColorSpaces.Srgb;

    /// <summary>
    /// Step 4: convert a finished scene-linear positive from <see cref="Working"/> into
    /// <paramref name="output"/> and apply that space's encoding curve — "colour space AND gamma
    /// together", which is what restores brightness and contrast.
    ///
    /// After this the data is display-encoded in <paramref name="output"/>, which is exactly the
    /// precondition Stage 2 needs.
    /// </summary>
    public static void ToOutputSpace(float[] data, ColorSpaceDef output,
                                     GamutMapping mapping = GamutMapping.Desaturate)
    {
        OutputRender.Convert(data, Working, output, mapping);
        OutputRender.Encode(data, output);
    }

    /// <summary>
    /// Step 4 as the roll has configured it — with its print-film emulation if it names one,
    /// plain otherwise.
    ///
    /// The single entry point for every caller that renders a finished scene-linear positive for
    /// display or export. Callers used to reach for <see cref="ToOutputSpace"/> directly from
    /// four places (the region renderer, two preview paths, Stage 2); with a second route through
    /// step 4 that would be four places to remember the fork, and the preview would silently
    /// disagree with the export the first time one was missed.
    /// </summary>
    public static void ToOutputSpaceFor(float[] data, FrameParams cal)
    {
        ColorSpaceDef output = cal.ResolvedOutputSpace;
        if (PrintLuts.Resolve(cal.PrintLut) is CubeLut lut)
            ToOutputSpaceVia(data, lut, output);
        else
            ToOutputSpace(data, output);
    }

    /// <summary>
    /// The space a print-film cube renders INTO. Resolve's film looks — and every other cube of
    /// this kind — are authored to land on Rec709 with a 2.4 display gamma, which their headers
    /// state outright.
    ///
    /// Hard-coded rather than configurable because it is a fact about the cube, not a choice: a
    /// stock emulation has one output by construction, and offering a picker would invite the
    /// user to declare something the file already decided.
    /// </summary>
    private static ColorSpaceDef LutOutput => ColorSpaces.Rec709;

    /// <summary>
    /// Step 4 with a print-film emulation in the middle: scene-linear positive → Cineon log →
    /// the cube → <paramref name="output"/>.
    ///
    /// WHY THE CUBE REPLACES THE GAMUT MAP RATHER THAN FOLLOWING IT. A print stock emulation IS a
    /// display rendering transform — its shoulder, toe and cross-channel coupling are precisely a
    /// tone and gamut compression, fitted to a real stock. Running
    /// <see cref="GamutMapping.Desaturate"/> before it would compress the picture twice, once by
    /// a generic per-pixel rule and again by the stock's own curve, and the stock would be fed a
    /// signal already stripped of the saturation it was characterised against. So the conversion
    /// into the cube's primaries is a plain matrix and the cube does the rest.
    ///
    /// The exit converts the cube's Rec709 output into the roll's chosen space. When that space
    /// is Rec709 the whole exit is a no-op; otherwise it is a decode, a matrix and a re-encode —
    /// the same round trip <see cref="OutputRender.FromSrgbEncoded"/> performs, for the same
    /// reason (the render is finished, the container is not).
    /// </summary>
    public static void ToOutputSpaceVia(float[] data, CubeLut lut, ColorSpaceDef output)
    {
        // Into the cube's own primaries. Rec709 shares sRGB's, so for the common case this is the
        // same matrix the pass-through path applies — what differs is what happens after it.
        OutputRender.Convert(data, Working, LutOutput, GamutMapping.Clip);

        switch (lut.InputEncoding)
        {
            case LutInputEncoding.Cineon:
                LogEncoding.ToCineon(data);
                break;
            default:
                throw new NotSupportedException($"未支持的 LUT 输入编码：{lut.InputEncoding}");
        }

        lut.Apply(data);

        // The cube's output is display-encoded Rec709. Re-container it if the roll wants
        // something else; a Rec709 roll keeps the cube's values untouched.
        if (output != LutOutput)
        {
            OutputRender.Decode(data, LutOutput);
            OutputRender.Convert(data, LutOutput, output);
            OutputRender.Encode(data, output);
        }
    }
}
