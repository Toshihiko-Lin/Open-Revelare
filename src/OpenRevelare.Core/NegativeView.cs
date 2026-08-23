namespace OpenRevelare.Core;

/// <summary>
/// The display transform for the UN-INVERTED negative — the film-base / highlight sampling view.
///
/// This is not part of the pipeline and deliberately so. Everything the pipeline measures reads
/// the UniWB decode (see <see cref="RawDecode"/>), because the density maths wants the sensor's
/// own untouched numbers. But UniWB is a DECODE baseline, not a viewing one: a Bayer sensor's
/// green channel has roughly twice the response of red and blue, so an un-inverted frame shown
/// at unit gain reads green, and the orange film base the user is being asked to point at does
/// not look orange. Undoing that for display — and only for display — is what this does.
///
/// One place, called by both the whole-frame view and the sharp patch that blits over it, because
/// those two are the same picture at two resolutions: any difference between them shows up as the
/// patch flashing a different colour the moment the user zooms in.
///
/// GEOMETRY IS NOT HERE, and is not skipped either. The view runs the frame through the SAME
/// orientation → straighten → crop chain as the positive it toggles with, so the two are the same
/// rectangle of the same frame and only the photometry differs. That belongs to the render paths
/// (<c>MainViewModel.GeometryForNegative</c> and <see cref="RegionRender"/>'s shared geometry map),
/// not to this file, which only ever touches pixel VALUES.
/// </summary>
public static class NegativeView
{
    /// <summary>
    /// Luminance weights of <see cref="ColorPipeline.Working"/> — the Y row of its RGB→XYZ matrix,
    /// the same derivation <c>Stage2</c> uses for the space IT runs in. ACEScg because this gain is
    /// applied while the buffer is still scene-linear working space.
    /// </summary>
    private static readonly double LumaR, LumaG, LumaB;

    static NegativeView()
    {
        double[,] toXyz = ColorPipeline.Working.ToXyz();
        LumaR = toXyz[1, 0];
        LumaG = toXyz[1, 1];
        LumaB = toXyz[1, 2];
    }

    /// <summary>
    /// Take an un-inverted frame from the scene-linear working space to
    /// <paramref name="output"/> for DISPLAY — the negative view's step 4, and deliberately not
    /// <see cref="ColorPipeline.ToOutputSpace"/>.
    ///
    /// WHAT THIS DOES: the primaries conversion and the output space's encoding curve, and nothing
    /// else. That is what an image viewer does with a file, and looking like the image viewer is
    /// the whole specification for this view — the user opens it to judge the negative against
    /// what they already know the scan looks like.
    ///
    /// WHY NOT ToOutputSpace, which is what this used to call. That path is the pipeline's step 4
    /// and carries a DISPLAY RENDERING with it: Cineon encode → CineonToDisplay → convert →
    /// encode. Both of the first two steps are statements about a CALIBRATED POSITIVE.
    /// <see cref="LogEncoding.ToCineon"/> maps linear 1.0 to code 1032, which is only meaningful
    /// once inversion has normalised the film's density ceiling to 1.0, and
    /// <see cref="ColorPipeline.CineonToDisplay"/> then renders against code 685 and takes code 95
    /// — the CALIBRATED film base — to display black.
    ///
    /// The negative view has had none of that done to it. It shows raw scene-linear sensor values
    /// with no inversion and no calibration, so nothing has put the frame anywhere in particular
    /// and the encode simply ran wherever the exposure happened to sit. Code 1032 is 347 codes
    /// above display white — 347·0.002/0.6 in log10, i.e. +3.84 EV — so a frame near unity blew
    /// out: measured through the old chain, scene-linear 0.2 rendered 224/255 and everything from
    /// 0.4 up pinned to 246-253/255. That is the "raw looks too bright" report, and it was an
    /// exposure fault in the DOMAIN rather than a colour one, which is why correcting the white
    /// balance alone did not shift it.
    ///
    /// A viewer transform has no such assumption to violate: it takes the numbers in the file at
    /// face value. The negative comes out looking like the negative does in any other application,
    /// which is both correct and the thing the user is comparing against.
    /// </summary>
    public static void ToDisplay(float[] data, ColorSpaceDef output)
    {
        OutputRender.Convert(data, ColorPipeline.Working, output);
        OutputRender.Encode(data, output);
    }

    /// <summary>
    /// Multiply in place by a LUMINANCE-PRESERVING white balance. Null or a non-finite gain is a
    /// no-op, so a caller with no camera coefficients passes what it has and gets the UniWB
    /// picture rather than having to branch.
    ///
    /// THE GAINS ARE RENORMALISED FIRST. A white balance is entitled to the RATIOS between the
    /// channels and to nothing else — the level is exposure, and this view has no exposure control
    /// to answer for it. The caller's vector is green-normalised
    /// (<see cref="RawDecode.CameraWhiteBalance"/>), which pins GREEN at unit gain but not
    /// BRIGHTNESS: as-shot coefficients run roughly 2.2 / 1.0 / 1.5 under daylight, so red and blue
    /// are scaled UP against a green that stays put and the picture comes out a third of a stop
    /// hot. Green carries most of the luminance, but "most" is 0.67, not 1.
    ///
    /// So divide through by the gain vector's own luminance: the ratios survive untouched and a
    /// neutral pixel keeps its luminance exactly.
    ///
    /// PURELY MULTIPLICATIVE, and applied BEFORE the output-space encode: the buffer is
    /// scene-linear at this point, which is the only domain where a white balance is a per-channel
    /// scale. No clamp — values above 1 stay above 1 and are dealt with by the encode, the same
    /// way the positive path treats them.
    /// </summary>
    public static void ApplyWhiteBalance(float[] data, double[]? gains)
    {
        if (gains is not { Length: 3 }) return;
        double dr = gains[0], dg = gains[1], db = gains[2];
        if (!double.IsFinite(dr) || !double.IsFinite(dg) || !double.IsFinite(db)) return;

        // Renormalise onto constant luminance. A degenerate vector has no meaningful level to
        // divide by, so it is dropped rather than turned into an arbitrary scale.
        double lum = LumaR * dr + LumaG * dg + LumaB * db;
        if (!(lum > 1e-9) || !double.IsFinite(lum)) return;
        dr /= lum; dg /= lum; db /= lum;

        float gr = (float)dr, gg = (float)dg, gb = (float)db;
        if (!float.IsFinite(gr) || !float.IsFinite(gg) || !float.IsFinite(gb)) return;
        if (gr == 1f && gg == 1f && gb == 1f) return;

        for (int i = 0; i + 2 < data.Length; i += 3)
        {
            data[i]     *= gr;
            data[i + 1] *= gg;
            data[i + 2] *= gb;
        }
    }
}
