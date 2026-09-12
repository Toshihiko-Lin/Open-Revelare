namespace OpenRevelare.Core;

/// <summary>
/// The shoulder an extended-range render uses instead of a clamp (D-021).
///
/// <para>
/// WHAT REPLACES WHAT. An SDR terminal ends by forcing every channel into <c>[0,1]</c> — the
/// print stock's own shoulder does it on the LUT path, <c>ApplyMatrix</c>'s clamp does it on the
/// analytic one. Neither is available to an extended target: the first is a print emulation, and
/// a print has no specular highlights to emulate, while the second throws away exactly the
/// information the target was asked to carry. What is left is the honest version of the same job:
/// compress what lies above diffuse white into the headroom the target actually has.
/// </para>
///
/// <para>
/// THE KNEE IS EXACTLY DIFFUSE WHITE, AND THAT IS A CONTRACT. At and below <c>1.0</c> this is the
/// identity — not approximately, not to within a tolerance, but untouched. An HDR render and its
/// SDR sibling must agree on the picture and differ only in what they do above paper white; if
/// the shoulder reached down below <c>1.0</c> it would re-grade the whole image and the two would
/// no longer be the same photograph.
/// </para>
///
/// <para>
/// THE CURVE. With <c>s = headroom − 1</c> and <c>t = (v − 1) / s</c>, values above the knee map
/// to <c>1 + s·t/(1+t)</c>. It is monotonic, it maps <c>1 → 1</c> and <c>+∞ → headroom</c> so the
/// target's peak is a real promise rather than a hope, and its derivative at the knee is exactly
/// <c>1</c> — the same slope the identity arrives with. That C¹ join is the point: a shoulder
/// that merely met the identity in value would put a visible crease at diffuse white, which on
/// film highlights is precisely where the eye is looking.
/// </para>
/// </summary>
public static class HighlightRolloff
{
    /// <summary>
    /// Rolls values above <c>1.0</c> toward <paramref name="headroom"/>, in place, over
    /// interleaved linear RGB in the target's own primaries.
    ///
    /// <para>
    /// APPLIED AFTER THE PRIMARIES ROTATION, ON PURPOSE. The headroom is a statement about the
    /// numbers that leave the render, so the compression has to happen in the space those numbers
    /// are in — rolling off in the working space and then rotating into narrower primaries can
    /// push a channel back above the peak and quietly break the promise. Doing it per channel
    /// here also desaturates bright colour slightly as it compresses, which is what a shoulder
    /// does on film and is the behaviour being replaced.
    /// </para>
    /// </summary>
    public static void Apply(float[] data, float headroom)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!float.IsFinite(headroom) || headroom <= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(headroom),
                headroom,
                "Highlight headroom must be finite and greater than one; a target without headroom has nothing to roll off.");
        }

        float span = headroom - 1f;
        ParallelSweep.Over(data.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float value = data[i];

                // Negatives, NaN and everything at or below diffuse white leave untouched. The
                // comparison is written this way so NaN takes the same exit as an in-range value
                // rather than falling into the arithmetic below and propagating.
                if (!(value > 1f)) continue;

                // +Infinity is the limit of the curve, and computing it would produce Inf/Inf.
                if (float.IsPositiveInfinity(value))
                {
                    data[i] = headroom;
                    continue;
                }

                float t = (value - 1f) / span;
                data[i] = 1f + (span * (t / (1f + t)));
            }
        });
    }
}
