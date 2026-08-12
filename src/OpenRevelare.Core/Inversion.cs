namespace OpenRevelare.Core;

/// <summary>
/// Density-domain negative inversion — the heart of the pipeline.
///
/// Faithful CPU port of <c>negative/inversion.py::invert()</c>, optimised for throughput:
///   • Row-parallel (<c>Parallel.For</c>) — scales with core count.
///   • Per-channel LUT folds steps 1–4 (T → density) so the expensive log10 is
///     computed 3×65536 times up-front instead of once per pixel. For 16-bit TIFF
///     input the LUT is EXACT (input has only 65536 distinct values), so parity is
///     unaffected. Phase 1b (RAW, values possibly &gt; 1) will extend the LUT
///     domain / add interpolation — see the clamp note below.
///
/// Steps (per-pixel, computed in double, stored back as float):
///   1. T_norm = T / T_base
///   2. D = -log10(max(T_norm, 10^-d_max))          ┐ folded into per-channel LUT
///   3. D += scan_exposure_ev * log10(2)             │
///   4. D = D * wb_high + (wb_offset - mean(wb_offset))┘
///   5. D_adj = pivot + (D - pivot)*grade + chroma - d_max
///   6. T_pos = 10^(D_adj)
/// </summary>
public static class Inversion
{
    private const double Ln10 = 2.302585092994046;
    private const double Log10_2 = 0.3010299956639812;
    private const double Tol = 1e-8;
    private const int LutSize = 65536; // one entry per distinct 16-bit input level

