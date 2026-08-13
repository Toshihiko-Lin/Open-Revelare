namespace OpenRevelare.Core;

/// <summary>
/// One round of Deep-WB inference: takes an sRGB-encoded positive and returns the net's
/// input (possibly resized) paired with its colour-corrected output, pixel-aligned.
///
/// This is an INTERFACE rather than a direct ONNX call so <c>OpenRevelare.Core</c> stays
/// dependency-free: onnxruntime lives in OpenRevelare.DeepWb.Onnx and is injected through here.
/// The onnxruntime + net_awb.onnx implementation lives outside Core; parity tests inject a
/// deterministic stub, which is what makes <see cref="WhiteBalance.AutoWbAffineIterative"/>
/// verifiable at all (the net itself can never be bit-matched across runtimes).
/// </summary>
public interface IDeepWbCorrector
{
    (ImageBuffer Input, ImageBuffer Output) CorrectOnce(ImageBuffer srgbPositive);
}

/// <summary>
/// Auto white balance — port of negative/white_balance.py (the runtime-independent half).
///
/// The inversion applies WB as an AFFINE in density space (step 4):
/// <c>D_wb[c] = D[c]·wb_high[c] + wb_offset[c]</c>. A colour negative's highlight-end and
/// shadow-end casts differ (the three dye layers' characteristic curves are non-parallel),
/// so a single multiplicative gain — what the Deep-WB net natively returns — has one degree
/// of freedom and can only fix one end. These functions therefore REGRESS the net's whole
/// correction across the density range to recover both slope and intercept.
///
/// ⚠ NOT THE SHIPPING AUTO-WB PATH. The GUI's 智能白平衡 button does NOT call
/// <see cref="AutoWbAffineIterative"/>; it runs its own loop (MainViewModel.AutoWbAiAsync) —
/// a geometric highlight baseline plus a chroma-only wb_high delta closed over the HIGHLIGHT
/// BAND rather than the whole-image mean, which is what fixed the yellow-clouds case that the
/// whole-image statistic caused. That is the behaviour the user has settled on (2026-08).
///
/// What lives here is kept deliberately, not by accident: it is the faithful port of
/// white_balance.py and the only auto-WB code with a parity harness behind it
/// (tools/parity/{make,ref}_wb.py ↔ CLI --print-wb-calib, 16/17 keys bit-identical), so it
/// remains the reference for the density-domain affine and the round-trip transforms below —
/// several of which (SrgbToPreStep4Density / PreStep4DensityToSrgb) are genuinely shared.
/// Just do not read a green --print-wb-calib as evidence about what the GUI does.
/// </summary>
public static class WhiteBalance
{
    /// <summary>Apply per-channel WB gains. Returns a new buffer.</summary>
    public static ImageBuffer ApplyWbGains(ImageBuffer image, double[] gains)
    {
        var outImg = new ImageBuffer(image.Width, image.Height);
        float g0 = (float)gains[0], g1 = (float)gains[1], g2 = (float)gains[2];
        float[] s = image.Data, o = outImg.Data;
        Parallel.For(0, image.PixelCount, p =>
        {
            int i = p * 3;
            o[i] = s[i] * g0; o[i + 1] = s[i + 1] * g1; o[i + 2] = s[i + 2] * g2;
        });
        return outImg;
    }

