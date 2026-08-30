using OpenRevelare.ColorManagement;

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
    /// The pass-through path's display rendering: the Cineon log → display transform, the
    /// analytic equivalent of a Cineon→Rec709 conversion LUT followed by a normalisation.
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
    /// log-looking plate. The transform is a decode with a RESPONSE GAMMA folded in, and that
    /// gamma is the contrast. A plain Colour Space Transform — decode without the gamma — is
    /// FLATTER still, not sharper: it renders the base near 0.29 against this transform's 0.
    ///
    ///   linear = (10^((code − 685) · 0.002 / 0.6) − k) / (1 − k),   k = the value at code 95
    ///
    /// CODE 95 MAPS TO DISPLAY BLACK, via that normalisation. This reverses an earlier judgement
    /// recorded here, and the reversal is worth stating because the earlier reasoning was checked
    /// and found wrong rather than merely re-weighed. The claim was that subtracting at 95 shifted
    /// the curve AWAY from cubes authored against the same encoding — "2383 reaches 0.18 at code
    /// 328, the subtracting version at 214". Measured, the normalised curve renders code 328 at
    /// 0.259 against 2383's 0.18, and code 250 at 0.172 against 2383's 0.10: it is CLOSER to the
    /// stock at both points than the un-normalised version (0.282 and 0.208) was. The old note had
    /// the direction of the error backwards. The mid-tone crossing, which is the one place the two
    /// renderings must agree, barely moves — 486 goes from 0.503 to 0.494.
    ///
    /// The film base therefore renders as black rather than as a grey. That is a deliberate
    /// departure from a literal reading of the standard, where 95 is merely the bottom of the CODE
    /// domain and renders around 0.10: the roll's calibration has already pinned the base to 95,
    /// so nothing a picture contains lies below it, and leaving it as a lifted grey read as a
    /// defect next to every print-stock cube. Real stocks do not go to zero either (2383 gives
    /// 0.037), so this is slightly deeper than a print — the trade is that the base and anything
    /// darker than it (sprocket cores, mask edges) collapse to a common 0.
    ///
    /// WHERE TO CHANGE THE LOOK, IF THE LOOK NEEDS CHANGING. This function is the log→display
    /// rendering, and it is the ONLY place a tonal decision belongs. The encoding upstream of it
    /// is calibration output: it says where the negative's two ends landed, and subtracting,
    /// clamping or renormalising THERE corrupts a measurement to buy an appearance. Note the
    /// distinction the earlier note lost: the prohibition covers <see cref="LogEncoding"/> and the
    /// endpoints, NOT this function. Normalising the rendering is a look decision, which is what
    /// this function is for; normalising the encoding would destroy the measurement both exits
    /// depend on.
    ///
    /// So if the pass-through picture wants different contrast, adjust THIS transform — its
    /// response gamma above all, which is empirical and documented as such at its declaration.
    /// Do not reach back into <see cref="LogEncoding"/> or the endpoints. The two exits stay
    /// comparable only as long as both consume the same untouched Cineon signal and differ solely
    /// in what they do with it.
    ///
    /// WHY 685 IS THE WHITE HERE BUT 1032 IS THE WHITE IN THE ENCODING. They answer different
    /// questions. <see cref="FrameParams.DMaxPerChannel"/> is the film's density ceiling and maps
    /// to 1032, the top of the encoding domain — a statement about the negative. 685 is where a
    /// PICTURE's white sits under the standard placement, with everything above it latitude. The
    /// encoding carries the whole negative; the rendering shows the picture.
    ///
    /// That latitude is ROLLED OFF rather than clipped — see <see cref="Shoulder"/>. It used to
    /// clip, which silently discarded the 2.31 stops between 685 and 1032 and made every switch to
    /// a print-film cube look like the cube had darkened the highlights, when in fact the cube was
    /// the only one of the two keeping them.
    ///
    /// THE OTHER END IS CORRECTED TOO — see <see cref="Toe"/>. The base normalisation a few lines
    /// below is a subtraction, and a subtraction flattens ratios near the point it subtracts, so
    /// the shadows arrived carrying separation that was the arithmetic's rather than the
    /// negative's. Both corrections are LOCAL, and deliberately so: the toe ends at 0.05 and the
    /// shoulder starts at 0.5, which leaves mid-grey, the mid-tone crossing and diffuse white set
    /// by the decode and the response gamma alone.
    /// </summary>
    public static void CineonToDisplay(float[] data)
    {
        const double refWhite = 685.0;
        // The response gamma folded into the transform, and the source of its contrast. Not the
        // output space's encoding gamma — that is applied afterwards, by OutputRender.Encode.
        //
        // 0.6 is the average gamma of a print stock's D-logE curve: a negative's density
        // difference ΔD prints onto positive stock as roughly 0.6·ΔD, so dividing by it asks
        // "how bright would this negative density be once printed". It is an EMPIRICAL typical
        // value, not a defined constant of the encoding the way 685 and 0.002 are — real stocks
        // and processes vary around it. It is therefore the legitimate knob for overall contrast,
        // and lowering it (0.5 lands the shadows nearly on 2383's measured points) is a look
        // decision rather than a departure from the standard.
        const double responseGamma = 0.6;
        const double codeFullScale = 1023.0;

        float scale = (float)(codeFullScale * FrameParams.CineonDensityPerCode / responseGamma);
        float white = (float)(refWhite / codeFullScale);

        // The black end, in the linear domain this function outputs. Code 685 is 1 by
        // construction (it is the exponent's zero); code 95 is where the calibrated film base
        // lands, and normalising by it is what takes the base to display black.
        float blackNorm = (float)(FrameParams.CineonBlackCode / codeFullScale);
        float blackLin = MathF.Pow(10.0f, (blackNorm - white) * scale);
        float span = 1.0f - blackLin;

        ParallelSweep.Over(data.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float lin = MathF.Pow(10.0f, (data[i] - white) * scale);
                data[i] = Shoulder(Toe(MathF.Max((lin - blackLin) / span, 0.0f)));
            }
        });
    }

    /// <summary>
    /// Where the toe ends, in the normalised linear domain. Above it the transform is untouched.
    ///
    /// 0.05 sits just below 18% mid-grey, which lands at 0.0585. That is the binding constraint
    /// rather than a rounded preference: mid-grey, the mid-tone crossing and the diffuse white are
    /// all set by the decode and the response gamma, and a toe reaching past 0.0585 would start
    /// moving them. Everything from mid-grey up passes through unchanged.
    /// </summary>
    private const float ToeKnee = 0.05f;

    /// <summary>
    /// The exponent applied below <see cref="ToeKnee"/>. 1.0 would be no toe at all.
    ///
    /// 1.3 is measured against the shadow launch rather than fitted to a stock. Out of the film
    /// base the bare transform climbs at 31.4 output levels per 100 code — 9.1× the 3.45 measured
    /// on Kodak 2383 — because the base normalisation is a subtraction and a subtraction flattens
    /// ratios near the point it subtracts. At 1.3 the launch is 5.2× the stock's, which is still
    /// clearly more open than a print (correct for a rendering meant to be neutral) while no
    /// longer manufacturing separation the negative does not contain. It also lands code 150 at
    /// 9.9 against the stock's 9.6, which is the corroboration rather than the target.
    /// </summary>
    private const float ToeGamma = 1.3f;

    /// <summary>
    /// Restores the shadow ratios the film-base normalisation flattens.
    ///
    /// WHAT THIS FIXES. Taking the base to black is a SUBTRACTION, <c>(lin − base)/(1 − base)</c>,
    /// and a subtraction destroys ratios near the point it subtracts. Two shadow values a few per
    /// cent apart in the negative arrive within a few thousandths of each other, and the output
    /// TRC — whose near-black segment is steep by design — magnifies that residue into tens of
    /// visible levels. The result reads as lifted, muddy shadows with no density: the separation
    /// on screen is not detail the negative recorded, it is the normalisation's own arithmetic
    /// being amplified.
    ///
    /// The toe undoes exactly that amplification and nothing else. It is a power curve below the
    /// knee, pinned at both ends — 0 stays 0 and the knee is the identity — so the film base still
    /// renders as black and no tone from mid-grey up moves at all. It is the shadow counterpart of
    /// <see cref="Shoulder"/>, and for the same reason: a correction aimed at one end of the range
    /// has to be local to that end, or it drags the placements the rest of the transform exists to
    /// establish.
    ///
    /// NOT C¹ AT THE KNEE, unlike <see cref="Shoulder"/>. The slope steps from
    /// <see cref="ToeGamma"/>× to 1× there. It is invisible in practice because the knee sits at
    /// 0.05, where the output TRC is already compressing hard, and buying continuity would mean a
    /// curve with a free parameter to fit rather than one number with a measured justification.
    /// </summary>
    private static float Toe(float v)
    {
        if (v >= ToeKnee) return v;
        return ToeKnee * MathF.Pow(v / ToeKnee, ToeGamma);
    }

    /// <summary>
    /// The knee where the shoulder starts, in the normalised linear domain
    /// <see cref="CineonToDisplay"/> works in. Below it the transform is untouched.
    ///
    /// 0.5 is not a free choice: it is what lands code 685 on 0.881 once the output space encodes,
    /// against the 0.880 measured on the real Kodak 2383 cube. Raising it to 0.6 gives 0.906 and
    /// lowering it to 0.4 gives 0.854, so this is the value that makes the two renderings agree at
    /// the diffuse white. In code terms the knee sits at 596, so everything from the film base up
    /// through the mid-tones passes through unchanged.
    /// </summary>
    private const float ShoulderKnee = 0.5f;

    /// <summary>
    /// Rolls the highlights off instead of letting them clip, so that codes above Cineon's diffuse
    /// white survive to the screen.
    ///
    /// WHAT THIS FIXES. The encoding carries the whole negative: <see cref="FrameParams.DMaxPerChannel"/>
    /// maps to code 1032, while 685 is only where a PICTURE's white sits, leaving 347 codes — 2.31
    /// stops — of latitude above it. Without a shoulder that entire span rendered as 1.0 and the
    /// output space's encoder clamped it away, so the standard rendering was discarding two and a
    /// third stops of measured data. It showed up on every switch to a print-film cube: the cube
    /// keeps that latitude (2383 puts 685 at 0.880 and spreads the rest between there and white),
    /// so a region that had been flat paper-white suddenly acquired detail and read as "the LUT
    /// darkened my highlights". Nothing was darkened — the standard path had been burning them.
    ///
    /// A Reinhard roll-off: everything below <see cref="ShoulderKnee"/> is identity, and above it
    /// the remaining range is compressed asymptotically toward 1 so nothing ever reaches it. That
    /// keeps two properties worth having — the mid-tones are bit-identical to what they were
    /// (code 486 stays at 0.494), and no input, however dense, can clip.
    ///
    /// The curve is C¹ at the knee, so there is no visible seam where it engages.
    /// </summary>
    private static float Shoulder(float v)
    {
        if (v <= ShoulderKnee) return v;
        const float headroom = 1.0f - ShoulderKnee;
        float d = v - ShoulderKnee;
        return ShoulderKnee + headroom * d / (d + headroom);
    }

    /// <summary>
    /// Step 4 as the roll has configured it — its print-film emulation if it names one, and the
    /// standard display rendering otherwise.
    ///
    /// THE TWO ARE NOT VARIATIONS OF ONE THING. They differ in who performs the display rendering:
    ///
    ///   • A CUBE performs it, fitted to a real stock. Its toe and shoulder are the look.
    ///   • The STANDARD path performs it analytically — <see cref="CineonToDisplay"/>, a decode
    ///     with Kodak's response gamma folded in and the film base normalised to display black.
    ///     This is a display rendering, not a bare conversion: it is doing the job a cube would.
    ///
    /// Every path here RENDERS. There was briefly a third — a pure CST that decoded the encoding
    /// and stopped, handing back a flat log plate for grading elsewhere — and it was removed
    /// because nobody used it: an unrendered picture is a step in someone else's pipeline, not a
    /// look a roll picks. The default is the standard rendering, because a roll that names nothing
    /// should show a picture.
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
    /// Versioned step-4 boundary. LegacyV1 is the exact overload above. ManagedV2 keeps a
    /// print-film cube's native output characterized as Rec709 and then asks the injected CMM to
    /// convert those colours into the exact selected output profile.
    /// </summary>
    public static void ToOutputSpaceFor(
        float[] data,
        FrameParams cal,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
        {
            ToOutputSpaceFor(data, cal);
            return;
        }
        if (pipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version.");

        ArgumentNullException.ThrowIfNull(colorManagement);
        ColorSpaceDef output = cal.ResolvedOutputSpace;
        CubeLut? lut = PrintLuts.Resolve(cal.PrintLut);
        if (lut is not null)
            ToOutputSpaceVia(data, lut, output, pipelineVersion, colorManagement);
        else if (!string.IsNullOrWhiteSpace(cal.PrintLut))
        {
            throw new NotSupportedException(
                $"ManagedV2 cannot use configured print LUT '{cal.PrintLut}' because it could " +
                "not be loaded and its native output encoding therefore cannot be proven.");
        }
        else
            ToOutputSpace(data, output);
    }

    /// <summary>
    /// The native space supported for managed print-film rendering. Resolve's two embedded film
    /// looks are authored to land on Rec709 with a 2.4 display gamma, which their trusted asset
    /// headers state outright. Generic user cubes are not assumed to share that contract; their
    /// <see cref="CubeLut.OutputEncoding"/> remains unknown and ManagedV2 rejects them.
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
    /// THE EXIT DOES NOT RE-APPLY A TONE CURVE, AND THAT IS THE WHOLE POINT. The cube's output is
    /// a FINISHED display rendering — the stock's toe and shoulder ARE the tone curve, already
    /// baked into the numbers. What the roll's output space still gets to decide is its PRIMARIES;
    /// what it does not get to decide is the tone response, because the cube has spent it.
    ///
    /// It used to decode with Rec709's curve and re-encode with the destination's, on the reading
    /// that "the render is finished, the container is not". That reading is right for
    /// <see cref="OutputRender.FromSrgbEncoded"/>, whose input really is sRGB-encoded data being
    /// re-containered. It is wrong here, because Rec709's 2.4 power and sRGB's piecewise curve are
    /// genuinely different transfer functions: converting between them is a legitimate operation
    /// that legitimately CHANGES the numbers, and applying it to a finished render re-interprets
    /// the stock's own toe as if it had been a container artefact.
    ///
    /// Measured on the real Kodak 2383 cube: the film base at Cineon code 95 leaves the cube at
    /// 3.74% luminance, and the round trip delivered it to an sRGB roll at 0.49% — under the 2%
    /// mark <see cref="ClippingDetect"/> flags, so the base and every shadow the stock had placed
    /// below 0.0675 lit up as under-exposed in an sRGB or Display P3 roll while looking correct in
    /// a Rec709 one. Nothing was under-exposed; the exit was crushing them.
    ///
    /// This mirrors what a finishing application does with a rendered frame — DaVinci's Grab Still
    /// writes the timeline's display-encoded values out as they stand and lets the container
    /// DECLARE the space, rather than re-transforming a finished picture on the way to disk. The
    /// declaration is <see cref="IccProfiles"/>'s job here, and it now reads the same
    /// <see cref="TransferFunction"/> field this path respects.
    ///
    /// So a wider-gamut roll (Display P3, AdobeRGB) still gets its matrix — the cube's Rec709
    /// primaries are mapped into the destination's, in the linear light the tone curve implies —
    /// but the curve that goes back on is the cube's own, never the destination's.
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

        // Primaries only. Same primaries (sRGB, Rec709) ⇒ nothing to do at all: the cube's values
        // pass through byte-for-byte, which is what keeps an sRGB roll agreeing with a Rec709 one.
        if (output.Red == LutOutput.Red && output.Green == LutOutput.Green
            && output.Blue == LutOutput.Blue && output.White == LutOutput.White)
            return;

        // A real gamut change. Undo the cube's OWN curve to reach linear light, rotate the
        // primaries there, then put the cube's own curve back — not the destination's, which
        // would re-grade the finished render.
        OutputRender.Decode(data, LutOutput);
        OutputRender.Convert(data, LutOutput, output);
        OutputRender.Encode(data, LutOutput);
    }

    /// <summary>
    /// Versioned print-film rendering. LegacyV1 delegates byte-for-byte to the overload above.
    /// ManagedV2 treats the cube result as exact Rec709-encoded RGB and performs one complete CMM
    /// transform into the selected exact profile; it never preserves Rec709 code values while
    /// changing only the profile label.
    /// </summary>
    public static void ToOutputSpaceVia(
        float[] data,
        CubeLut lut,
        ColorSpaceDef output,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
        {
            ToOutputSpaceVia(data, lut, output);
            return;
        }
        if (pipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version.");

        ArgumentNullException.ThrowIfNull(colorManagement);
        if (lut.OutputEncoding != LutOutputEncoding.Rec709)
        {
            throw new NotSupportedException(
                $"ManagedV2 cannot use print LUT '{lut.Title}' because its output profile is " +
                "unknown. Select one of the characterized built-in Rec709 LUTs or supply an " +
                "asset descriptor that proves the cube's native output encoding.");
        }

        // Render only into the cube's declared native output. The legacy overload's manual
        // primaries-only exit is intentionally not called here: it creates the hybrid
        // target-primaries/Rec709-TRC encoding that ManagedV2 exists to remove.
        OutputRender.Convert(data, Working, LutOutput, GamutMapping.Clip);
        switch (lut.InputEncoding)
        {
            case LutInputEncoding.Cineon:
                LogEncoding.ToCineon(data);
                break;
            default:
                throw new NotSupportedException($"Unsupported LUT input encoding: {lut.InputEncoding}");
        }
        lut.Apply(data);

        ConvertPrintLutNativeOutput(data, output, colorManagement);
    }

    /// <summary>
    /// Converts pixels already rendered into the print cube's native Rec709 encoding to the exact
    /// selected profile before target-space Stage 2. The source array is replaced only after
    /// transform creation and application both succeed.
    /// </summary>
    internal static void ConvertPrintLutNativeOutput(
        float[] data,
        ColorSpaceDef output,
        IColorManagementEngine colorManagement)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (data.Length % 3 != 0)
            throw new ArgumentException("Print-LUT RGB data length must be divisible by three.", nameof(data));

        ColorProfileRef nativeProfile = BuiltInColorProfiles.Rec709(ProfileRole.Input);
        ColorProfileRef outputProfile = BuiltInColorProfiles.For(output, ProfileRole.Output);
        if (nativeProfile.Identity == outputProfile.Identity)
            return;

        var request = new ColorTransformRequest(
            nativeProfile,
            outputProfile,
            TransformPurpose.PrintLutOutput,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            adaptationState: 1.0);
        float[] converted = new float[data.Length];

        try
        {
            using (IColorTransformLease transform = colorManagement.Lease(request))
                transform.Apply(data, converted, data.Length / 3);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new ColorManagementException(
                $"Managed print-LUT output conversion failed ({nativeProfile.Identity} -> " +
                $"{outputProfile.Identity}, output={output.Name}); the native pixels were left unchanged.",
                ex);
        }

        converted.CopyTo(data, 0);
    }

}
