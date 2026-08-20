namespace OpenRevelare.Core;

/// <summary>
/// The bridge between the pipeline's density domain and a print-film LUT's input.
///
/// WHY THIS IS AN AFFINE MAP AND NOT A CONVERSION. Stage 1 already ends in the shape a Cineon-log
/// LUT wants. <see cref="DensityEndpoints"/> pins each channel so the sampled black lands at
/// <c>D_adj = -OutputRange</c> and the highlight at <c>D_adj = 0</c>. So the density domain and
/// Cineon code values are the same line in different units, and getting from one to the other is a
/// scale and a shift — no curve, no table, nothing to be approximate about.
///
///   code = 685 + D_adj / step            D_adj ∈ [-1.874, 0] → code ∈ [95, 685]
///   v    = code / 1023                                       → v ∈ [0.0929, 0.6696]
///
/// WHERE THE TWO ANCHORS COME FROM. The white end is Cineon's 90% diffuse white (685), NOT the
/// 1032 headroom ceiling; see <see cref="WhiteCode"/> for the measurement that settled it. Placing
/// the roll's white at 1032 put the entire picture inside a print stock's shoulder and no amount
/// of recalibrating D_max could recover it, because that moves the picture without changing its
/// span. The step then FOLLOWS from the two anchors rather than being declared — pinning both ends
/// and the step over-determines the map.
///
/// WHY IT LIVES HERE RATHER THAN IN THE INVERSION. The inversion has no business knowing that a
/// LUT exists downstream; it states the two endpoints and stops. This is the piece that says what
/// those endpoints mean to a consumer that speaks code values.
/// </summary>
public static class LogEncoding
{
    /// <summary>10-bit full scale — the divisor that turns a Cineon code into a normalised
    /// float. NOT 1024: the code range is 0..1023, so 1023 is what maps to 1.0.</summary>
    private const double CodeFullScale = 1023.0;

    /// <summary>
    /// The code the roll's WHITE end lands on — Cineon's 90% diffuse white reference.
    ///
    /// NOT 1032, and this is the crux of the whole path. 1032 is the top of the encoding's
    /// headroom: the ceiling for specular overshoot, not the value a picture's white sits at.
    /// A print-film cube is characterised against the standard placement, where a scene's white
    /// is 685 and everything from there to 1023 is latitude the stock rolls off.
    ///
    /// Sending <see cref="FrameParams.DMaxPerChannel"/> to 1032 therefore asked 2383 to render
    /// the whole picture inside its shoulder. Measured on the real cube: the stock's useful
    /// input span is codes 211..908, i.e. 697 codes, while <see cref="FrameParams.OutputRange"/>
    /// stretches the sampled black-to-white across 937 — 1.34x too wide. 26% of the range fell
    /// into the dead flats beyond the toe and shoulder, which is exactly the reported symptom:
    /// shadows and highlights piling up, harsh transitions, and hue shifts in the clipped
    /// highlights (the three channels have unequal shoulders, so they saturate at different
    /// rates). Crucially it could not be corrected by moving D_max, because D_max only SHIFTS
    /// the picture along the code axis while the span stays pinned by OutputRange — push one end
    /// in and the other end falls out.
    ///
    /// Anchoring white at 685 puts the span at 590 codes, inside the stock's working region,
    /// with the toe and shoulder left as the latitude they are meant to be. The resulting
    /// response measures a local gamma near 1.0 in the highlights rising to ~1.4-2.0 through the
    /// midtones — a print curve, which is the point of selecting a print stock.
    ///
    /// THIS DOES NOT REINTERPRET THE CALIBRATION. D_max is still the roll's measured highlight
    /// and still means what it did; what changes is only which code this path hands it to, and
    /// only on this path. Pass-through is untouched — it has no cube to fit and renders exactly
    /// as before, bit for bit.
    /// </summary>
    private const double WhiteCode = 685.0;

    /// <summary>
    /// The code the roll's BLACK end lands on — the bottom of the Cineon domain.
    ///
    /// Kept at 95 rather than pushed up to the cube's measured toe (211). The toe is not a defect
    /// to be avoided: a print stock's shadow roll-off is part of its look, and mapping the
    /// sampled black above it would render film with no toe at all. 95 is also where
    /// <see cref="FrameParams.OutputRange"/>'s black already sits by definition, so the two ends
    /// stay the pair the encoding names.
    /// </summary>
    private const double BlackCode = 95.0;

    /// <summary>
    /// Density per code ON THIS PATH — derived from the two anchors, not Cineon's 0.002.
    ///
    /// The roll's span is <see cref="FrameParams.OutputRange"/> density laid across
    /// <c>WhiteCode - BlackCode</c> codes, so the step follows from the anchors rather than being
    /// declared independently. Writing 0.002 here instead would over-determine the mapping: the
    /// two ends and the step cannot all three be chosen freely, and pinning all three is what
    /// produced the 937-code span in the first place.
    /// </summary>
    private const double DensityPerCode = FrameParams.OutputRange / (WhiteCode - BlackCode);

