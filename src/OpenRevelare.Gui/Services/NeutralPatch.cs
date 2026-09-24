using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// The reading behind the white-balance eyedropper: the mean linear colour of a patch the user
/// says ought to be neutral.
///
/// It is not a plain average, because of what a clipped sample means. A channel sitting at the top
/// of the range says "at least this much" — the value it would have had is unknown and larger — so
/// including it pulls the mean towards white and, since the channels clip at different scene
/// levels, towards whichever channel clipped LAST. A patch that catches a blown highlight or the
/// white the sprocket mask paints in would therefore answer with a cast that is an artefact of the
/// clipping rather than a property of the light. Those samples are dropped, and when too few
/// survive the reading is refused instead of guessed.
/// </summary>
public static class NeutralPatch
{
    /// <summary>A display value at or above this is pinned, not measured.</summary>
    public const float ClipCeiling = 0.995f;

    /// <summary>Fewer usable samples than this and the mean is noise rather than a reading.</summary>
    public const int MinSamples = 16;

    /// <summary>
    /// The mean of the patch's unclipped samples, in LINEAR light — the domain Stage 2's white
    /// balance multiplies.
    /// </summary>
    /// <param name="patch">Interleaved RGB, display-encoded in <paramref name="space"/>. Decoded IN
    /// PLACE, so the caller must hand over a copy it does not need again.</param>
    /// <returns>Null when too few samples survive the clipping test.</returns>
    public static (double[] Mean, int Clipped, int Used)? MeanOfUnclipped(float[] patch, ColorSpaceDef space)
    {
        ArgumentNullException.ThrowIfNull(patch);
        int count = patch.Length / 3;

        // Decided BEFORE the decode, while the encoded value still says plainly that it is at the
        // ceiling: the decode's curve is steepest there, so a test after it would need a different
        // threshold for every space.
        var keep = new bool[count];
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            int p = i * 3;
            keep[i] = patch[p] < ClipCeiling && patch[p + 1] < ClipCeiling && patch[p + 2] < ClipCeiling;
            if (keep[i]) used++;
        }
        if (used < MinSamples) return null;

        OutputRender.Decode(patch, space);

        double r = 0, g = 0, b = 0;
        for (int i = 0; i < count; i++)
        {
            if (!keep[i]) continue;
            int p = i * 3;
            r += patch[p]; g += patch[p + 1]; b += patch[p + 2];
        }
        return (new[] { r / used, g / used, b / used }, count - used, used);
    }
}
