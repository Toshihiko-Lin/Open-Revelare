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
        // The floor is the encoding ceiling, the same constant for all three channels — see
        // DensityFloor for why it is not the measured endpoint. Kept as three locals so the
        // inner loop reads them like the per-channel t_base beside it.
        double floorV0 = Math.Pow(10.0, -DensityFloor(cal, 0));
        double floorV1 = Math.Pow(10.0, -DensityFloor(cal, 1));
        double floorV2 = Math.Pow(10.0, -DensityFloor(cal, 2));
        // The two endpoints are NOT applied here. Steps 1–4 produce the film-base-normalised
        // density and stop; both ends enter at step 5 through DensityEndpoints. Mirrors
        // BuildDensityLuts.
        //
        // The scan-exposure bias is gone with it: shifting the density zero point is what MOVING
        // BOTH ENDPOINTS TOGETHER does, so it was a twelfth parameter for a job the six already
        // do. It also sat AFTER the density floor, which is why it was never exactly equivalent
        // to a t_base change and could not simply be folded away.
        double tb0 = cal.TBase[0], tb1 = cal.TBase[1], tb2 = cal.TBase[2];

        // Canonical per-channel affine for the non-decomposed path. Hoisted out of the loop:
        // three multiplies and three adds replace the pivot/grade/d_max arithmetic per pixel.
        DensityEndpoints endpoints = DensityEndpoints.For(cal);
        double es0 = endpoints.Scale[0], es1 = endpoints.Scale[1], es2 = endpoints.Scale[2];
        double eo0 = endpoints.Offset[0], eo1 = endpoints.Offset[1], eo2 = endpoints.Offset[2];

        static double DirectDensity(double v, double tb, double flr)
            => -Math.Log10(Math.Max(v / tb, flr));

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
                double d0 = v0 <= 1.0f ? lut0[ToIndex(v0)] : DirectDensity(v0, tb0, floorV0);
                double d1 = v1 <= 1.0f ? lut1[ToIndex(v1)] : DirectDensity(v1, tb1, floorV1);
                double d2 = v2 <= 1.0f ? lut2[ToIndex(v2)] : DirectDensity(v2, tb2, floorV2);

                // 5: density-domain inversion — the canonical per-channel affine
                // (DensityEndpoints). For a legacy roll these coefficients reduce to
                // pivot + (d-pivot)*grade - d_max exactly, gating included; for a roll with
                // measured per-channel endpoints it is the endpoint normalisation itself, with
                // no grade anywhere in it. EVERY roll goes through this line first: it is what
                // pins dMin_c to black and dMax_c to white for each channel.
                double a0 = es0 * d0 + eo0;
                double a1 = es1 * d1 + eo1;
                double a2 = es2 * d2 + eo2;

                if (needDecomp)
                {
                    // Path A chroma compensation (and the per-channel chroma scale) act on the
                    // chroma of the ENDPOINT-NORMALISED density, not of the raw density. The
                    // difference is the whole of the Path A colour cast that used to be here.
                    //
                    // Raw density chroma d_c - mean(d) is not scene colour: it carries the orange
                    // mask and the three layers' unequal contrast, so at the calibrated white
                    // (d = dMax) it is far from zero — blue densest by ~0.3. Compressing THAT by
                    // M (built from 1/amp, so it only ever shrinks) pulled the white point off
                    // neutral: at amp 1.6 a calibrated white rendered R 1.59 / G 1.00 / B 0.72,
                    // and the black end drifted the same way — a yellow-red cast over the whole
                    // roll that no D-max measurement could remove, because the render was not
                    // honouring the endpoints being measured. Path B never showed it, having no
                    // matrix. Before the endpoint model wb_high was folded into steps 1–4, so the
                    // raw chroma at white already WAS zero and M had nothing to shrink there;
                    // moving the per-channel slope to step 5 (bd8a1aa) put the decomposition
                    // ahead of the balance without moving the balance ahead of it.
                    //
                    // After the affine, a neutral tone has a_0 = a_1 = a_2 by construction, so
                    // chroma = a - mean(a) is zero at both endpoints and on every grey between
                    // them, and M only touches what it was measured to touch — scene saturation
                    // the decouple matrix widened. No slope multiplier either: `a` is already in
                    // output-density units, and M maps the sum-zero plane to itself, so the
                    // result is still pure chroma and mean(a) — the luminance — is untouched.
                    double aMean = (a0 + a1 + a2) / 3.0;
                    double c0 = a0 - aMean, c1 = a1 - aMean, c2 = a2 - aMean;

                    if (channelScaleActive) { c0 *= cs0; c1 *= cs1; c2 *= cs2; }

                    // The matrix REPLACES chromaAmp, it does not stack with it: this branch never
                    // reads amp. That is correct, not an oversight — ChromaAxisCompensationMatrix
                    // is built from 1/ampYb and 1/ampRg, so it already carries the amplification,
                    // resolved per chroma axis instead of per RGB channel. Passing both leaves the
                    // amp silently unused; the else branch below is the only consumer, for callers
                    // with no matrix.
                    if (useMatrix)
                    {
                        double n0 = m00 * c0 + m01 * c1 + m02 * c2;
                        double n1 = m10 * c0 + m11 * c1 + m12 * c2;
                        double n2 = m20 * c0 + m21 * c1 + m22 * c2;
                        c0 = n0; c1 = n1; c2 = n2;
                    }
                    else if (!ampIdentity)
                    {
                        // Per-channel amp, then re-centre so the result stays pure chroma.
                        c0 /= ampC0; c1 /= ampC1; c2 /= ampC2;
                        double cm = (c0 + c1 + c2) / 3.0;
                        c0 -= cm; c1 -= cm; c2 -= cm;
                    }

                    a0 = aMean + c0;
                    a1 = aMean + c1;
                    a2 = aMean + c2;
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
    // The tables depend on exactly three numbers — t_base, one per channel. Neither a Stage-2
    // edit (exposure, contrast, curves, saturation, levels, WB gains) nor a Stage-1 endpoint
    // solve touches those, so dragging any of those sliders reuses the tables outright.
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
        // Exactly what BuildDensityLuts reads, and nothing more — which is now t_base alone. The
        // endpoints are NOT here: steps 1–4 stop at the film-base-normalised density, and both
        // ends enter afterwards at step 5.
        //
        // DMaxPerCh USED TO BE HERE, not as an endpoint but because it set the per-channel density
        // floor. Now that the floor is FrameParams.DensityCeiling — a constant — the tables do not
        // depend on it, so keying on it would only cost rebuilds. That is the practical half of
        // the fix in DensityFloor: every automatic solve writes DMaxPerChannel, and each write
        // used to invalidate three 512 KB tables it had no influence over.
        public double Tb0, Tb1, Tb2;
        public double[][] Luts = null!;

        public bool Matches(FrameParams c) =>
            Tb0 == c.TBase[0] && Tb1 == c.TBase[1] && Tb2 == c.TBase[2];
    }

    private static DensityLutEntry? _lutCache;

    /// <summary>The per-channel T→density tables for <paramref name="cal"/>, from the cache when
    /// the three t_base values are bit-identical to the last build. READ ONLY — shared across
    /// callers.</summary>
    private static double[][] DensityLuts(FrameParams cal)
    {
        DensityLutEntry? hit = _lutCache;
        if (hit is not null && hit.Matches(cal)) return hit.Luts;

        var entry = new DensityLutEntry
        {
            Tb0 = cal.TBase[0], Tb1 = cal.TBase[1], Tb2 = cal.TBase[2],
            Luts = BuildDensityLuts(cal),
        };
        _lutCache = entry;
        return entry.Luts;
    }

    /// <summary>Precompute the per-channel T→density mapping (steps 1–4) for every 16-bit level.</summary>
    private static double[][] BuildDensityLuts(FrameParams cal)
    {
        // Neither endpoint is applied here — these tables stop at the film-base-normalised
        // density, and DensityEndpoints maps both ends at step 5.
        var luts = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double tBase = cal.TBase[c];
            double floor = Math.Pow(10.0, -DensityFloor(cal, c));
            var lut = new double[LutSize];
            for (int idx = 0; idx < LutSize; idx++)
            {
                double t = idx / 65535.0;
                lut[idx] = -Math.Log10(Math.Max(t / tBase, floor));
            }
            luts[c] = lut;
        }
        return luts;
    }

    /// <summary>
    /// How deep density is allowed to go — <see cref="FrameParams.DensityCeiling"/>, the encoding
    /// domain's ceiling. Kept in one place because the LUT and the direct &gt;1 path must agree.
    ///
    /// THREE DIFFERENT QUANTITIES live near each other here, and this used to conflate two of them:
    ///
    ///   OutputRange     where the output's black lands           — a constant
    ///   DensityCeiling  where -log10 is allowed to stop          — a constant  ← this one
    ///   DMaxPerChannel  the measured highlight endpoint          — CALIBRATION
    ///
    /// Clamping at the OUTPUT RANGE would truncate the highlight end before step 5 sees it,
    /// landing the darkest area short of white and tinted — which is why it was not that.
    ///
    /// But clamping at the MEASURED ENDPOINT, as this did, is the Cineon equivalent of using 685
    /// where 1032 belongs: the encoding domain's ceiling became a calibration output. Two
    /// consequences, both real. Any pixel legitimately denser than the endpoint was silently
    /// truncated. And the tables' validity became tied to a value every automatic solve writes
    /// (roll calibration, highlight alignment, Deep-WB), so each of those forced a full rebuild —
    /// 196,608 Math.Log10 calls and three 512 KB LOH allocations — for tables whose contents the
    /// endpoint no longer influences at all.
    ///
    /// The ceiling is a constant, so it is not read from <paramref name="cal"/>; the parameter
    /// stays for call-site symmetry with the per-channel form this replaced.
    /// </summary>
    private static double DensityFloor(FrameParams cal, int c) => FrameParams.DensityCeiling;

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