    /// <summary>
    /// sRGB positive → the density space WB acts on ("pre-step-4 D"), as (N,3).
    /// Inverts the inversion tail: sRGB → linear → log10 (undo step 6) → undo step 5.
    ///
    /// Step 5 splits density into a luminance mean (scaled by grade) and a chroma deviation
    /// (scaled by chroma_grade·ccs). chroma_grade must be kept at its REAL value or the WB
    /// solve is biased — forcing it to grade over-saturates the positive the net sees and it
    /// over-corrects. The cross-channel mean makes this a 3×3 linear map with a clean
    /// closed-form inverse. <paramref name="chromaGrade"/> null (or == grade) takes the
    /// simple path, where it collapses to D = (d_adj + d_max - pivot)/grade + pivot.
    /// </summary>
    public static double[][] SrgbToPreStep4Density(ImageBuffer srgb, double grade, double pivot,
                                                   double dMax, double? chromaGrade = null,
                                                   double[]? ccs = null,
                                                   DensityEndpoints? endpoints = null)
    {
        // NOTE: Python does np.log10 on a float32 array → the log is evaluated in SINGLE
        // precision and only then widened. Keep MathF here; computing in double would
        // actually move us AWAY from the reference.
        float[] inv = Srgb.InverseLut;
        int n = srgb.PixelCount;
        float[] s = srgb.Data;
        var dAdj = new double[n][];
        for (int p = 0; p < n; p++)
        {
            var row = new double[3];
            for (int c = 0; c < 3; c++)
            {
                float lin = inv[Srgb.LutIndex(s[p * 3 + c])];
                row[c] = MathF.Log10(MathF.Max(lin, 1e-10f));
            }
            dAdj[p] = row;
        }

        var res = new double[n][];
        if (chromaGrade is null || chromaGrade == grade)
        {
            // Per-channel affine inverse, in the general (scale, offset) form. The caller supplies
            // the endpoints; with none given this falls back to a neutral set built from the
            // scalar d_max, which is what the retired grade/pivot chain reduced to once grade was
            // 1. This routine is reached only by the CLI's parity diagnostics.
            var ep = endpoints ?? DensityEndpoints.FromMeasured(
                new[] { dMax, dMax, dMax }, dMax);
            for (int p = 0; p < n; p++)
            {
                var r = new double[3];
                for (int c = 0; c < 3; c++) r[c] = ep.Invert(c, dAdj[p][c]);
                res[p] = r;
            }
            return res;
        }

        double[] ccsV = ccs ?? new[] { 1.0, 1.0, 1.0 };
        double cg = chromaGrade.Value;
        for (int p = 0; p < n; p++)
        {
            var sRow = new double[3];
            for (int c = 0; c < 3; c++) sRow[c] = dAdj[p][c] + dMax;
            // Luminance mean recovered from the channel-mean of s (the chroma part sums to ~0).
            double sMean = (sRow[0] + sRow[1] + sRow[2]) / 3.0;
            double dMean = (sMean - pivot * (1.0 - grade)) / grade;
            var r = new double[3];
            for (int c = 0; c < 3; c++)
                r[c] = (sRow[c] - pivot * (1.0 - grade) - grade * dMean) / (ccsV[c] * cg) + dMean;
            res[p] = r;
        }
        return res;
    }

    /// <summary>
    /// Inverse of <see cref="SrgbToPreStep4Density"/>: pre-step-4 D → sRGB positive. Mirrors
    /// the forward inversion tail (step 5 with the luminance/chroma split → step 6 →
    /// linear_to_srgb) exactly, so the iterative solver can re-render the positive the net
    /// sees under the current cumulative affine WITHOUT a full re-inversion.
    /// </summary>
    public static ImageBuffer PreStep4DensityToSrgb(double[][] d, int width, int height,
                                                    double grade, double pivot, double dMax,
                                                    double? chromaGrade = null, double[]? ccs = null)
    {
        float[] fwd = Srgb.ForwardLut;
        var outImg = new ImageBuffer(width, height);
        float[] o = outImg.Data;
        bool simple = chromaGrade is null || chromaGrade == grade;
        double[] ccsV = ccs ?? new[] { 1.0, 1.0, 1.0 };
        double cg = chromaGrade ?? grade;

        for (int p = 0; p < d.Length; p++)
        {
            var dAdj = new double[3];
            if (simple)
            {
                for (int c = 0; c < 3; c++) dAdj[c] = pivot + (d[p][c] - pivot) * grade - dMax;
            }
            else
            {
                double dMean = (d[p][0] + d[p][1] + d[p][2]) / 3.0;
                for (int c = 0; c < 3; c++)
                {
                    double dChroma = (d[p][c] - dMean) * ccsV[c];
                    dAdj[c] = pivot + (dMean - pivot) * grade + dChroma * cg - dMax;
                }
            }
            for (int c = 0; c < 3; c++)
            {
                double tPos = Math.Pow(10.0, dAdj[c]);
                float clamped = (float)Math.Clamp(tPos, 0.0, 1.0);
                o[p * 3 + c] = fwd[Srgb.LutIndex(clamped)];
            }
        }
        return outImg;
    }

