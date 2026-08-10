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
///                     ├─ InputTransform ──▶ WORKING SPACE (linear)
///   camera / scanner ─┘                         │
///                                            −log10
///                                               ▼
///                                        density domain — inversion, t_base,
///                                        wb_high/offset, grade. NO colour
///                                        operation belongs here: log is a
///                                        change of scale, not of primaries.
///                                               │
///                                             10^x
///                                               ▼
///                                     WORKING SPACE (linear positive)
///                                               │
///                        ┌──────────────────────┴──────────────────────┐
///                   OutputRender                                 OutputRender
///                        ▼                                             ▼
///                  export space                                 display space
///
/// The density domain has no primaries of its own — that is worth stating because it was a real
/// point of confusion. −log10 rescales each channel independently; it cannot change what the
/// channels mean. Whatever primaries entered the log domain are the ones that leave it.
/// </summary>
public static class ColorPipeline
{
    /// <summary>
    /// The space the density maths runs in, and the space its output is in.
    ///
    /// sRGB is the default because it is what the pipeline has always implicitly used, so
    /// declaring it changes nothing. A wider working space is the eventual goal — it would stop
    /// saturated colour being clipped before the output transform can place it — but Stage 2 is
    /// written against sRGB assumptions (contrast pivots on 0.5, curves clamp to [0,1], the luma
    /// weights are sRGB's), so widening it means reworking those seven operations first.
    /// </summary>
    public static ColorSpaceDef Working => ColorSpaces.Srgb;

    /// <summary>
    /// Renders a finished positive from the working space into <paramref name="destination"/>,
    /// gamut-mapping and encoding it.
    ///
    /// <paramref name="alreadyEncoded"/> is the awkward but necessary flag: under
    /// <see cref="OutputIntent.Basic"/> Stage 2 bakes the sRGB TRC in as its last step, for the
    /// preview as much as for the export, so callers on that path hand over encoded data. The
    /// TRC is inverted first in that case. Under NONE the data is still linear.
    /// </summary>
    public static void Render(float[] data, ColorSpaceDef destination, bool alreadyEncoded,
                              GamutMapping mapping = GamutMapping.Desaturate)
    {
        if (alreadyEncoded)
        {
            OutputRender.FromSrgbEncoded(data, destination, mapping);
            return;
        }
        OutputRender.Convert(data, Working, destination, mapping);
        OutputRender.Encode(data, destination);
    }
}
