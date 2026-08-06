namespace OpenRevelare.Core;

/// <summary>
/// Stage 2 (SceneBase) op-chain — port of negative/levels.py, run in the order
/// pipeline.py::_run_stage2 uses:
///   WB → exposure → levels → contrast → highlights/shadows → curves → saturation.
/// All ops work in linear light on the interleaved float buffer, in place, and
/// (except curves/sRGB) do NOT clip — headroom is carried in full float so later
/// steps keep working on un-truncated data.
///
/// ONE FUSED PASS, not seven. Every op here is pointwise — none of them reads a
/// neighbouring pixel — so running them as seven separate <c>Parallel.For</c> sweeps was
/// seven full round-trips through the frame for arithmetic that could ride along in one.
/// At 24 MP each sweep moves 288 MB, so the chain was pushing ~4 GB through memory to do
/// perhaps a dozen flops per pixel; it was bandwidth-bound, not compute-bound. Fusing is
/// exactly equivalent pixel-for-pixel (verified bit-identical) because the per-pixel
/// expression order is unchanged — only the loop nesting is.
///
/// The per-op enable flags are hoisted out of the loop, so the branches inside are
/// perfectly predicted and cost nothing next to the memory traffic they save.
/// </summary>
public static class Stage2
{
    private const float LumaR = 0.2126f, LumaG = 0.7152f, LumaB = 0.0722f;
    private const float HsGammaStrength = 1.2f;
    private const float Gamma = 2.2f, InvGamma = 1.0f / 2.2f;
    private const int CurveLutSize = 256;

    /// <summary>True when any Stage-2 op would alter the image (beyond the sRGB exit).</summary>
    public static bool IsActive(FrameParams c)
    {
        static bool AllOne(double[] v) => v.All(x => Math.Abs(x - 1.0) <= 1e-8 + 1e-5);
        return !AllOne(c.WbGains)
            || c.ExposureEv != 0.0
            || c.BlackPoint != 0.0 || c.WhitePoint != 1.0
            || c.Contrast != 0.0
            || c.Highlights != 0.0 || c.Shadows != 0.0
            || c.Saturation != 0.0
            || c.CurvePointsM.Count > 0 || c.CurvePointsR.Count > 0
            || c.CurvePointsG.Count > 0 || c.CurvePointsB.Count > 0;
    }