    /// <summary>
    /// Solve the density-domain affine WB (wb_high, wb_offset) from one Deep-WB
    /// input/output positive pair.
    ///
    /// Both frames are mapped back to the pre-step-4 density space and regressed per channel
    /// as <c>D_target = wb_high·D_input + wb_offset</c> — exactly the step-4 form, so the
    /// solved pair reproduces the net's colour decision when plugged into the inversion.
    /// The caller MUST build the net input on the simple inversion path so input and output
    /// share one self-consistent tail; WB neutrality and chroma saturation are orthogonal,
    /// so collapsing chroma for the WB solve does not bias it.
    /// </summary>
    /// <returns>(wb_high, wb_offset, ok). ok=false when a channel's fit is degenerate
    /// (near-constant D_input) or lands outside the clip range — the arrays are then the
    /// clipped best-effort and the caller should warn / fall back rather than apply silently.</returns>
    public static (double[] WbHigh, double[] WbOffset, bool Ok) SolveWbAffineFromPositive(
        ImageBuffer inputSrgb, ImageBuffer outputSrgb,
        double grade, double pivot, double dMax,
        double? chromaGrade = null, double[]? ccs = null,
        (double Lo, double Hi)? wbHighClip = null, (double Lo, double Hi)? wbOffsetClip = null,
        DensityEndpoints? endpoints = null)
    {
        // grade is unused when endpoints are supplied — the slope lives in them instead — so the
        // positivity check only applies to the legacy parameterisation.
        if (endpoints is null && grade <= 0)
            throw new ArgumentException("grade must be positive for WB affine solve");
        var hClip = wbHighClip ?? (0.5, 2.0);
        var oClip = wbOffsetClip ?? (-0.5, 0.5);

        double[][] dIn = SrgbToPreStep4Density(inputSrgb, grade, pivot, dMax, chromaGrade, ccs, endpoints);
        double[][] dOut = SrgbToPreStep4Density(outputSrgb, grade, pivot, dMax, chromaGrade, ccs, endpoints);
        bool[][] valid = ValidMask(inputSrgb, outputSrgb);
        return RegressAffine(dIn, dOut, valid, hClip, oClip);
    }

    /// <summary>
    /// Per-channel mask dropping pixels clipped at either sRGB extreme in EITHER frame —
    /// those have lost their true density and would drag the regression's slope.
    /// </summary>
    private static bool[][] ValidMask(ImageBuffer inp, ImageBuffer outp)
    {
        const float Eps = 1e-3f;
        int n = inp.PixelCount;
        float[] si = inp.Data, so = outp.Data;
        var valid = new bool[n][];
        for (int p = 0; p < n; p++)
        {
            var v = new bool[3];
            for (int c = 0; c < 3; c++)
            {
                float a = si[p * 3 + c], b = so[p * 3 + c];
                v[c] = a > Eps && a < 1.0f - Eps && b > Eps && b < 1.0f - Eps;
            }
            valid[p] = v;
        }
        return valid;
    }

    /// <summary>Per-channel least-squares fit D_out = wb_high·D_in + wb_offset.</summary>
    private static (double[] WbHigh, double[] WbOffset, bool Ok) RegressAffine(
        double[][] dIn, double[][] dOut, bool[][] valid,
        (double Lo, double Hi) hClip, (double Lo, double Hi) oClip)
    {
        var wbHigh = new[] { 1.0, 1.0, 1.0 };
        var wbOffset = new[] { 0.0, 0.0, 0.0 };
        bool ok = true;

        for (int c = 0; c < 3; c++)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int p = 0; p < dIn.Length; p++)
                if (valid[p][c]) { xs.Add(dIn[p][c]); ys.Add(dOut[p][c]); }

