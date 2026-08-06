namespace OpenRevelare.Core;

/// <summary>
/// sRGB transfer-function helpers (IEC 61966-2-1).
///
/// Closed-form for single values; a 65536-entry LUT for bulk conversion (the hot
/// export/preview path), mirroring Python's <c>negative/_srgb.py</c>. The LUT
/// removes one <c>Pow</c> per pixel; peak error &lt; 1/65535, below 16-bit
/// quantisation.
/// </summary>
public static class Srgb
{
    /// <summary>Forward TRC: linear (clamped to [0,1]) -&gt; sRGB-encoded.</summary>
    public static float LinearToSrgb(float x)
    {
        x = Math.Clamp(x, 0.0f, 1.0f);
        return x <= 0.0031308f
            ? x * 12.92f
            : 1.055f * MathF.Pow(x, 1.0f / 2.4f) - 0.055f;
    }

    /// <summary>Inverse TRC: sRGB-encoded (clamped to [0,1]) -&gt; linear.</summary>
    public static float SrgbToLinear(float x)
    {
        x = Math.Clamp(x, 0.0f, 1.0f);
        return x <= 0.04045f
            ? x / 12.92f
            : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    private const int LutSize = 65536;

    // Both tables are built in DOUBLE and then narrowed, matching how _srgb.py builds
    // its own (np.linspace float64 → np.power → .astype(np.float32)). Evaluating the
    // closed form in float32 instead leaves ~2/3 of the entries 1 ULP off Python's
    // (~2e-7) — harmless for 16-bit export, but these tables also feed the white-balance
    // density solve, where staying bit-identical to the reference is free.

    // ── Cached tables ────────────────────────────────────────────────────────────
    //
    // Both tables depend on NOTHING but the standard, so they are built once per process
    // instead of once per call. They used to be rebuilt inside every ApplyForwardInPlace,
    // i.e. once per Pipeline.ProcessFrame — 65,536 Math.Pow calls and a 256 KB LARGE-OBJECT
    // allocation on every rendered frame, thumbnail and drag step. Measured at 1.00 ms and
    // 0.25 MB of LOH churn per frame on a drag-sized preview, which was ~10% of the whole
    // render and a large part of what forced full Gen2 collections mid-drag.
    //
    // Lazy, not a static field initialiser: the class is also touched for LinearToSrgb /
    // LutIndex on paths that never need a table, and building both eagerly would cost 2 ms
    // on first touch for nothing.
    //
    // ⚠ The arrays are SHARED. Callers read them; nobody may write to them.
    private static readonly Lazy<float[]> ForwardLutLazy = new(BuildForwardLut, LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<float[]> InverseLutLazy = new(BuildInverseLut, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Shared forward LUT: index i (= round(v*65535)) -&gt; sRGB(i/65535). READ ONLY.</summary>
    public static float[] ForwardLut => ForwardLutLazy.Value;

    /// <summary>Shared inverse LUT: index i (= round(v*65535)) -&gt; linear(i/65535). READ ONLY.</summary>
    public static float[] InverseLut => InverseLutLazy.Value;

    private static float[] BuildForwardLut()
    {
        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double x = i / 65535.0;
            lut[i] = (float)(x <= 0.0031308 ? x * 12.92 : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055);
        }
        return lut;
    }

    private static float[] BuildInverseLut()
    {
        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double x = i / 65535.0;
            lut[i] = (float)(x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4));
        }
        return lut;
    }

    /// <summary>Apply the forward TRC to a whole interleaved buffer in place (parallel, LUT).</summary>
    public static void ApplyForwardInPlace(float[] data)
    {
        float[] lut = ForwardLut;
        Parallel.For(0, data.Length, i => data[i] = lut[LutIndex(data[i])]);
    }

    /// <summary>Apply the inverse TRC to a whole interleaved buffer in place (parallel, LUT).</summary>
    public static void ApplyInverseInPlace(float[] data)
    {
        float[] lut = InverseLut;
        Parallel.For(0, data.Length, i => data[i] = lut[LutIndex(data[i])]);
    }

    /// <summary>LUT index for a value: clip to [0,1] then round — mirrors _srgb.py's
    /// <c>(clipped * 65535.0 + 0.5).astype(np.uint16)</c>.</summary>
    public static int LutIndex(float v)
    {
        int idx = (int)(v * 65535.0f + 0.5f);
        return idx < 0 ? 0 : (idx > 65535 ? 65535 : idx);
    }

    /// <summary>
    /// Adobe RGB (1998) TRC: a pure power curve, gamma 563/256.
    ///
    /// Deliberately NOT LUT-accelerated, and computed in float32 — both match export.py.
    /// A LUT was rejected there because AdobeRGB's pure power curve is steep near 0 and a
    /// 16K-entry table overshoots by ~225 levels, unlike sRGB's linear-toe TRC which
    /// tabulates cleanly. float32 halves the cost on a 24MP frame (~3.3s → ~1.3s) and,
    /// after 16-bit quantisation, differs from the float64 path by at most 1 code level.
    /// </summary>
    public static void ApplyAdobeRgbInPlace(float[] data)
    {
        const float G = 256.0f / 563.0f;
        Parallel.For(0, data.Length, i => data[i] = MathF.Pow(Math.Clamp(data[i], 0.0f, 1.0f), G));
    }
}
