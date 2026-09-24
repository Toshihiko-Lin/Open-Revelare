namespace OpenRevelare.Core;

/// <summary>
/// Black-and-white negatives: one image, carried on three channels that have no business
/// disagreeing.
///
/// WHY THE COLOUR PATH IS WRONG FOR THEM. Everything Stage 1 does with three channels rests on
/// there being three dye layers to tell apart — the orange mask to subtract, a highlight balance to
/// read off the difference between the channels' densities. A silver image has none of that. What
/// the camera's R, G and B see through it is ONE density, sampled three times through three
/// different spectral windows, so the differences between them are the sensor's and the light's,
/// not the film's. Left per-channel they become a colour cast to be corrected, and the user is then
/// balancing a picture that has no colour in it.
///
/// WHAT THIS DOES INSTEAD. Fold the three samples into one signal before the density conversion,
/// and give the inversion ONE pair of endpoints instead of three. After that the whole existing
/// pipeline applies unchanged and the INVERSION's output is neutral by construction rather than by
/// correction: there is no cast to chase, because the three channels are the same number.
///
/// WHAT STILL COMES AFTER IT. A print-film LUT sits between the inversion and Stage 2, and a colour
/// stock has a cast of its own — so a black-and-white roll rendered through Kodak 2383 is NOT
/// neutral, and should not be: printing black-and-white on colour paper is a real thing people do,
/// and the program has no business refusing it. The guarantee is therefore exact and narrow: neutral
/// under the standard display rendering, and neutral plus whatever stock was asked for otherwise.
/// The frame report says so when both are in play.
///
/// THE FOLD IS LUMINANCE-WEIGHTED (Rec.709), IN LINEAR LIGHT. Of the obvious candidates it is the
/// least noisy: green alone throws away two thirds of the samples (and on a Bayer sensor green
/// already carries half of them, which is why it is the usual single-channel pick), while a flat
/// mean weights the blue channel — the one with the fewest photons through a copy light and the
/// most noise — as heavily as green. The weights differ from an ideal panchromatic response, but
/// that difference is a constant SCALE on the whole frame, and a constant scale is exactly what the
/// two endpoints absorb. What it must not be is per-pixel, and it is not.
/// </summary>
public static class Monochrome
{
    /// <summary>Rec.709 luminance weights, the same ones the clipping overlay and the histogram's
    /// luma curve use — one definition of "how bright is this pixel" in the program.</summary>
    private const float WeightR = 0.2126f, WeightG = 0.7152f, WeightB = 0.0722f;

    /// <summary>
    /// Collapse an interleaved linear RGB buffer to one signal, written back to all three channels.
    /// IN PLACE: every caller owns its buffer by the time this runs (see the in-place op set in
    /// <see cref="Pipeline"/>).
    /// </summary>
    public static void FoldInPlace(float[] data)
    {
        ParallelSweep.OverPixels(data.Length / 3, (from, to) =>
        {
            for (int i = from; i < to; i += 3)
            {
                float y = WeightR * data[i] + WeightG * data[i + 1] + WeightB * data[i + 2];
                data[i] = data[i + 1] = data[i + 2] = y;
            }
        });
    }

    /// <summary>
    /// The same parameters with every per-channel colour quantity collapsed to one value, so the
    /// inversion cannot re-introduce a difference the pixels no longer have.
    ///
    /// GREEN IS THE ONE KEPT, not an average of the three. These triples are calibration readings,
    /// and on a folded frame all three channels measure the same thing — so after a calibration run
    /// in this mode they are already equal and the choice is moot. It matters only for a roll
    /// calibrated in colour and then switched, where the three readings describe three different
    /// spectral windows of the same negative: green is the one the luminance fold weights most, so
    /// it is the closest of the three to what the folded signal actually contains.
    ///
    /// Returns the input unchanged when the roll is not monochrome, so callers can apply it
    /// unconditionally.
    /// </summary>
    public static FrameParams Collapse(FrameParams cal)
    {
        ArgumentNullException.ThrowIfNull(cal);
        if (!cal.Monochrome) return cal;

        FrameParams mono = cal.Clone();
        mono.TBase = Triple(cal.TBase);
        mono.DMinPerChannel = Triple(cal.DMinPerChannel);
        mono.DMaxPerChannel = Triple(cal.DMaxPerChannel);
        // Stage 2's colour controls are levers over a picture with no colour. Left in, a pair of
        // slider values or a channel curve set before the roll was switched would tint a neutral
        // frame with nothing on screen to explain it. Dropped HERE and not in the stored
        // parameters: this is a render-time copy, so switching the roll back to colour brings the
        // user's grade back exactly as they left it.
        mono.WbGains = new[] { 1.0, 1.0, 1.0 };
        mono.CurvePointsR = new List<(double X, double Y)>();
        mono.CurvePointsG = new List<(double X, double Y)>();
        mono.CurvePointsB = new List<(double X, double Y)>();
        return mono;
    }

    /// <summary>The green reading, on all three channels.</summary>
    private static double[] Triple(double[] perChannel)
    {
        double g = perChannel is { Length: 3 } ? perChannel[1] : 0d;
        return new[] { g, g, g };
    }
}
