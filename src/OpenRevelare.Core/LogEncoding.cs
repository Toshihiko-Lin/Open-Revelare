namespace OpenRevelare.Core;

/// <summary>
/// The bridge between the pipeline's density domain and the Cineon code values every consumer
/// downstream speaks — a print-film LUT's input, and the pass-through path's own display
/// rendering.
///
/// WHY THIS IS AN AFFINE MAP AND NOT A CONVERSION. Stage 1 ends in the shape Cineon names.
/// <see cref="DensityEndpoints"/> pins each channel so the sampled base lands at
/// <c>D_adj = -OutputRange</c> and the film's density ceiling at <c>D_adj = 0</c>. So the density
/// domain and Cineon code values are the same line in different units, and getting from one to
/// the other is a scale and a shift — no curve, no table, nothing to be approximate about.
///
///   code = 1032 + D_adj / 0.002        D_adj ∈ [-1.874, 0] → code ∈ [95, 1032]
///   v    = code / 1023                                     → v ∈ [0.0929, 1.0088]
///
/// THE TWO ANCHORS ARE THE STANDARD'S, NOT THIS CODEBASE'S. 95 and 1032 are where Cineon puts
/// the ends of its encoding domain, and 0.002 density per code is the encoding's own step. All
/// three are stated once in <see cref="FrameParams"/> and read from there, because pinning both
/// ends AND the step over-determines the map — the three have to come from one place or they
/// drift apart.
///
/// They did drift apart. A previous revision anchored white at 685 — Cineon's 90% diffuse white —
/// to keep a print stock's shoulder out of the picture, which forced a non-standard 0.00318
/// density per code to span the same range, while <see cref="FrameParams.OutputRange"/> went on
/// being computed from 95..1032 at 0.002. Two scales for one line. The 685 belonged to the
/// CALIBRATION rather than the encoding: <see cref="FrameParams.DMaxPerChannel"/> means the
/// film's density ceiling, so a picture's white sits BELOW it, and where it sits is exactly what
/// the user adjusts D_max to control. Hard-coding 685 here took that adjustment away and then
/// needed a bespoke step to hide the fact.
///
/// WHY IT LIVES HERE RATHER THAN IN THE INVERSION. The inversion has no business knowing what
/// consumes its output; it states the two endpoints and stops. This is the piece that says what
/// those endpoints mean to a consumer that speaks code values.
/// </summary>
public static class LogEncoding
{
    /// <summary>10-bit full scale — the divisor that turns a Cineon code into a normalised
    /// float. NOT 1024: the code range is 0..1023, so 1023 is what maps to 1.0.</summary>
    private const double CodeFullScale = 1023.0;

    /// <summary>
    /// The code the roll's WHITE end lands on — Cineon's encoding ceiling, and the code
    /// <see cref="FrameParams.DMaxPerChannel"/> maps to by definition.
    ///
    /// D_max is the FILM's density ceiling, not the picture's white. A well-exposed frame's
    /// highlight sits below it, and therefore below 1032; raising D_max slides the picture down
    /// the code axis, which is how a user places their white where a downstream print stock wants
    /// it (around 685, the 90% diffuse white those cubes are characterised against). That
    /// placement is a calibration decision and belongs to the user, so it is NOT baked in here.
    /// </summary>
    private const double WhiteCode = FrameParams.CineonWhiteCode;

    /// <summary>
    /// The code the roll's BLACK end lands on — the bottom of the Cineon domain, and the code
    /// <see cref="FrameParams.DMinPerChannel"/> maps to by definition.
    /// </summary>
    private const double BlackCode = FrameParams.CineonBlackCode;

    /// <summary>
    /// Density per code — Cineon's defining step, shared with
    /// <see cref="FrameParams.OutputRange"/> so the two cannot disagree about the scale.
    /// </summary>
    private const double DensityPerCode = FrameParams.CineonDensityPerCode;

    /// <summary>
    /// Normalised Cineon value for the black end, <c>D_adj = -OutputRange</c>. ≈ 0.0929.
    ///
    /// THIS IS NOT DISPLAY BLACK. Code 95 is where the film base sits once the roll is calibrated,
    /// and in the log domain a calibrated base reads as a grey, not as zero. It only becomes black
    /// when a display rendering — <see cref="ColorPipeline.CineonToDisplay"/>, or a print-film
    /// cube — takes it there.
    /// </summary>
    public static readonly float Black = (float)(BlackCode / CodeFullScale);

    /// <summary>Normalised Cineon value for the white end, <c>D_adj = 0</c>. ≈ 1.0088.
    ///
    /// Above 1.0, because 1032 is above the 10-bit full scale of 1023 — that is the headroom the
    /// encoding reserves, and why a print-film cube declares a domain wider than [0,1].</summary>
    public static readonly float White = (float)(WhiteCode / CodeFullScale);

    /// <summary>
    /// Where an 18% scene grey lands under this mapping, relative to the white end. Reported for
    /// diagnostics rather than used by the transform; the anchors above define the mapping on
    /// their own.
    /// </summary>
    public static readonly float MidGrey =
        (float)((WhiteCode + Math.Log10(0.18) / DensityPerCode) / CodeFullScale);

    /// <summary>
    /// Converts the linear positive Stage 1 produces into normalised Cineon code values, in
    /// place.
    ///
    /// THE INPUT IS <c>10^D_adj</c>, straight from Stage 1. It used to be
    /// <c>(10^D_adj - floor)/(1 - floor)</c> — Stage 1 normalised the sampled base to linear zero
    /// before handing over — and this function undid that normalisation before taking its
    /// logarithm. Both halves are gone: the normalisation was Stage 1 deciding, on the display
    /// rendering's behalf, that the film base is black, which is a decision the Cineon domain does
    /// not get to make. See <see cref="Pipeline.ProcessFrame"/>.
    ///
    /// Values at or below zero (sprocket cores, scan borders, RAW padding) would take the log to
    /// -infinity, so they clamp to the encoding's black rather than running off the bottom.
    /// </summary>
    public static void ToCineon(float[] data)
    {
        const float invDensityPerCode = (float)(1.0 / (DensityPerCode * CodeFullScale));
        const float whiteNorm = (float)(WhiteCode / CodeFullScale);
        const float blackNorm = (float)(BlackCode / CodeFullScale);

        Parallel.For(0, data.Length, i =>
        {
            float v = data[i];
            data[i] = v > 0.0f
                ? MathF.Max(whiteNorm + MathF.Log10(v) * invDensityPerCode, blackNorm)
                : blackNorm;
        });
    }

    /// <summary>
    /// The inverse of <see cref="ToCineon"/> — normalised Cineon back to the linear positive
    /// <c>10^D_adj</c>.
    ///
    /// Exact rather than approximate: the pair is a logarithm and its exponential about the same
    /// two constants, so a round trip is accurate to ~1e-7 relative in float32 — four orders below
    /// a cube's own 33³ interpolation error. The clamp at black is not inverted, by design: it
    /// discards nothing a picture contains, only the -infinity that T=0 would produce.
    /// </summary>
    public static void FromCineon(float[] data)
    {
        const float densityPerCodeScaled = (float)(DensityPerCode * CodeFullScale);
        const float whiteNorm = (float)(WhiteCode / CodeFullScale);

        Parallel.For(0, data.Length, i =>
            data[i] = MathF.Pow(10.0f, (data[i] - whiteNorm) * densityPerCodeScaled));
    }
}