    /// <summary>
    /// Convert a linear-light negative frame to a positive.
    /// </summary>
    /// <param name="chromaAmp">Per-channel factor the decouple matrix widened chroma by
    /// (null / all-1 = white-light). chroma_grade is divided by it.</param>
    /// <param name="chromaMatrix">Axis-accurate 3×3 compensation (row-major); supersedes
    /// chromaAmp when non-identity. null = white-light roll.</param>
    /// <param name="blackFloor">Film-base black-point normalisation, folded into the output
    /// write instead of running as its own sweep afterwards: <c>max((v - floor)/(1 - floor), 0)</c>.
    /// Pass null to skip it. Pointwise either way, so the result is identical — but as a
    /// separate pass it re-read and re-wrote the entire frame (288 MB at 24 MP) to do one
    /// subtract and one multiply per sample.</param>
    public static ImageBuffer Invert(
        ImageBuffer image,
        FrameParams cal,
        double[]? chromaAmp = null,
        double[,]? chromaMatrix = null,
        double? blackFloor = null)
    {
        double[] amp = chromaAmp ?? new[] { 1.0, 1.0, 1.0 };
        bool ampIdentity = ApproxAll(amp, 1.0);
        bool useMatrix = chromaMatrix != null && MatrixIsActive(chromaMatrix);

        double m00 = 1, m01 = 0, m02 = 0, m10 = 0, m11 = 1, m12 = 0, m20 = 0, m21 = 0, m22 = 1;
        if (chromaMatrix != null)
        {
            m00 = chromaMatrix[0, 0]; m01 = chromaMatrix[0, 1]; m02 = chromaMatrix[0, 2];
            m10 = chromaMatrix[1, 0]; m11 = chromaMatrix[1, 1]; m12 = chromaMatrix[1, 2];
            m20 = chromaMatrix[2, 0]; m21 = chromaMatrix[2, 1]; m22 = chromaMatrix[2, 2];
        }

        bool channelScaleActive = !ApproxAll(cal.ChromaChannelScale, 1.0);
        // Decomposition is needed only for the Path-A mechanisms (a decouple chroma matrix or
        // per-channel amp) and the per-channel chroma scale. Chroma itself no longer has its own
        // coefficient: Cineon applies ONE gamma to all three channels, and chroma — being the
        // per-channel deviation — follows luminance proportionally without a second parameter.
        // What used to be chroma_grade patched a missing colour-space conversion; that belongs to
        // InputTransform and OutputRender now.
        bool needDecomp = useMatrix || !ampIdentity || channelScaleActive;

        // ── Per-channel density LUTs (folds steps 1–4); cached, see DensityLuts ──
        double[][] dLut = DensityLuts(cal);
        double[] lut0 = dLut[0], lut1 = dLut[1], lut2 = dLut[2];

        // Path A widened chroma by `amp`; the decomposed branch divides it back out.
        double ampC0 = amp[0], ampC1 = amp[1], ampC2 = amp[2];

        double cs0 = cal.ChromaChannelScale[0], cs1 = cal.ChromaChannelScale[1], cs2 = cal.ChromaChannelScale[2];

        // Direct-compute params for the >1 fast path: the LUT only covers input
        // [0,1] (exact for 16-bit). Pre-inversion ops (vignette, later RAW
        // highlights) can push T above 1, where the LUT would clamp and get the
        // density wrong — those rare pixels compute steps 1–4 directly instead.
        // Per-channel: with measured endpoints the floor follows the channel, not d_max.
        double floorV0 = Math.Pow(10.0, -DensityFloor(cal, 0));
        double floorV1 = Math.Pow(10.0, -DensityFloor(cal, 1));
        double floorV2 = Math.Pow(10.0, -DensityFloor(cal, 2));
        bool biasActive = cal.ScanExposureEv != 0.0;
        double biasV = cal.ScanExposureEv * Log10_2;
        // Mirrors BuildDensityLuts: under the endpoint model these live in the endpoints.
        bool endpointModel = cal.DMaxPerChannel is { Length: 3 };
        bool wbHighActive = !endpointModel && !ApproxAll(cal.WbHigh, 1.0);
        bool wbOffsetActive = !endpointModel && cal.WbOffset.Any(x => Math.Abs(x) > Tol);
        double wbOffMean = (cal.WbOffset[0] + cal.WbOffset[1] + cal.WbOffset[2]) / 3.0;
        double tb0 = cal.TBase[0], tb1 = cal.TBase[1], tb2 = cal.TBase[2];
        double wh0 = cal.WbHigh[0], wh1 = cal.WbHigh[1], wh2 = cal.WbHigh[2];
        double wo0 = cal.WbOffset[0] - wbOffMean, wo1 = cal.WbOffset[1] - wbOffMean, wo2 = cal.WbOffset[2] - wbOffMean;

        // Canonical per-channel affine for the non-decomposed path. Hoisted out of the loop:
        // three multiplies and three adds replace the pivot/grade/d_max arithmetic per pixel.
        DensityEndpoints endpoints = DensityEndpoints.For(cal);
        double es0 = endpoints.Scale[0], es1 = endpoints.Scale[1], es2 = endpoints.Scale[2];
        double eo0 = endpoints.Offset[0], eo1 = endpoints.Offset[1], eo2 = endpoints.Offset[2];
        // The single multiplier the chroma matrix is scaled by. Under the legacy parameters every
        // channel's slope IS grade, so this reduces to grade exactly; under measured endpoints the
        // slopes differ and their mean is the scalar that keeps M's output inside the sum-zero
        // plane (see the useMatrix branch).
        double chromaSlope = (es0 + es1 + es2) / 3.0;

        double DirectDensity(double v, double tb, double wh, double woc, double flr)
        {
            double d = -Math.Log10(Math.Max(v / tb, flr));
            if (biasActive) d += biasV;
            if (wbHighActive) d *= wh;
            if (wbOffsetActive) d += woc;
            return d;
        }

        // Black floor, if folded in. Mirrors Pipeline's standalone loop exactly, including the
        // 0 < floor < 1 admissibility test and the "no upper clip" rule.
        bool bfActive = blackFloor is double bfv && bfv > 0.0 && bfv < 1.0;
        float bf = bfActive ? (float)blackFloor!.Value : 0.0f;
        float bfScale = bfActive ? (float)(1.0 / (1.0 - blackFloor!.Value)) : 1.0f;

        var outImg = new ImageBuffer(image.Width, image.Height);
        float[] src = image.Data;
        float[] dst = outImg.Data;
        int width = image.Width;

        Parallel.For(0, image.Height, y =>
        {
            int rowStart = y * width * 3;
            int rowEnd = rowStart + width * 3;
            for (int i = rowStart; i < rowEnd; i += 3)
            {
                // 1–4: per-channel density. LUT for input in [0,1] (exact); direct
                // compute for T > 1 (vignette-boosted / RAW highlights).
                float v0 = src[i], v1 = src[i + 1], v2 = src[i + 2];
                double d0 = v0 <= 1.0f ? lut0[ToIndex(v0)] : DirectDensity(v0, tb0, wh0, wo0, floorV0);
                double d1 = v1 <= 1.0f ? lut1[ToIndex(v1)] : DirectDensity(v1, tb1, wh1, wo1, floorV1);
                double d2 = v2 <= 1.0f ? lut2[ToIndex(v2)] : DirectDensity(v2, tb2, wh2, wo2, floorV2);

                // 5: density-domain inversion.
                double a0, a1, a2;
                if (needDecomp)
                {
                    double dMean = (d0 + d1 + d2) / 3.0;
                    double c0 = d0 - dMean, c1 = d1 - dMean, c2 = d2 - dMean;

                    if (channelScaleActive) { c0 *= cs0; c1 *= cs1; c2 *= cs2; }

                    // The matrix REPLACES chromaAmp, it does not stack with it: this branch uses
                    // the bare chroma_grade and never reads effChroma. That is correct, not an
                    // oversight — ChromaAxisCompensationMatrix is built from 1/ampYb and 1/ampRg,
                    // so it already carries the amplification, resolved per chroma axis instead
                    // of per RGB channel. Passing both leaves the amp silently unused; the else
                    // branch below is the only consumer, for callers with no matrix.
                    if (useMatrix)
                    {
                        // ONE scalar on the matrix output, not the per-channel slopes. M maps the
                        // sum-zero plane to itself, so a single multiplier keeps the result summing
                        // to zero — i.e. pure chroma. Scaling per channel instead would break that
                        // (measured leak ~9e-3 on a typical pixel) and quietly push luminance
                        // around, undoing the balance the endpoints just established.
                        double n0 = (m00 * c0 + m01 * c1 + m02 * c2) * chromaSlope;
                        double n1 = (m10 * c0 + m11 * c1 + m12 * c2) * chromaSlope;
                        double n2 = (m20 * c0 + m21 * c1 + m22 * c2) * chromaSlope;
                        c0 = n0; c1 = n1; c2 = n2;
                    }
                    else
                    {
                        // No matrix: chroma follows its own channel's slope, which is exactly what
                        // the plain per-channel affine would have done (lum + S·c reconstructs
                        // S·d + b identically), then the per-channel amp is applied on top.
                        c0 *= es0 / ampC0; c1 *= es1 / ampC1; c2 *= es2 / ampC2;
                        if (!ampIdentity)
                        {
                            double cm = (c0 + c1 + c2) / 3.0;
                            c0 -= cm; c1 -= cm; c2 -= cm;
                        }
                    }

                    // Luminance carries the per-channel endpoint affine — that is where the
                    // highlight colour balance lives, so it must NOT collapse to one channel.
                    a0 = es0 * dMean + eo0 + c0;
                    a1 = es1 * dMean + eo1 + c1;
                    a2 = es2 * dMean + eo2 + c2;
                }
                else
                {
                    // Canonical per-channel affine (DensityEndpoints). For a legacy roll these
                    // coefficients reduce to pivot + (d-pivot)*grade - d_max exactly, gating
                    // included, so this path is bit-identical to what it replaced; for a roll
                    // with measured per-channel endpoints it is the endpoint normalisation
                    // itself, with no grade anywhere in it.
                    a0 = es0 * d0 + eo0;
                    a1 = es1 * d1 + eo1;
                    a2 = es2 * d2 + eo2;
                }

                // 6: back to linear (+ black floor, when folded in).
                float o0 = (float)Math.Exp(a0 * Ln10);
                float o1 = (float)Math.Exp(a1 * Ln10);
                float o2 = (float)Math.Exp(a2 * Ln10);
                if (bfActive)
                {
                    o0 = (o0 - bf) * bfScale; if (o0 < 0.0f) o0 = 0.0f;
                    o1 = (o1 - bf) * bfScale; if (o1 < 0.0f) o1 = 0.0f;
                    o2 = (o2 - bf) * bfScale; if (o2 < 0.0f) o2 = 0.0f;
                }
                dst[i] = o0; dst[i + 1] = o1; dst[i + 2] = o2;
            }
        });

        return outImg;
    }

