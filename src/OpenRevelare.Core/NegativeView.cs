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
/// </summary>
public static class NegativeView
{
    /// <summary>
    /// Multiply in place by a green-normalised white balance. Null or a non-finite gain is a
    /// no-op, so a caller with no camera coefficients passes what it has and gets the UniWB
    /// picture rather than having to branch.
    ///
    /// PURELY MULTIPLICATIVE, and applied BEFORE the output-space encode: the buffer is
    /// scene-linear at this point, which is the only domain where a white balance is a per-channel
    /// scale. No clamp — values above 1 stay above 1 and are dealt with by the encode, the same
    /// way the positive path treats them.
    /// </summary>
    public static void ApplyWhiteBalance(float[] data, double[]? gains)
    {
        if (gains is not { Length: 3 }) return;
        float gr = (float)gains[0], gg = (float)gains[1], gb = (float)gains[2];
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
