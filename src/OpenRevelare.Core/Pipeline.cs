namespace OpenRevelare.Core;

/// <summary>
/// Processing pipeline orchestration — one frame, negative → positive.
///
/// CPU only, and deliberately so. There was a D3D12/ComputeSharp backend behind an
/// <c>IGpuAccelerator</c> hook here; it was removed (2026-08) once measurement showed it had
/// no target left. Its fused kernel covered inversion + black floor + sRGB and bailed out to
/// the CPU whenever Stage 2, geometry, sprocket masking or Path A decoupling was active —
/// i.e. essentially always, in the GUI. Meanwhile the CPU path itself came down to ~20 ms for
/// a 1600 px preview, and the one genuinely heavy operation left, RAW decode, is ~38% serial
/// entropy decoding that no GPU can touch. Re-adding it would mean a stateful frame-resident
/// session, not the stateless per-call upload/download the old interface implied.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Which chroma matrix the inversion should use.
    ///
    /// Path A wins when present: its matrix is solved for that roll's own narrow-band light
    /// source, so it describes a real measurement of THIS setup, where the C-41 matrix describes
    /// the process in general. They occupy the same slot in the inversion and must not stack.
    /// </summary>
    public static double[,]? ResolveChromaMatrix(FrameParams cal) =>
        cal.DecoupleChromaMatrix ?? (cal.UseC41Crosstalk ? C41Crosstalk.Direction : null);

    /// <summary>Run Stage 1 (+ black floor) and, for BASIC intent, the sRGB exit TRC.</summary>
    /// <param name="applyBlackFloor">Apply the film-base black-point normalisation. Set FALSE for the
    /// Deep-WB affine solve, whose density inversion assumes the RAW positive 10^(d_adj).</param>
    public static ImageBuffer ProcessFrame(ImageBuffer img, FrameParams cal,
                                           bool applyBlackFloor = true)
    {
        // ── Pre-inversion linear-domain corrections (distortion → vignette) ───────
        // Applied to a working copy so the caller's buffer is untouched. Order
        // mirrors pipeline.py: (lensfun) → distortion → (lcc) → vignette → (decouple).
        ImageBuffer src = img;
        bool preOps = cal.DistortionK1 != 0.0 || cal.LccFlatField != null
                      || cal.VignetteAmount != 0.0 || cal.DecoupleMatrix != null;
        if (preOps)
        {
            src = new ImageBuffer(img.Width, img.Height, (float[])img.Data.Clone());
            if (cal.DistortionK1 != 0.0)
                src = LensCorrections.ApplyDistortion(src, cal.DistortionK1);
            if (cal.LccFlatField != null)
                Lcc.Apply(src.Data, src.Width, src.Height, cal.LccFlatField);
            if (cal.VignetteAmount != 0.0)
                LensCorrections.ApplyVignette(src.Data, src.Width, src.Height,
                                              cal.VignetteAmount, cal.VignetteFalloff);
        }

        // Sprocket/light-board mask — detected on the raw negative BEFORE decouple
        // (the neutral over-bright board would otherwise skew the chroma statistic).
        bool[]? sprocketMask = null;
        if (cal.SprocketEnabled && cal.SprocketThreshold is double thr)
            sprocketMask = Sprocket.MakeMask(src.Data, src.PixelCount, (float)thr);

        // ── Input colour space: declared primaries → sRGB, on the NEGATIVE ────────
        // Before the inversion, because that is where t_base and the rest of Stage 1 are
        // calibrated; and before decouple, because decouple's matrix is solved in this space.
        if (InputTransform.ToWorking(cal.InputPrimaries, cal.InputWhitePoint) is double[,] inputM)
            InputTransform.Apply(src.Data, inputM);

        // ── Path A: RGB-light decoupling (linear domain, after vignette) ──────────
        if (cal.DecoupleMatrix != null)
            Decouple.Apply(src.Data, cal.DecoupleMatrix, cal.DecoupleMode);

        // ── Stage 1: density inversion (chroma_amp / chroma_matrix from decouple) ─
        //
        // Black-point correction — exact port of Python pipeline.py: map the film base
        // (D=0 → T_pos = 10^(pivot*(1-grade) - d_max)) to pure black so the sampled base
        // lands at 0. (result - floor)/(1 - floor) clipped at 0, no upper clip. The
        // denominator is (1 - floor), NOT (ceil - floor): with floor ≈ 1e-3 it is ≈ 1, so
        // grade stays a clean rotation about the pivot (mid-tone held) — matching the source.
        //
        // Handed to Invert rather than run as a second sweep: it is pointwise, so folding it
        // into the write that produces the value is free, whereas a standalone pass costs a
        // full read+write of the frame.
        double blackFloor = Math.Pow(10.0, cal.Pivot * (1.0 - cal.Grade) - cal.DMax);
        ImageBuffer result = Inversion.Invert(src, cal, cal.DecoupleChromaAmp, ResolveChromaMatrix(cal),
                                              applyBlackFloor ? blackFloor : null);

        // Apply sprocket mask after inversion + black floor: fill masked pixels white.
        if (sprocketMask != null)
            Sprocket.ApplyMask(result.Data, sprocketMask);

        // ── Geometry (export path): orientation → straighten → crop ───────────
        if (cal.QuarterTurns != 0 || cal.FlipH || cal.FlipV)
            result = Geometry.ApplyOrientation(result, cal.QuarterTurns, cal.FlipH, cal.FlipV);
        if (cal.Rotation != 0.0)
            result = Geometry.ApplyRotation(result, cal.Rotation);
        if (cal.CropRect != null)
            result = Geometry.ApplyCrop(result, cal.CropRect.Value);


        // ── Output intent gate ────────────────────────────────────────────────
        if (cal.OutputIntent == OutputIntent.None)
            return result;

        // ── Step 4 + Stage 2 (BASIC) ──────────────────────────────────────────────
        //    Stage2.ApplyChain performs the step-4 conversion (ACEScg → the roll's output
        //    space, primaries and gamma together) and then runs WB → exposure → levels →
        //    contrast → hi/sh → curves → saturation IN that space, as one fused pass.
        //    The result is display-encoded in cal.ResolvedOutputSpace — which is what both
        //    the preview and the exported file use, so the two agree by construction.
        Stage2.ApplyChain(result.Data, cal, cal.ResolvedOutputSpace, encodeExit: true);
        return result;
    }
}
