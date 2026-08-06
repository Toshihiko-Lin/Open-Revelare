namespace OpenRevelare.Core;

/// <summary>Domain in which the Path-A decouple matrix is applied to the linear RAW.</summary>
public enum DecoupleMode
{
    /// <summary>Linear-domain matrix multiply with per-pixel gamut mapping (default).</summary>
    Linear,
    /// <summary>Density (log) domain: chroma transformed in log space, converted back.</summary>
    Density,
}

/// <summary>
/// Path-A RGB-light decoupling — port of negative/decouple_apply.py. Applied to the
/// linear RAW BEFORE inversion (after the manual vignette), only when a valid 3×3
/// matrix was computed at import time. Path B (white light / scanner TIFF) skips it.
///
/// The matrix itself comes from the import-time calibration (decouple.py, C batch),
/// supplied externally via <see cref="FrameParams.DecoupleMatrix"/>.
/// </summary>
public static class Decouple
{
    /// <summary>
    /// Apply the decouple matrix to a linear (H, W, 3) buffer in place, in the given domain.
    /// </summary>
    /// <param name="data">Interleaved linear RGB, length H*W*3.</param>
    /// <param name="m">Row-major 3×3 decouple matrix (t_dec = t · Mᵀ).</param>
    public static void Apply(float[] data, double[,] m, DecoupleMode mode)
    {
        if (mode == DecoupleMode.Density) ApplyDensity(data, m);
        else ApplyLinear(data, m);
    }

    // Linear-domain decouple with per-pixel gamut mapping. Pure per-pixel: no
    // cross-pixel statistic, so the result is independent of any tiling.
    private static void ApplyLinear(float[] data, double[,] m)
    {
        const double eps = 1e-6;
        double m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
        double m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
        double m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];

        int n = data.Length / 3;
        Parallel.For(0, n, p =>
        {
            int i = p * 3;
            double o0 = data[i], o1 = data[i + 1], o2 = data[i + 2];   // t_orig
            double t0 = m00 * o0 + m01 * o1 + m02 * o2;                 // t_dec = t_orig @ Mᵀ
            double t1 = m10 * o0 + m11 * o1 + m12 * o2;
            double t2 = m20 * o0 + m21 * o1 + m22 * o2;

            // Gamut map only pixels the matrix drove (near-)negative in some channel.
            if (Math.Min(t0, Math.Min(t1, t2)) < eps)
            {
                double a0 = GamutAlpha(o0, t0, eps);
                double a1 = GamutAlpha(o1, t1, eps);
                double a2 = GamutAlpha(o2, t2, eps);
                double a = Math.Clamp(Math.Min(a0, Math.Min(a1, a2)), 0.0, 1.0);
                t0 = (1.0 - a) * o0 + a * t0;
                t1 = (1.0 - a) * o1 + a * t1;
                t2 = (1.0 - a) * o2 + a * t2;
            }

            data[i] = (float)t0; data[i + 1] = (float)t1; data[i + 2] = (float)t2;
        });
    }

    // Per-channel gamut blend factor: how far toward t_dec we can go before this
    // channel drops below eps. Channels that decouple did not darken (diff ≤ 1e-10)
    // impose no limit (factor 1).
    private static double GamutAlpha(double orig, double dec, double eps)
    {
        double diff = orig - dec;
        return diff > 1e-10 ? (orig - eps) / diff : 1.0;
    }

    // Density-domain decouple: transform chroma in log space, convert back. Uses a
    // per-channel 99th-percentile T_base estimate (a cross-pixel statistic).
    private static void ApplyDensity(float[] data, double[,] m)
    {
        double m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
        double m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
        double m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];

        int n = data.Length / 3;
        double tb0 = Math.Max(Percentile99(data, 0, n), 1e-6);
        double tb1 = Math.Max(Percentile99(data, 1, n), 1e-6);
        double tb2 = Math.Max(Percentile99(data, 2, n), 1e-6);

        Parallel.For(0, n, p =>
        {
            int i = p * 3;
            double d0 = -Math.Log10(Math.Max(data[i]     / tb0, 1e-10));
            double d1 = -Math.Log10(Math.Max(data[i + 1] / tb1, 1e-10));
            double d2 = -Math.Log10(Math.Max(data[i + 2] / tb2, 1e-10));

            double dMean = (d0 + d1 + d2) / 3.0;
            double c0 = d0 - dMean, c1 = d1 - dMean, c2 = d2 - dMean;

            double n0 = m00 * c0 + m01 * c1 + m02 * c2;   // d_chroma @ Mᵀ
            double n1 = m10 * c0 + m11 * c1 + m12 * c2;
            double n2 = m20 * c0 + m21 * c1 + m22 * c2;
            double nMean = (n0 + n1 + n2) / 3.0;          // re-neutralise chroma mean
            n0 -= nMean; n1 -= nMean; n2 -= nMean;

            data[i]     = (float)(Math.Pow(10.0, -(dMean + n0)) * tb0);
            data[i + 1] = (float)(Math.Pow(10.0, -(dMean + n1)) * tb1);
            data[i + 2] = (float)(Math.Pow(10.0, -(dMean + n2)) * tb2);
        });
    }

    // numpy.percentile(channel, 99, method='linear'), computed in float64. See NumpyStats.
    private static double Percentile99(float[] data, int channel, int n)
        => NumpyStats.PercentileChannel(data, channel, n, 99.0);
}
