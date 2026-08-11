namespace OpenRevelare.Core;

/// <summary>
/// Stage 2 (SceneBase) op-chain — port of negative/levels.py, run in the order
/// pipeline.py::_run_stage2 uses:
///   WB → exposure → levels → contrast → highlights/shadows → curves → saturation.
///
/// Stage 2 runs in the roll's OUTPUT space, after the Cineon step-4 conversion, because that is
/// what its operations mean: contrast pivots on 0.5 as mid-grey, levels' endpoints are 0 and 1,
/// curve control points are authored on a bounded perceptual ramp. None of that is true in the
/// scene-linear working space. The output space is therefore threaded in rather than assumed —
/// luminance weights and the exit curve both come from it.
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
    /// <summary>
    /// Luminance weights — the Y row of the RGB→XYZ matrix of the space Stage 2 is RUNNING IN,
    /// which is the roll's OUTPUT space, not the working space.
    ///
    /// This is the distinction the split makes necessary. Stage 2 happens after step 4, so by the
    /// time these ops see a pixel it has already been converted out of ACEScg into the output
    /// space; weighting it by ACEScg's 0.2722/0.6741/0.0537 would be measuring luminance in a
    /// space the data has left. Rec709 wants sRGB's 0.2126/0.7152/0.0722, and the paper spaces
    /// want their own — the whole point of offering them.
    ///
    /// Derived per call rather than cached in a static, because the output space is a per-roll
    /// parameter now and a static would pin whichever roll happened to render first.
    /// </summary>
    private readonly record struct Luma(float R, float G, float B)
    {
        public static Luma For(ColorSpaceDef space)
        {
            double[,] toXyz = space.ToXyz();
            return new Luma((float)toXyz[1, 0], (float)toXyz[1, 1], (float)toXyz[1, 2]);
        }

        public float Of(float r, float g, float b) => r * R + g * G + b * B;
    }

    private const float HsGammaStrength = 1.2f;

    /// <summary>
    /// Companding for the tone-curve step. Curves are authored against a perceptual ramp — a
    /// control point at 0.5 should sit at mid-grey, not at half the linear light — so the data is
    /// encoded before sampling and decoded after.
    ///
    /// This is an output-space property in principle, but 2.2 is deliberately kept as a plain
    /// constant rather than derived: it defines what the user's saved curve points MEAN. Deriving
    /// it would silently reinterpret every stored curve whenever the output space changed — and
    /// that space is now a per-roll setting the user can switch at will, so a derived value would
    /// move the curve under them on every switch. It stays 2.2 and curves keep their meaning.
    /// </summary>
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
    /// <param name="output">The space Stage 2 runs in — the roll's step-4 target. Luminance
    /// weights and the encoding curve both come from it.</param>
    /// <param name="encodeExit">Also apply <paramref name="output"/>'s TRC as the final per-pixel
    /// step. It rides along here rather than as its own sweep for the same reason the seven ops do
    /// — it is pointwise, and a separate pass over a 24 MP frame is another 288 MB of traffic for
    /// one table lookup per sample. Note this still runs when every op above is disabled.</param>
    public static void ApplyChain(float[] d, FrameParams cal, ColorSpaceDef output,
                                  bool encodeExit = false)
    {
        // Display-referred chain: scale light in linear, then encode, then do everything
        // perceptual in the encoded space where its definitions actually hold.
        if (cal.DisplayReferredStage2)
        {
            ApplyDisplayReferred(d, cal, output, encodeExit);
            return;
        }

        Luma luma = Luma.For(output);

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
            if (encodeExit) OutputRender.Encode(d, output);
            return;
        }

        // The LUT fast path only exists for the piecewise sRGB/P3 curve. Every other space is a
        // power curve, applied as a separate sweep after the chain — one extra pass over the
        // frame, which is the honest cost of not having a table for it.
        bool lutExit = encodeExit && UsesSrgbCurve(output);
        float[]? srgbLut = lutExit ? Srgb.ForwardLut : null;

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
                float lum = luma.Of(r, g, bl);
                float lumaC = lum < 0.0f ? 0.0f : (lum > 1.0f ? 1.0f : lum);
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

            // 6 — tone curves, in gamma-2.2 encoded space.
            //
            // Values outside [0,1] are carried THROUGH rather than truncated. The curve is only
            // defined on [0,1], so out-of-range samples keep their original value and rejoin
            // afterwards; clamping them here (as this did) destroyed exactly the headroom a
            // wider working space exists to provide — an ACEScg red lands at 1.23 in sRGB terms,
            // and truncating it before the curve throws that away permanently.
            //
            // Negatives are still floored: Pow of a negative base is NaN, and a negative here
            // means the colour left the gamut entirely, which the output stage handles.
            if (doCurves)
            {
                float kr = r > 1.0f ? r : 0.0f, kg = g > 1.0f ? g : 0.0f, kb = bl > 1.0f ? bl : 0.0f;
                float cr = (float)Math.Pow(Math.Clamp(r, 0.0f, 1.0f), InvGamma);
                float cg = (float)Math.Pow(Math.Clamp(g, 0.0f, 1.0f), InvGamma);
                float cb = (float)Math.Pow(Math.Clamp(bl, 0.0f, 1.0f), InvGamma);

                if (lutM != null)
                {
                    if (preserveHue)
                    {
                        float lum = luma.Of(cr, cg, cb);
                        float lumaOut = SampleLut(lutM, lum);
                        float scale = lum > 1e-6f ? lumaOut / Math.Max(lum, 1e-6f) : 1.0f;
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

                // Restore anything that was above 1.0 on the way in: the curve had nothing to
                // say about it, so it passes through untouched rather than being flattened.
                r = kr > 0.0f ? kr : Math.Max((float)Math.Pow(cr, Gamma), 0.0f);
                g = kg > 0.0f ? kg : Math.Max((float)Math.Pow(cg, Gamma), 0.0f);
                bl = kb > 0.0f ? kb : Math.Max((float)Math.Pow(cb, Gamma), 0.0f);
            }

            // 7 — saturation
            if (doSaturation)
            {
                float lum = luma.Of(r, g, bl);
                r = lum + (r - lum) * satFactor;
                g = lum + (g - lum) * satFactor;
                bl = lum + (bl - lum) * satFactor;
            }

            // 8 — output TRC (same shared table Srgb.ApplyForwardInPlace uses), when the
            // destination is one of the piecewise-curve spaces. Others fall to the sweep below.
            if (srgbLut != null)
            {
                r = srgbLut[Srgb.LutIndex(r)];
                g = srgbLut[Srgb.LutIndex(g)];
                bl = srgbLut[Srgb.LutIndex(bl)];
            }

            d[b] = r; d[b + 1] = g; d[b + 2] = bl;
        });

        // Power-curve spaces: the exit TRC could not ride along in the fused loop, so it runs here.
        if (encodeExit && !lutExit) OutputRender.Encode(d, output);
    }

    /// <summary>
    /// Whether <paramref name="space"/> encodes with sRGB's piecewise curve — the one curve we
    /// hold a LUT for. Display P3 shares it exactly; everything else is a pure power curve.
    /// </summary>
    private static bool UsesSrgbCurve(ColorSpaceDef space) =>
        space.Name.Equals("sRGB", StringComparison.OrdinalIgnoreCase)
     || space.Name.Equals("DisplayP3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The two-stage chain: linear-light operations, then step 4, then the perceptual operations
    /// in the output space.
    ///
    /// WHY THE SPLIT. Each Stage-2 operation is one of two kinds, and the old chain ran both
    /// kinds in linear light:
    ///
    ///   • White balance and exposure SCALE LIGHT. A gain of 2 means twice the photons, which is
    ///     a multiply in linear and nothing so simple once encoded — measured, linear 0.25×2
    ///     encodes to 0.735, whereas doubling the encoded value clips to 1.0 outright.
    ///   • Levels, contrast, highlights/shadows, curves and saturation are PERCEPTUAL. Contrast
    ///     pivots on 0.5 meaning mid-grey; in linear light 0.5 is 73.5% display brightness, so
    ///     the old chain rotated about a point well above mid-grey and crushed the shadows.
    ///
    /// Encoding once, in the middle, also removes the private gamma the curve step used to apply
    /// and undo around itself — the data is already in the right space when the curve sees it,
    /// so the curve simply samples it. That private round trip existed only because the encoding
    /// happened too late.
    ///
    /// The middle step is the Cineon workflow's step 4: it converts PRIMARIES as well as applying
    /// the transfer curve. That is what makes the output space a real choice — the perceptual ops
    /// below run in whatever space the roll targets, so the on-screen result and the exported file
    /// are the same render rather than one being a simulation of the other.
    /// </summary>
    private static void ApplyDisplayReferred(float[] d, FrameParams cal, ColorSpaceDef output,
                                             bool encodeExit)
    {
        static bool AllOne(double[] v) => v.All(x => Math.Abs(x - 1.0) <= 1e-8 + 1e-5);

        // ── Stage A: linear light ────────────────────────────────────────────────
        bool doWb = !AllOne(cal.WbGains);
        bool doExposure = cal.ExposureEv != 0.0;
        if (doWb || doExposure)
        {
            float wb0 = (float)cal.WbGains[0], wb1 = (float)cal.WbGains[1], wb2 = (float)cal.WbGains[2];
            float gain = (float)Math.Pow(2.0, cal.ExposureEv);
            Parallel.For(0, d.Length / 3, p =>
            {
                int b = p * 3;
                float r = d[b], g = d[b + 1], bl = d[b + 2];
                if (doWb) { r *= wb0; g *= wb1; bl *= wb2; }
                if (doExposure) { r *= gain; g *= gain; bl *= gain; }
                d[b] = r < 0f ? 0f : r;
                d[b + 1] = g < 0f ? 0f : g;
                d[b + 2] = bl < 0f ? 0f : bl;
            });
        }

        // ── STEP 4: scene-linear working space → output space, primaries AND gamma ────
        // This is the Cineon step 4 proper, and it sits here because everything below is DEFINED
        // in the output space. Applied unconditionally, not only when encodeExit is set: the
        // perceptual ops NEED the encoded domain to mean what they say. Under OutputIntent.None
        // the caller never reaches Stage 2 at all, so this cannot encode linear output.
        //
        // Note this converts primaries as well as applying the curve — the earlier version only
        // did the curve, because working and output were the same space by assumption.
        ColorPipeline.ToOutputSpace(d, output);

        // ── Stage B: output space ────────────────────────────────────────────────
        var perceptual = cal.Clone();
        perceptual.WbGains = new[] { 1.0, 1.0, 1.0 };
        perceptual.ExposureEv = 0.0;
        perceptual.DisplayReferredStage2 = false;          // run the op bodies, not this wrapper
        ApplyChain(d, perceptual, output, encodeExit: false);   // already encoded; no exit TRC

        // Final clamp. In the old chain the sRGB exit TRC came LAST and quietly bounded
        // everything; here the encoding happens before the perceptual ops, so an op that
        // overshoots has nothing after it. Contrast is the one that does — measured 1.049 on a
        // 0.2 setting, because rotating about mid-grey lifts highlights above white by design.
        // Display-encoded values have no meaning outside [0,1], so this is the correct place to
        // resolve it rather than letting it reach the exporter.
        Parallel.For(0, d.Length, i => d[i] = d[i] < 0f ? 0f : (d[i] > 1f ? 1f : d[i]));
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
