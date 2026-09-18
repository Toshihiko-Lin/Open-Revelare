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
    /// THE BOUND IS PER PIXEL, NOT PER CHANNEL (D-036). A colour whose largest component is over
    /// the ceiling is scaled down as a whole, by the one factor that puts that component ON the
    /// ceiling; its ratios — hue and saturation — are kept. Clamping each channel on its own
    /// instead let a warm highlight lose its red first and its green second as exposure rose,
    /// so the highlights an exposure push carried past the ceiling changed colour on the way,
    /// which is the cast seen on an HDR panel. The SDR path's clamp into <c>[0,1]</c> does clip
    /// per channel, but there the ceiling is paper white and every channel ends on it together;
    /// an HDR ceiling is a luminance, and a colour can sit on it without being white.
    /// </para>
    ///
    /// <para>
    /// The lower bound is deliberately absent. In the display-referred form a negative could only
    /// be an artefact of the destination encoding; here it is a colour outside the target's
    /// primaries, which the extended carrier represents exactly and D-005 keeps on purpose. The
    /// scale is below 1, so a negative component only moves toward zero.
    /// </para>
    /// </summary>
    public static void BoundAbove(float[] data, float asymptote)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireValidAsymptote(asymptote);
        if (data.Length % 3 != 0)
            throw new ArgumentException("Expected interleaved RGB samples.", nameof(data));

        ParallelSweep.OverPixels(data.Length / 3, (from, to) =>
        {
            for (int i = from; i < to; i += 3)
            {
                float r = data[i], g = data[i + 1], b = data[i + 2];
                // Not MathF.Max: that propagates NaN, and a NaN component would then leave its
                // two neighbours unbounded. Written this way NaN fails every comparison, so it is
                // ignored for the maximum and stays NaN on the way out, as it did per channel.
                float max = float.NegativeInfinity;
                if (r > max) max = r;
                if (g > max) max = g;
                if (b > max) max = b;
                if (!(max > asymptote)) continue;
                float scale = asymptote / max;
                // The largest component lands on the ceiling exactly, the others under it: a
                // rounded product must not leave a sample an ulp over the promise.
                data[i] = r == max ? asymptote : MathF.Min(r * scale, asymptote);
                data[i + 1] = g == max ? asymptote : MathF.Min(g * scale, asymptote);
                data[i + 2] = b == max ? asymptote : MathF.Min(b * scale, asymptote);
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