    /// <summary>
    /// Run the whole Stage-2 chain over <paramref name="d"/> in one pass.
    ///
    /// The seven ops are applied to each pixel in the order pipeline.py::_run_stage2 uses,
    /// with each op's original expression reproduced verbatim (same float narrowing, same
    /// clamps, same guard conditions) — that is what makes the fused result bit-identical to
    /// running them one after another over the whole frame.
    /// </summary>
    /// <param name="srgbExit">Also apply the sRGB output TRC as the final per-pixel step. It
    /// rides along here rather than as its own sweep for the same reason the seven ops do —
    /// it is pointwise, and a separate pass over a 24 MP frame is another 288 MB of traffic
    /// for one table lookup per sample. Note this still runs when every op above is disabled.</param>
    public static void ApplyChain(float[] d, FrameParams cal, bool srgbExit = false)
    {
        // ── Hoisted enables + per-op constants ───────────────────────────────────
        static bool AllOne(double[] v) => v.All(x => Math.Abs(x - 1.0) <= 1e-8 + 1e-5);

        bool doWb = !AllOne(cal.WbGains);
        float wb0 = (float)cal.WbGains[0], wb1 = (float)cal.WbGains[1], wb2 = (float)cal.WbGains[2];

        bool doExposure = cal.ExposureEv != 0.0;
        float expGain = (float)Math.Pow(2.0, cal.ExposureEv);

        // Levels' own degenerate guard (black >= white) folds into the enable flag.
        bool doLevels = (cal.BlackPoint != 0.0 || cal.WhitePoint != 1.0)
                        && cal.BlackPoint < cal.WhitePoint;
        float black = (float)cal.BlackPoint;
        float lvlScale = doLevels ? (float)(1.0 / (cal.WhitePoint - cal.BlackPoint)) : 1.0f;

        bool doContrast = cal.Contrast != 0.0;
        float contrastGain = (float)Math.Pow(2.0, cal.Contrast);

        bool doHs = cal.Highlights != 0.0 || cal.Shadows != 0.0;
        float hi = (float)cal.Highlights, sh = (float)cal.Shadows;
        // These two are constant for the whole frame. They used to be evaluated INSIDE
        // HsTargetLuma, i.e. twice per pixel — 3.4 million redundant double-precision Pow
        // calls on a 1.7 MP preview, which made highlights/shadows cost more than the entire
        // density inversion. Narrowed to float here exactly as the original did, so the value
        // fed to Pow below is unchanged.
        float shGamma = (float)Math.Pow(2.0, -sh * HsGammaStrength);
        float hiGamma = (float)Math.Pow(2.0, hi * HsGammaStrength);
        float shAmt = Math.Abs(sh), hiAmt = Math.Abs(hi);

        float[]? lutM = BuildLut(cal.CurvePointsM);
        float[]? lutR = BuildLut(cal.CurvePointsR);
        float[]? lutG = BuildLut(cal.CurvePointsG);
        float[]? lutB = BuildLut(cal.CurvePointsB);
        bool doCurves = lutM != null || lutR != null || lutG != null || lutB != null;
        bool preserveHue = cal.CurvePreserveHue;

        bool doSaturation = cal.Saturation != 0.0;
        float satFactor = 1.0f + (float)cal.Saturation;

        if (!(doWb || doExposure || doLevels || doContrast || doHs || doCurves || doSaturation))
        {
            if (srgbExit) Srgb.ApplyForwardInPlace(d);
            return;
        }

        float[]? srgbLut = srgbExit ? Srgb.ForwardLut : null;

        Parallel.For(0, d.Length / 3, p =>
        {
            int b = p * 3;
            float r = d[b], g = d[b + 1], bl = d[b + 2];

            // 1 — white balance gains
            if (doWb) { r *= wb0; g *= wb1; bl *= wb2; }

            // 2 — exposure (clamps negatives, as the standalone op did)
            if (doExposure)
            {
                r *= expGain; g *= expGain; bl *= expGain;
                if (r < 0.0f) r = 0.0f;
                if (g < 0.0f) g = 0.0f;
                if (bl < 0.0f) bl = 0.0f;
            }

            // 3 — levels
            if (doLevels)
            {
                r = (r - black) * lvlScale;
                g = (g - black) * lvlScale;
                bl = (bl - black) * lvlScale;
            }

            // 4 — contrast about 0.5
            if (doContrast)
            {
                r = (r - 0.5f) * contrastGain + 0.5f;
                g = (g - 0.5f) * contrastGain + 0.5f;
                bl = (bl - 0.5f) * contrastGain + 0.5f;
            }

            // 5 — highlights / shadows (luma-driven, hue preserving)
            if (doHs)
            {
                float luma = r * LumaR + g * LumaG + bl * LumaB;
                float lumaC = luma < 0.0f ? 0.0f : (luma > 1.0f ? 1.0f : luma);
                float outv = lumaC;
                if (sh != 0.0f)
                {
                    float c = Math.Clamp(outv, 0.0f, 1.0f);
                    outv = (1.0f - shAmt) * outv + shAmt * (float)Math.Pow(c, shGamma);
                }
                if (hi != 0.0f)
                {
                    float c = Math.Clamp(outv, 0.0f, 1.0f);
                    outv = (1.0f - hiAmt) * outv + hiAmt * (1.0f - (float)Math.Pow(1.0f - c, hiGamma));
                }
                float scale = lumaC > 1e-6f ? outv / Math.Max(lumaC, 1e-6f) : 1.0f;
                r *= scale; g *= scale; bl *= scale;
            }

            // 6 — tone curves, in gamma-2.2 encoded space
            if (doCurves)
            {
                float cr = (float)Math.Pow(Math.Clamp(r, 0.0f, 1.0f), InvGamma);
                float cg = (float)Math.Pow(Math.Clamp(g, 0.0f, 1.0f), InvGamma);
                float cb = (float)Math.Pow(Math.Clamp(bl, 0.0f, 1.0f), InvGamma);

                if (lutM != null)
                {
                    if (preserveHue)
                    {
                        float luma = cr * LumaR + cg * LumaG + cb * LumaB;
                        float lumaOut = SampleLut(lutM, luma);
                        float scale = luma > 1e-6f ? lumaOut / Math.Max(luma, 1e-6f) : 1.0f;
                        cr = Math.Clamp(cr * scale, 0.0f, 1.0f);
                        cg = Math.Clamp(cg * scale, 0.0f, 1.0f);
                        cb = Math.Clamp(cb * scale, 0.0f, 1.0f);
                    }
                    else
                    {
                        cr = SampleLut(lutM, cr); cg = SampleLut(lutM, cg); cb = SampleLut(lutM, cb);
                    }
                }
                if (lutR != null) cr = SampleLut(lutR, cr);
                if (lutG != null) cg = SampleLut(lutG, cg);
                if (lutB != null) cb = SampleLut(lutB, cb);

                r = Math.Clamp((float)Math.Pow(cr, Gamma), 0.0f, 1.0f);
                g = Math.Clamp((float)Math.Pow(cg, Gamma), 0.0f, 1.0f);
                bl = Math.Clamp((float)Math.Pow(cb, Gamma), 0.0f, 1.0f);
            }

            // 7 — saturation
            if (doSaturation)
            {
                float luma = r * LumaR + g * LumaG + bl * LumaB;
                r = luma + (r - luma) * satFactor;
                g = luma + (g - luma) * satFactor;
                bl = luma + (bl - luma) * satFactor;
            }

            // 8 — sRGB output TRC (same shared table Srgb.ApplyForwardInPlace uses)
            if (srgbLut != null)
            {
                r = srgbLut[Srgb.LutIndex(r)];
                g = srgbLut[Srgb.LutIndex(g)];
                bl = srgbLut[Srgb.LutIndex(bl)];
            }

            d[b] = r; d[b + 1] = g; d[b + 2] = bl;
        });
    }

