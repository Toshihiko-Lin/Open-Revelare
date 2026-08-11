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

}
