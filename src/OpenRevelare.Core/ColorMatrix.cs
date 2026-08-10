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
    /// row-major, clamping the result to non-negative.
    ///
    /// The lower clamp matters: a characterisation matrix has negative off-diagonals, so a
    /// saturated colour near the gamut edge can land slightly below zero. Left alone those
    /// values would survive into Stage 2, where the contrast and curve operations assume
    /// non-negative input. No upper clamp — highlights above 1 are meaningful until the
    /// output TRC, which does its own clamping.
    /// </summary>
    public static void ApplyInPlace(float[] data, double[,] m)
    {
        float m00 = (float)m[0, 0], m01 = (float)m[0, 1], m02 = (float)m[0, 2];
        float m10 = (float)m[1, 0], m11 = (float)m[1, 1], m12 = (float)m[1, 2];
        float m20 = (float)m[2, 0], m21 = (float)m[2, 1], m22 = (float)m[2, 2];

        Parallel.For(0, data.Length / 3, i =>
        {
            int p = i * 3;
            float r = data[p], g = data[p + 1], b = data[p + 2];
            float nr = m00 * r + m01 * g + m02 * b;
            float ng = m10 * r + m11 * g + m12 * b;
            float nb = m20 * r + m21 * g + m22 * b;
            data[p] = nr < 0.0f ? 0.0f : nr;
            data[p + 1] = ng < 0.0f ? 0.0f : ng;
            data[p + 2] = nb < 0.0f ? 0.0f : nb;
        });
    }
}
