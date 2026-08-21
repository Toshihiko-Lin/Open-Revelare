namespace OpenRevelare.Core;

/// <summary>
/// Carrying one frame's crop across to another frame of the SAME scan.
///
/// A crop is normalised against the whole source FILE. On an ordinary roll that is the same
/// thing as the frame, so the rect means the same on every frame and a broadcast can hand it
/// over unchanged. On a scan pre-cut into several frames it is not: each frame owns a different
/// cell of one file (see <see cref="FrameParams.SplitCell"/>), and the source's rect names a
/// patch of the SOURCE's negative. Copied verbatim it points every frame of the strip at that
/// one patch, and the copies all render the same photograph — the bug this exists to fix.
///
/// What "the same crop" means across split frames is the same crop OF THE NEGATIVE: take the
/// middle 80% of each, trim a hair off the left of each. So the rect is reduced to its position
/// within the source's cell and rebuilt against the target's.
/// </summary>
public static class CropRebase
{
    /// <summary>
    /// <paramref name="rect"/>, drawn against <paramref name="from"/>'s negative, re-expressed
    /// against <paramref name="to"/>'s.
    ///
    /// All three are normalised against the whole file, and the two cells are in the file's own
    /// (un-oriented) axes — callers holding an ORIENTED crop must un-orient it on the way in and
    /// re-orient on the way out, because a quarter turn permutes the crop's axes but not the
    /// file's. Null cells mean "this frame is the whole file", which is precisely the case where
    /// the verbatim copy was always correct, so the rect comes back untouched.
    /// </summary>
    public static (double X, double Y, double W, double H) Rebase(
        (double X, double Y, double W, double H) rect,
        (double X, double Y, double W, double H)? from,
        (double X, double Y, double W, double H)? to)
    {
        if (from is not { } f || to is not { } t) return rect;
        if (f == t) return rect;                       // same negative — nothing to move
        if (f.W <= 0 || f.H <= 0) return rect;         // degenerate cell; no ratio to take
        var shape = ((rect.X - f.X) / f.W, (rect.Y - f.Y) / f.H, rect.W / f.W, rect.H / f.H);
        return Clamp01((t.X + shape.Item1 * t.W, t.Y + shape.Item2 * t.H,
                        shape.Item3 * t.W, shape.Item4 * t.H));
    }

    /// <summary>
    /// Hold a rect inside the file, keeping its size where it can.
    ///
    /// The cells of a strip are not all the same size — the detector's dividers land where the
    /// gutters are, and the end frames get whatever the strip's ends leave them — so a shape that
    /// fits its source cell can overhang a slightly smaller target one. SLIDING it back inside is
    /// what preserves the user's intent: they chose a size and an aspect, and those survive. Only
    /// a shape genuinely larger than the file gets trimmed, and there is nothing else to do with
    /// that. Letting it through unclamped would send the region decoder outside the image.
    /// </summary>
    public static (double X, double Y, double W, double H) Clamp01(
        (double X, double Y, double W, double H) r)
    {
        double w = System.Math.Min(r.W, 1.0), h = System.Math.Min(r.H, 1.0);
        return (System.Math.Clamp(r.X, 0.0, 1.0 - w),
                System.Math.Clamp(r.Y, 0.0, 1.0 - h), w, h);
    }
}
