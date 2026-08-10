namespace OpenRevelare.Core;

/// <summary>
/// A 3×3 applied to interleaved linear RGB. Small enough to be one function, separate enough
/// from <see cref="OutputRender"/> to be worth its own name: this is the INPUT side, where a
/// characterisation matrix maps the device's own primaries into a known space, whereas
/// OutputRender is the OUTPUT side and carries gamut mapping with it.
/// </summary>
public static class ColorMatrix
{
    /// <summary>
    /// Rewrites <paramref name="data"/> (interleaved RGB) through <paramref name="m"/>,
    /// row-major, bringing colours the matrix pushes outside [0,1] back inside it by
    /// desaturating toward their own luminance rather than clipping each channel.
    ///
    /// This matters far more than it looks. A characterisation matrix has large negative
    /// off-diagonals — that is what expanding chroma per hue means — so it throws a lot of
    /// colour out of range: measured on the OM-5 matrix over a 21³ grid of the unit cube,
    /// 47.3% of samples land below 0 in some channel and 47.4% land above 1.
    ///
    /// Clipping each channel independently (the obvious thing, and what this function did at
    /// first) shifts both hue and luminance on every one of those pixels, because it moves the
    /// channels by different amounts. On one measured saturated colour it took luminance from
    /// 0.113 to 0.248 — more than double. That is the "colour spilling" a saturated frame shows.
    ///
    /// <see cref="GamutMapping.Desaturate"/> instead pulls the colour toward the neutral of its
    /// OWN luminance until it just fits, so hue and luminance survive and only chroma gives way,
    /// and only on the pixels that need it. It is the same mapper the output stage uses; using
    /// per-channel clipping here while arguing against it there was simply inconsistent.
    /// </summary>
    public static void ApplyInPlace(float[] data, double[,] m)
        => OutputRender.ApplyMatrix(data, m, ColorSpaces.Srgb, GamutMapping.Desaturate);
}