    /// <summary>Reconstruct the 16-bit input level from a [0,1] float and clamp to LUT range.</summary>
    private static int ToIndex(float v)
    {
        int idx = (int)(v * 65535.0f + 0.5f);
        if (idx < 0) return 0;
        if (idx > 65535) return 65535;
        return idx;
    }

    // ── Density-LUT cache ────────────────────────────────────────────────────────
    //
    // Building the tables costs 196,608 Math.Log10 calls and THREE 512 KB arrays — every one of
    // them a large-object allocation. That was paid on every single Invert call: every preview
    // render, every drag step (inline on the UI thread), every thumbnail in the roll, every
    // round of the smart-WB loop, every contact-sheet cell. Measured at 1.86 ms and 1.50 MB per
    // call REGARDLESS of image size, so on a 256 px thumbnail it dwarfed the actual pixel work
    // and it was the main thing dragging the process into full Gen2 collections mid-drag.
    //
    // The tables depend on exactly eleven numbers, and a Stage-2 edit (exposure, contrast,
    // curves, saturation, levels, WB gains) changes none of them — so dragging any Stage-2
    // slider now reuses them outright.
    //
    // ONE slot, and the key is compared with EXACT double equality rather than a tolerance:
    // the whole point of the LUT is to be bit-identical to the direct computation, so "close
    // enough" parameters must miss and rebuild. A roll-wide thumbnail pass with per-frame
    // calibration simply thrashes the slot, which is no worse than the old unconditional build.
    //
    // Lock-free: the entry is built locally and published by a single reference assignment,
    // which is atomic. Two threads racing on different parameters just rebuild; neither can
    // observe a half-built table, and nobody ever mutates a published one.
    private sealed class DensityLutEntry
    {
        public double Tb0, Tb1, Tb2, Wh0, Wh1, Wh2, Wo0, Wo1, Wo2, ScanEv, DMax;
        // Part of the key: it sets the per-channel density floor, so two rolls differing only
        // here must not share tables.
        public double[]? DMaxPerCh;
        public double[][] Luts = null!;

