namespace OpenRevelare.Core;

/// <summary>
/// The display rendering's shoulder, as one family of curves rather than two implementations
/// (D-021).
///
/// <para>
/// THE SDR RENDERING IS A MEMBER OF THIS FAMILY, NOT A SIBLING OF IT. The analytic path has
/// always ended with a Reinhard shoulder from <see cref="Knee"/> asymptotic to <c>1.0</c>: the
/// encoding carries the whole negative — D_max lands on code 1032 while 685 is only where a
/// PICTURE's white sits, leaving 2.31 stops of latitude above it — and without the shoulder that
/// entire span rendered as 1.0 and the output encoder clamped it away. An extended target does
/// not need a different curve, it needs the SAME curve aimed somewhere higher. Passing
/// <c>asymptote = 1</c> reproduces the established rendering exactly.
/// </para>
///
/// <para>
/// WHAT THIS MEANS FOR THE TWO RENDERINGS, PRECISELY. They are identical at and below
/// <see cref="Knee"/> — bit-identical, because below the knee the curve is the identity in both.
/// Above it they diverge progressively: the SDR render compresses the latitude into
/// <c>[0.5, 1)</c>, the extended render spreads the same latitude over <c>[0.5, headroom)</c>.
/// So the shadows and mid-tones of an HDR render ARE its SDR sibling's; the highlights are the
/// part that was being spent, and the part this exists to stop spending.
/// </para>
///
/// <para>
/// It would be wrong to describe the two as "identical below paper white". The knee sits at 0.5,
/// well under Cineon's picture white, so everything from the knee up is reshaped — which is
/// exactly the point, and exactly what a print stock's shoulder was doing.
/// </para>
/// </summary>
public static class HighlightRolloff
{
    /// <summary>
    /// Where the shoulder engages, in the normalised linear domain
    /// <see cref="ColorPipeline.CineonToDisplay"/> works in. Below it the transform is untouched.
    ///
    /// <para>
    /// 0.5 IS NOT A FREE CHOICE. It is what lands code 685 on 0.881 once the output space encodes,
    /// against the 0.880 measured on the real Kodak 2383 cube. Raising it to 0.6 gives 0.906 and
    /// lowering it to 0.4 gives 0.854, so this is the value that makes the two renderings agree at
    /// the diffuse white. In code terms the knee sits at 596, so everything from the film base up
    /// through the mid-tones passes through unchanged.
    /// </para>
    ///
    /// <para>
    /// It is shared by every member of the family rather than re-derived per target, and that is
    /// what makes them agree below it: moving it for extended targets would re-grade the
    /// mid-tones of an HDR render relative to its SDR sibling, which is precisely what must not
    /// happen.
    /// </para>
    /// </summary>
    public const float Knee = 0.5f;

    /// <summary>
    /// One shoulder sample: the identity at and below <see cref="Knee"/>, and above it a Reinhard
    /// roll-off asymptotic to <paramref name="asymptote"/> — which is therefore approached but
    /// never reached, so no input, however dense, can clip.
    ///
    /// <para>
    /// The curve is C¹ at the knee: its derivative there is exactly 1, the slope the identity
    /// arrives with, so nothing creases where it engages.
    /// </para>
    /// </summary>
    public static float Of(float value, float asymptote)
    {
        if (!(value > Knee)) return value;

        float span = asymptote - Knee;

        // The limit of the curve. Computing it directly would be Inf/Inf.
        if (float.IsPositiveInfinity(value)) return asymptote;

        float d = value - Knee;
        return Knee + (span * d / (d + span));
    }

    /// <summary>
    /// Applies <see cref="Of"/> in place over normalised linear samples.
    ///
    /// <para>
    /// Negatives, NaN and everything at or below the knee pass through untouched — the guard is
    /// written so NaN takes the same exit as an in-range value rather than propagating through the
    /// arithmetic.
    /// </para>
    /// </summary>
    public static void Apply(float[] data, float asymptote)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireValidAsymptote(asymptote);

        ParallelSweep.Over(data.Length, (from, to) =>
        {
            for (int i = from; i < to; i++) data[i] = Of(data[i], asymptote);
        });
    }

    /// <summary>
    /// Bounds already-rendered samples at <paramref name="asymptote"/>.
    ///
    /// <para>
    /// THE EXTENDED COUNTERPART OF THE SDR PATH'S FINAL CLAMP. Stage 2's display-referred form
    /// ends by clamping into <c>[0,1]</c>, because exposure and white balance are multiplicative
    /// and land AFTER the shoulder — they can push a rendered value back past the ceiling the
    /// shoulder established. The same is true of an extended target, so the same guard applies at
    /// the same point, against the target's own ceiling instead of one.
    /// </para>
    ///
    /// <para>
    /// The lower bound is deliberately absent. In the display-referred form a negative could only
    /// be an artefact of the destination encoding; here it is a colour outside the target's
    /// primaries, which the extended carrier represents exactly and D-005 keeps on purpose.
    /// </para>
    /// </summary>
    public static void BoundAbove(float[] data, float asymptote)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireValidAsymptote(asymptote);

        ParallelSweep.Over(data.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                if (data[i] > asymptote) data[i] = asymptote;
            }
        });
    }

    private static void RequireValidAsymptote(float asymptote)
    {
        if (!float.IsFinite(asymptote) || asymptote <= Knee)
        {
            throw new ArgumentOutOfRangeException(
                nameof(asymptote),
                asymptote,
                $"The shoulder's asymptote must be finite and above the knee ({Knee}).");
        }
    }
}
