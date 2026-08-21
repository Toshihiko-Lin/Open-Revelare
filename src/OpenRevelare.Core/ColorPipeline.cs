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
        LogEncoding.ToCineon(data);
        CineonToDisplay(data);
        OutputRender.Convert(data, Working, output, mapping);
        OutputRender.Encode(data, output);
    }

    /// <summary>
    /// The pass-through path's display rendering: the standard Cineon log → display transform,
    /// the analytic equivalent of a Cineon→Rec709 conversion LUT.
    ///
    /// THIS IS THE STEP THE PASS-THROUGH PATH WAS MISSING, and its absence is what made the two
    /// exits disagree. A print-film cube IS a display rendering — it takes code 95 to its own toe
    /// and rolls the highlights off into its shoulder — while pass-through went from the linear
    /// positive straight to the output space's TRC with nothing in between. Stage 1 compensated by
    /// normalising the film base to linear zero, which put the black decision in the CALIBRATION
    /// rather than in the rendering and left D_min as the only control over a print stock's toe.
    ///
    /// A DECODE ALONE IS NOT THIS TRANSFORM. Undoing the log and handing the result to the output
    /// space's gamma is the identity in all but name: the picture arrives display-encoded but with
    /// none of the contrast a display rendering supplies, which reads on screen as a flat, grey,
    /// log-looking plate. The standard transform is a decode with a RESPONSE GAMMA folded in —
    /// Kodak's 0.6 — and that gamma is the contrast.
    ///
    ///   linear = 10^((code − 685) · 0.002 / 0.6)
    ///
    /// CODE 95 DOES NOT MAP TO DISPLAY BLACK, and forcing it to was a real defect. An earlier
    /// revision subtracted the value at 95 and renormalised, so that the film base came out pure
    /// black. That is not the standard, and it is not what any Cineon LUT does: measured on the
    /// real Kodak 2383 cube, code 95 renders at 0.037 and code 685 at 0.880 — the stock's own
    /// black and white, neither of them clamped. Subtracting shifted this curve away from every
    /// cube authored against the same encoding, and the shift showed up exactly where the two were
    /// compared: the mid-tones still agreed (2383's mid at code 486 against this transform's 474)
    /// while the shadows diverged badly (2383 reaches 0.18 at code 328, the subtracting version at
    /// 214). Two renderings of one encoding must not disagree about the shadows.
    ///
    /// So 95 renders as a grey here, near 0.15. That is correct and it is what the encoding means:
    /// 95 is the bottom of the CODE domain, not the bottom of a display. A picture's black comes
    /// from its own dark content, which sits above the base — not from the film base, which is
    /// merely the least dense thing on the negative.
    ///
    /// WHERE TO CHANGE THE LOOK, IF THE LOOK NEEDS CHANGING. This function is the log→display
    /// rendering, and it is the ONLY place a tonal decision belongs. The encoding upstream of it
    /// is calibration output: it says where the negative's two ends landed, and subtracting,
    /// clamping or renormalising anything there corrupts a measurement to buy an appearance. That
    /// is what the earlier black subtraction did, and the cost was that this curve no longer
    /// agreed with cubes authored against the same encoding.
    ///
    /// So if the pass-through picture wants different contrast or a different black, adjust THIS
    /// transform — its reference white, its response gamma, or replace it outright with a
    /// conversion LUT. Do not reach back into <see cref="LogEncoding"/> or the endpoints. The two
    /// exits stay comparable only as long as both consume the same untouched Cineon signal and
    /// differ solely in what they do with it.
    ///
    /// WHY 685 IS THE WHITE HERE BUT 1032 IS THE WHITE IN THE ENCODING. They answer different
    /// questions. <see cref="FrameParams.DMaxPerChannel"/> is the film's density ceiling and maps
    /// to 1032, the top of the encoding domain — a statement about the negative. 685 is where a
    /// PICTURE's white sits under the standard placement, with everything above it latitude. The
    /// encoding carries the whole negative; the rendering shows the picture. Codes above 685
    /// exceed 1 and clip when the output space encodes, which is what a conversion LUT does with
    /// them too.
    /// </summary>
    public static void CineonToDisplay(float[] data)
    {
        const double refWhite = 685.0;
        // Kodak's display response gamma for the Cineon transform. Not the output space's
        // encoding gamma — that is applied afterwards, by OutputRender.Encode.
        const double responseGamma = 0.6;
        const double codeFullScale = 1023.0;

        float scale = (float)(codeFullScale * FrameParams.CineonDensityPerCode / responseGamma);
        float white = (float)(refWhite / codeFullScale);

        Parallel.For(0, data.Length, i =>
            data[i] = MathF.Pow(10.0f, (data[i] - white) * scale));
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