            double varX = xs.Count >= 16 ? PopVar(xs) : 0.0;
            if (double.IsNaN(varX) || double.IsInfinity(varX) || varX < 1e-9) { ok = false; continue; }

            // Closed-form least squares (centred form) — algebraically identical to the
            // lstsq/SVD Python uses for this full-rank 2-parameter fit.
            double xm = Mean(xs), ym = Mean(ys);
            double num = 0, den = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                double dx = xs[i] - xm;
                num += dx * (ys[i] - ym);
                den += dx * dx;
            }
            wbHigh[c] = num / den;
            wbOffset[c] = ym - wbHigh[c] * xm;
        }

        var clippedH = new double[3];
        var clippedO = new double[3];
        for (int c = 0; c < 3; c++)
        {
            clippedH[c] = Math.Clamp(wbHigh[c], hClip.Lo, hClip.Hi);
            clippedO[c] = Math.Clamp(wbOffset[c], oClip.Lo, oClip.Hi);
        }
        if (!AllClose(clippedH, wbHigh) || !AllClose(clippedO, wbOffset)) ok = false;
        for (int c = 0; c < 3; c++)
            if (double.IsNaN(wbHigh[c]) || double.IsInfinity(wbHigh[c]) || wbHigh[c] <= 0) ok = false;

        Quantise(clippedH); Quantise(clippedO);
        return (clippedH, clippedO, ok);
    }

    /// <summary>
    /// Iterative Deep-WB affine solve. Each round re-renders the positive the net sees under
    /// the current cumulative affine, runs the net once, regresses the net's correction into
    /// a per-round affine increment, and composes it on. Composition in the density domain:
    /// applying (h, o) on top of (H, O) gives D·(H·h) + (O·h + o), so H ← H·h, O ← O·h + o.
    ///
    /// Cost: the caller inverts the frame ONCE to <paramref name="identitySrgb"/>; each round
    /// is then a cheap density-domain affine + sRGB re-encode + one net pass.
    /// </summary>
    /// <param name="identitySrgb">sRGB positive built with identity WB and chroma_grade==grade
    /// (the simple inversion path), already cropped to the kept region — the round-0 input.</param>
    /// <param name="wbOffsetEnabled">Default FALSE, and that is deliberate: white balance is
    /// carried by wb_high, the MULTIPLICATIVE channel ratio. Verified on a green-dominated real
    /// frame — letting the additive wb_offset absorb part of the correction left an orange cast
    /// (ratio 1.59) while a pure-wb_high solve reached 1.24.</param>
    /// <returns>(wb_high, wb_offset, converged, reason). reason is "" when converged, "guard"
    /// when a round's regression hit the sanity guard (suggest manual box-sampling), or
    /// "maxiter" when it ran out of iterations.</returns>
    public static (double[] WbHigh, double[] WbOffset, bool Converged, string Reason) AutoWbAffineIterative(
        ImageBuffer identitySrgb, IDeepWbCorrector corrector,
        double grade, double pivot, double dMax,
        double? chromaGrade = null, double[]? ccs = null,
        int maxIter = 50, double tol = 5e-3, double plateauTol = 1e-4,
        bool wbOffsetEnabled = false,
        (double Lo, double Hi)? wbHighClip = null, (double Lo, double Hi)? wbOffsetClip = null,
        Action<int, int, double>? progressCb = null)
    {
        if (grade <= 0) throw new ArgumentException("grade must be positive for affine WB iteration");
        var hClip = wbHighClip ?? (0.3, 3.0);
        var oClip = wbOffsetClip ?? (-0.5, 0.5);

        // Round-0 densities are fixed (the identity positive); only the RENDERED positive the
        // net sees changes each round, via the cumulative affine.
        double[][] d0 = SrgbToPreStep4Density(identitySrgb, grade, pivot, dMax, chromaGrade, ccs);

        var bigH = new[] { 1.0, 1.0, 1.0 };
        var bigO = new[] { 0.0, 0.0, 0.0 };
        bool converged = false;
        string reason = "maxiter";
        double prevDev = double.PositiveInfinity;

        for (int it = 0; it < maxIter; it++)
        {
            var dCur = new double[d0.Length][];
            for (int p = 0; p < d0.Length; p++)
            {
                var r = new double[3];
                for (int c = 0; c < 3; c++) r[c] = d0[p][c] * bigH[c] + bigO[c];
                dCur[p] = r;
            }
            ImageBuffer curSrgb = PreStep4DensityToSrgb(dCur, identitySrgb.Width, identitySrgb.Height,
                                                        grade, pivot, dMax, chromaGrade, ccs);

            var (inp, outp) = corrector.CorrectOnce(curSrgb);

            double[][] dInR = SrgbToPreStep4Density(inp, grade, pivot, dMax, chromaGrade, ccs);
            double[][] dOutR = SrgbToPreStep4Density(outp, grade, pivot, dMax, chromaGrade, ccs);
            var (h, o, ok) = RegressAffine(dInR, dOutR, ValidMask(inp, outp), hClip, oClip);
            if (!ok)
            {
                // A round's fit is unreliable — stop rather than compound the error.
                reason = "guard";
                break;
            }

            // Strip brightness, keep only chroma: WB is a RELATIVE channel balance; absolute
            // level is exposure/d_max's job. Without this the brightness pull compounds across
            // rounds and drives wb_high toward 0 (collapse → severe darkening).
            double gm = Math.Exp((Math.Log(Math.Max(h[0], 1e-6)) + Math.Log(Math.Max(h[1], 1e-6))
                                + Math.Log(Math.Max(h[2], 1e-6))) / 3.0);
            for (int c = 0; c < 3; c++) h[c] /= gm;
            if (wbOffsetEnabled)
            {
                double om = (o[0] + o[1] + o[2]) / 3.0;
                for (int c = 0; c < 3; c++) o[c] -= om;
            }
            else o = new[] { 0.0, 0.0, 0.0 };   // pure wb_high white balance

            double dev = Math.Max(Math.Max(Math.Abs(h[0] - 1.0), Math.Max(Math.Abs(h[1] - 1.0), Math.Abs(h[2] - 1.0))),
                                  Math.Max(Math.Abs(o[0]), Math.Max(Math.Abs(o[1]), Math.Abs(o[2]))));
            progressCb?.Invoke(it + 1, maxIter, dev);

            // Plateau early-stop: dev drops fast for a few rounds then flattens on the net's
            // residual noise floor (sRGB↔density round-trip + model bias). Past that, further
            // rounds only DRIFT the solve away from a good answer — verified: forcing tol below
            // the floor pushes wb_high further from 1. So keep the PREVIOUS cumulative pair,
            // do NOT compose this drifting increment, and call it converged.
            if (it > 0 && (prevDev - dev) < plateauTol && dev < tol * 5)
            {
                converged = true;
                reason = "";
                break;
            }
            prevDev = dev;

            for (int c = 0; c < 3; c++)
            {
                bigO[c] = bigO[c] * h[c] + o[c];
                bigH[c] = bigH[c] * h[c];
            }

            if (dev < tol) { converged = true; reason = ""; break; }
        }

        Quantise(bigH); Quantise(bigO);
        return (bigH, bigO, converged, reason);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static double Mean(List<double> v)
    {
        double s = 0;
        for (int i = 0; i < v.Count; i++) s += v[i];
        return s / v.Count;
    }

    private static double PopVar(List<double> v)
    {
        double m = Mean(v), acc = 0;
        for (int i = 0; i < v.Count; i++) { double e = v[i] - m; acc += e * e; }
        return acc / v.Count;
    }

    // np.allclose defaults: rtol=1e-5, atol=1e-8.
    private static bool AllClose(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-8 + 1e-5 * Math.Abs(b[i])) return false;
        return true;
    }

    // Python returns these as float32; round-trip so callers see the same value.
    private static void Quantise(double[] v)
    {
        for (int i = 0; i < v.Length; i++) v[i] = (float)v[i];
    }
}