    private static float SampleLut(float[] lut, float x)
    {
        float idxF = x * (CurveLutSize - 1);
        int lo = (int)idxF;
        if (lo < 0) lo = 0; else if (lo > CurveLutSize - 2) lo = CurveLutSize - 2;
        float frac = idxF - lo;
        return lut[lo] * (1.0f - frac) + lut[lo + 1] * frac;
    }

    /// <summary>Build a 256-entry LUT from control points via monotone PCHIP; null = identity.</summary>
    private static float[]? BuildLut(List<(double X, double Y)> points)
    {
        if (points.Count == 0) return null;
        var pts = points.OrderBy(p => p.X).ToList();
        var xs = pts.Select(p => p.X).ToList();
        var ys = pts.Select(p => p.Y).ToList();
        if (xs[0] > 0.0) { xs.Insert(0, 0.0); ys.Insert(0, 0.0); }
        if (xs[^1] < 1.0) { xs.Add(1.0); ys.Add(1.0); }
        if (xs.Count < 2) return null;
        for (int i = 1; i < xs.Count; i++)
            if (xs[i] <= xs[i - 1]) return null; // need strictly increasing x

        var pchip = new Pchip(xs.ToArray(), ys.ToArray());
        var lut = new float[CurveLutSize];
        for (int i = 0; i < CurveLutSize; i++)
        {
            double t = (double)i / (CurveLutSize - 1);
            lut[i] = Math.Clamp((float)pchip.Eval(t), 0.0f, 1.0f);
        }
        return lut;
    }
}