        public bool Matches(FrameParams c) =>
            Tb0 == c.TBase[0] && Tb1 == c.TBase[1] && Tb2 == c.TBase[2] &&
            Wh0 == c.WbHigh[0] && Wh1 == c.WbHigh[1] && Wh2 == c.WbHigh[2] &&
            Wo0 == c.WbOffset[0] && Wo1 == c.WbOffset[1] && Wo2 == c.WbOffset[2] &&
            ScanEv == c.ScanExposureEv && DMax == c.DMax &&
            SameEndpoints(DMaxPerCh, c.DMaxPerChannel);

        private static bool SameEndpoints(double[]? a, double[]? b)
        {
            if (a is null || b is null) return a is null && b is null;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    private static DensityLutEntry? _lutCache;

    /// <summary>The per-channel T→density tables for <paramref name="cal"/>, from the cache when
    /// the eleven inputs are bit-identical to the last build. READ ONLY — shared across callers.</summary>
    private static double[][] DensityLuts(FrameParams cal)
    {
        DensityLutEntry? hit = _lutCache;
        if (hit is not null && hit.Matches(cal)) return hit.Luts;

        var entry = new DensityLutEntry
        {
            Tb0 = cal.TBase[0], Tb1 = cal.TBase[1], Tb2 = cal.TBase[2],
            Wh0 = cal.WbHigh[0], Wh1 = cal.WbHigh[1], Wh2 = cal.WbHigh[2],
            Wo0 = cal.WbOffset[0], Wo1 = cal.WbOffset[1], Wo2 = cal.WbOffset[2],
            ScanEv = cal.ScanExposureEv, DMax = cal.DMax,
            DMaxPerCh = cal.DMaxPerChannel is { Length: 3 } dmc ? (double[])dmc.Clone() : null,
            Luts = BuildDensityLuts(cal),
        };
        _lutCache = entry;
        return entry.Luts;
    }

    /// <summary>Precompute the per-channel T→density mapping (steps 1–4) for every 16-bit level.</summary>
    private static double[][] BuildDensityLuts(FrameParams cal)
    {
        // The density floor is where the log is allowed to bottom out. In the legacy model that
        // is d_max, because d_max IS the deepest density the roll reaches. With measured
        // per-channel endpoints those are two different numbers — d_max becomes the OUTPUT range
        // (where white lands) while the deepest density is dMaxPerChannel[c] — and clamping at
        // the output range would truncate the highlight end before step 5 sees it, landing the
        // darkest area short of white and tinted.
        bool biasActive = cal.ScanExposureEv != 0.0;
        double bias = cal.ScanExposureEv * Log10_2;
        // Under the endpoint model wb_high / wb_offset are folded into the endpoints
        // (DensityEndpoints.FromMeasured), so applying them here too would double them.
        bool endpointModel = cal.DMaxPerChannel is { Length: 3 };
        bool wbHighActive = !endpointModel && !ApproxAll(cal.WbHigh, 1.0);
        bool wbOffsetActive = !endpointModel && cal.WbOffset.Any(x => Math.Abs(x) > Tol);
        double wbOffsetMean = (cal.WbOffset[0] + cal.WbOffset[1] + cal.WbOffset[2]) / 3.0;

        var luts = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double tBase = cal.TBase[c];
            double wbHigh = cal.WbHigh[c];
            double wbOff = cal.WbOffset[c] - wbOffsetMean;
            double floor = Math.Pow(10.0, -DensityFloor(cal, c));
            var lut = new double[LutSize];
            for (int idx = 0; idx < LutSize; idx++)
            {
                double t = idx / 65535.0;
                double d = -Math.Log10(Math.Max(t / tBase, floor));
                if (biasActive) d += bias;
                if (wbHighActive) d *= wbHigh;
                if (wbOffsetActive) d += wbOff;
                lut[idx] = d;
            }
            luts[c] = lut;
        }
        return luts;
    }

    /// <summary>
    /// How deep density is allowed to go for channel <paramref name="c"/> — the measured
    /// endpoint when the roll has one, otherwise d_max (which in the legacy model is the same
    /// thing). Kept in one place because the LUT and the direct &gt;1 path must agree, and
    /// because it is the exact spot where "deepest density" and "output range" stop being the
    /// same number.
    /// </summary>
    private static double DensityFloor(FrameParams cal, int c) =>
        cal.DMaxPerChannel is { Length: 3 } dm ? Math.Max(dm[c], 1e-6) : cal.DMax;

    private static bool ApproxAll(double[] v, double target)
    {
        double atol = Tol + 1e-5 * Math.Abs(target);
        foreach (var x in v)
            if (Math.Abs(x - target) > atol) return false;
        return true;
    }

    private static bool MatrixIsActive(double[,] m)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double ident = i == j ? 1.0 : 0.0;
                if (Math.Abs(m[i, j] - ident) > Tol + 1e-5 * Math.Abs(ident)) return true;
            }
        return false;
    }
}