    /// <summary>
    /// Normalised Cineon value for the black end, <c>D_adj = -OutputRange</c>. ≈ 0.0929.
    ///
    /// Exposed because it is the pivot-adjacent constant a log-domain operation would need: its
    /// endpoints are this and <see cref="White"/>, not 0 and 1.
    /// </summary>
    public static readonly float Black = (float)(BlackCode / CodeFullScale);

    /// <summary>Normalised Cineon value for the white end, <c>D_adj = 0</c>. ≈ 0.6696.</summary>
    public static readonly float White = (float)(WhiteCode / CodeFullScale);

    /// <summary>
    /// Where an 18% scene grey lands under this mapping — <c>white - log10(1/0.18)</c> in code
    /// terms, i.e. around code 451. Reported for diagnostics rather than used by the transform;
    /// the anchors above define the mapping on their own.
    /// </summary>
    public static readonly float MidGrey =
        (float)((WhiteCode + Math.Log10(0.18) / DensityPerCode) / CodeFullScale);

    /// <summary>
    /// Converts the linear positive Stage 1 produces into normalised Cineon code values, in
    /// place.
    ///
    /// THE INPUT IS NOT <c>10^(D_adj)</c>. Stage 1 exponentiates and then applies the black
    /// floor, so what actually arrives is
    ///
    ///   <c>(10^D_adj - floor) / (1 - floor)</c>,  floor = 10^-OutputRange
    ///
    /// which is the normalisation that makes the SAMPLED BLACK come out at exactly 0 and the
    /// highlight at 1. That is right for the pass-through path — a display space wants its black
    /// at zero — but it is the wrong domain to take a logarithm of, and taking one anyway is a
    /// real bug with a visible signature. The sampled black is 0, <c>log10(0)</c> pins to
    /// <see cref="FrameParams.DensityCeiling"/>, and the value lands near code -574: far below
    /// the encoding's 95 and deep inside the flat dead zone under a print stock's toe. The whole
    /// shadow range collapses there, and the only way to drag it back out is to pull D_min down
    /// until the picture's blacks are lifted enough to clear the toe — which is exactly the
    /// symptom that led here, and which quietly wrecks the calibration to compensate for an
    /// encoding mistake.
    ///
    /// So the floor is UNDONE first, recovering <c>10^D_adj</c>, and only then does the log run.
    /// The floor is a constant (<see cref="DensityEndpoints.BlackFloor"/> is
    /// <c>10^-OutputRange</c> for every channel, by the span normalisation), so undoing it is
    /// exact rather than an estimate. The sampled black then lands on code 95 where it belongs,
    /// and D_min goes back to meaning the measured film base.
    ///
    /// The log/exp round trip that remains is redundant in principle and kept on purpose: it lets
    /// this path reuse Stage 1 unchanged rather than forking it, and in float32 the pair is
    /// accurate to ~1e-7 relative — four orders below the cube's own 33³ interpolation error.
    ///
    /// Values at or below the floor (sprocket cores, scan borders, RAW padding, and anything the
    /// user sampled as black) would take the log to -infinity, so they clamp to the encoding's
    /// black rather than running off the bottom.
    /// </summary>
    public static void ToCineon(float[] data)
    {
        const float invDensityPerCode = (float)(1.0 / (DensityPerCode * CodeFullScale));
        const float whiteNorm = (float)(WhiteCode / CodeFullScale);
        const float blackNorm = (float)(BlackCode / CodeFullScale);

        // The floor Inversion removed, and the span it divided by, so this can put both back.
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);
        float span = 1.0f - floor;

        Parallel.For(0, data.Length, i =>
        {
            // Undo the black-floor normalisation: back to 10^D_adj.
            float v = data[i] * span + floor;
            // At or under the floor the picture is already at or below the sampled black.
            data[i] = v > floor
                ? whiteNorm + MathF.Log10(v) * invDensityPerCode
                : blackNorm;
        });
    }

    /// <summary>
    /// The inverse of <see cref="ToCineon"/> — normalised Cineon back to the linear positive.
    ///
    /// Needed for the pass-through case, where the concept is "the LUT slot holds an identity"
    /// but the implementation skips the cube: the data still has to come back out of the log
    /// domain to meet <see cref="ColorPipeline.ToOutputSpace"/>.
    /// </summary>
    public static void FromCineon(float[] data)
    {
        const float densityPerCodeScaled = (float)(DensityPerCode * CodeFullScale);
        const float whiteNorm = (float)(WhiteCode / CodeFullScale);

        // Mirrors ToCineon exactly, including RE-APPLYING the black floor — the two have to be a
        // true inverse pair or a round trip would shift the black by a full floor's worth.
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);
        float invSpan = 1.0f / (1.0f - floor);

        Parallel.For(0, data.Length, i =>
        {
            float v = MathF.Pow(10.0f, (data[i] - whiteNorm) * densityPerCodeScaled);
            data[i] = (v - floor) * invSpan;
        });
    }
}
