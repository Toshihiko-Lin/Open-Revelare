using OpenRevelare.ColorManagement;

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
    /// Typed render boundary for new callers. M1 deliberately delegates the pixel math to the
    /// frozen v1 implementation below; it adds truthful identity and diagnostics without changing
    /// a single sample. M2 replaces the legacy print-LUT compatibility profile with a real CMM
    /// conversion and computes a versioned fingerprint.
    /// </summary>
    public static RenderedFrame Render(WorkingFrame source, FrameParams cal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cal);

        ImageBuffer pixels = ProcessFrame(source.Pixels, cal);
        return DescribeRenderedPixels(pixels, cal, ColorPipelineVersion.LegacyV1);
    }

    /// <summary>
    /// Attaches the canonical output semantics to pixels already produced by the named pipeline.
    /// Full-frame and regional renderers share this boundary so compatibility profiles, effective
    /// LUT identity, mismatch diagnostics and fingerprint availability cannot drift apart.
    /// </summary>
    internal static RenderedFrame DescribeRenderedPixels(
        ImageBuffer pixels,
        FrameParams cal,
        ColorPipelineVersion pipelineVersion)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(cal);

        return pipelineVersion switch
        {
            ColorPipelineVersion.LegacyV1 => DescribeLegacyPixels(pixels, cal),
            ColorPipelineVersion.ManagedV2 => DescribeManagedPixels(pixels, cal),
            _ => throw new ArgumentOutOfRangeException(
                nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version."),
        };
    }

    private static RenderedFrame DescribeLegacyPixels(ImageBuffer pixels, FrameParams cal)
    {
        ColorProfileRef requested;
        ColorProfileRef actual;
        ColorReference reference;
        TransferState transfer;
        NumericRange range;
        string gamutPolicy;
        bool mismatch;
        string effectivePrintLut = string.Empty;

        if (cal.OutputIntent == OutputIntent.None)
        {
            requested = actual = BuiltInColorProfiles.LinearAcesCg(ProfileRole.Output);
            reference = ColorReference.SceneReferred;
            transfer = TransferState.LinearInProfilePrimaries;
            range = NumericRange.Extended;
            gamutPolicy = "none (scene-linear ACEScg)";
            mismatch = false;
        }
        else
        {
            ColorSpaceDef selected = cal.ResolvedOutputSpace;
            requested = BuiltInColorProfiles.For(selected, ProfileRole.Output);
            bool hasPrintLut = PrintLuts.Resolve(cal.PrintLut) is not null;
            if (cal.DisplayReferredStage2 && hasPrintLut)
                effectivePrintLut = cal.PrintLut ?? string.Empty;
            actual = !cal.DisplayReferredStage2
                ? BuiltInColorProfiles.LegacyLinearStage2Output(selected)
                : hasPrintLut
                    ? BuiltInColorProfiles.LegacyPrintLutOutput(selected)
                    : requested;
            reference = ColorReference.DisplayReferred;
            transfer = TransferState.ProfileEncoded;
            range = NumericRange.Normalized;
            gamutPolicy = !cal.DisplayReferredStage2
                ? "legacy v1 ACEScg primaries / selected transfer; print LUT ignored"
                : hasPrintLut
                    ? "print-LUT v1 primaries-only exit"
                    : "legacy explicit gamut policy";
            mismatch = actual.Identity != requested.Identity;
        }

        var encoding = new CharacterizedPixelEncoding(
            actual,
            reference,
            transfer,
            range);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.LegacyV1,
            requested,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            gamutPolicy,
            effectivePrintLut,
            mismatch);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            new RenderFingerprint.Unavailable(
                FingerprintUnavailableReason.LegacyPipelineHasNoVersionedRecipe));
    }

    private static RenderedFrame DescribeManagedPixels(ImageBuffer pixels, FrameParams cal)
    {
        ColorProfileRef requested;
        CharacterizedPixelEncoding encoding;
        string gamutPolicy;
        string effectivePrintLut = string.Empty;

        if (cal.OutputIntent == OutputIntent.None)
        {
            requested = BuiltInColorProfiles.LinearAcesCg(ProfileRole.Output);
            encoding = new CharacterizedPixelEncoding(
                requested,
                ColorReference.SceneReferred,
                TransferState.LinearInProfilePrimaries,
                NumericRange.Extended);
            gamutPolicy = "none (scene-linear ACEScg)";
        }
        else
        {
            ColorSpaceDef selected = cal.ResolvedOutputSpace;
            requested = BuiltInColorProfiles.For(selected, ProfileRole.Output);
            bool hasPrintLut = PrintLuts.Resolve(cal.PrintLut) is not null;
            if (hasPrintLut)
            {
                // Generic external cubes remain uncharacterized and fail in the ManagedV2 step-4
                // boundary before a RenderedFrame exists. Enforce that invariant again here so a
                // future resolver change cannot put a machine-specific absolute path into a
                // portable render recipe/fingerprint by accident.
                if (!PrintLuts.IsBuiltin(cal.PrintLut))
                {
                    throw new InvalidOperationException(
                        "A successful ManagedV2 print-LUT render must carry a portable built-in " +
                        "identity, never a filesystem path.");
                }
                effectivePrintLut = cal.PrintLut!.ToLowerInvariant();
            }
            gamutPolicy = hasPrintLut
                ? "print-LUT native Rec709 -> exact output ICC (relative colorimetric, BPC off)"
                : "managed exact built-in output profile";
            encoding = new CharacterizedPixelEncoding(
                requested,
                ColorReference.DisplayReferred,
                TransferState.ProfileEncoded,
                NumericRange.Normalized);
        }

        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            requested,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            gamutPolicy,
            effectivePrintLut,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    /// <summary>
    /// Explicit versioned render boundary. LegacyV1 delegates to the frozen overload above and
    /// never touches the supplied CMM. ManagedV2 converts a print-film look from its declared
    /// native Rec709 encoding to the exact selected profile before Stage 2, so every adjustment
    /// operates in the encoding its controls declare. No process-wide CMM is consulted or created.
    /// </summary>
    public static RenderedFrame Render(
        WorkingFrame source,
        FrameParams cal,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
            return Render(source, cal);
        if (pipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version.");

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cal);
        ArgumentNullException.ThrowIfNull(colorManagement);

        ImageBuffer pixels;

        if (cal.OutputIntent == OutputIntent.None)
        {
            pixels = ProcessFrame(source.Pixels, cal);
        }
        else
        {
            ColorSpaceDef selected = cal.ResolvedOutputSpace;

            // Reuse every frozen operation through Stage 1 and geometry, stopping at the existing
            // OutputIntent.None gate. Managed step 4 then produces exact target-encoded pixels
            // before Stage 2. This is the intentional v2 migration of old implicit-false projects
            // whose legacy route either bypassed the cube or applied only a target TRC.
            FrameParams scene = cal.Clone();
            scene.OutputIntent = OutputIntent.None;
            pixels = ProcessFrame(source.Pixels, scene);
            ColorPipeline.ToOutputSpaceFor(
                pixels.Data,
                cal,
                ColorPipelineVersion.ManagedV2,
                colorManagement);
            Stage2.ApplyManagedAfterTargetEncoding(pixels.Data, cal, selected);
        }

        return DescribeRenderedPixels(pixels, cal, ColorPipelineVersion.ManagedV2);
    }

    /// <summary>
    /// Which chroma matrix the inversion should use.
    ///
    /// Path A wins when present: its matrix is solved for that roll's own narrow-band light
    /// source, so it describes a real measurement of THIS setup, where the C-41 matrix describes
    /// the process in general. They occupy the same slot in the inversion and must not stack.
    /// </summary>
    public static double[,]? ResolveChromaMatrix(FrameParams cal) =>
        cal.DecoupleChromaMatrix ?? (cal.UseC41Crosstalk ? C41Crosstalk.Direction : null);

    /// <summary>Run Stage 1 and, for BASIC intent, the sRGB exit TRC.</summary>
    public static ImageBuffer ProcessFrame(ImageBuffer img, FrameParams cal)
    {
        // ── Pre-inversion linear-domain corrections (distortion → vignette) ───────
        // Order mirrors pipeline.py: (lensfun) → distortion → (lcc) → vignette → (decouple).
        //
        // THE CALLER'S BUFFER IS NEVER WRITTEN. Every op here except distortion is in-place and
        // so needs a private copy; distortion is not, and that distinction is what decides
        // whether a copy is made at all.
        //
        // It used to clone unconditionally, up front, before asking which ops were active. When
        // distortion was one of them the clone was pure waste: ApplyDistortion resamples OUT OF
        // PLACE — it has to, since a distortion reads source pixels a corrected pixel has already
        // overwritten — so it allocates its own output and the freshly cloned input is read once
        // and dropped. On a 24 MP frame that is a 288 MB allocation plus a full memcpy, thrown
        // away microseconds later, and the export path pays it per frame.
        //
        // So: run distortion FIRST, off the caller's buffer, and let its output be the working
        // copy the in-place ops then need. Clone only when distortion is inactive and an
        // in-place op still has to write somewhere private.
        ImageBuffer src = img;

        if (cal.DistortionK1 != 0.0)
            src = LensCorrections.ApplyDistortion(src, cal.DistortionK1);

        // Every remaining pre-inversion op writes src.Data IN PLACE, so all of them have to be
        // counted here — including the input-primaries transform further below, which is applied
        // to the same buffer. It is null on every roll today (nothing sets it) but a project file
        // can carry one, and leaving it out of this set would let it write through to the
        // caller's buffer on the one configuration that reaches it.
        double[,]? inputMatrix = InputTransform.ToWorking(cal.InputPrimaries, cal.InputWhitePoint);
        bool inPlaceOps = cal.LccFlatField != null || cal.VignetteAmount != 0.0
                          || cal.DecoupleMatrix != null || inputMatrix != null;
        if (inPlaceOps && ReferenceEquals(src, img))
            src = new ImageBuffer(img.Width, img.Height, (float[])img.Data.Clone())
                      .InheritSourceFrom(img);

        if (inPlaceOps)
        {
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

        // ── Input colour space: declared primaries → WORKING (ACEScg), on the NEGATIVE ──
        // Before the inversion, because that is where t_base and the rest of Stage 1 are
        // calibrated; and before decouple, because decouple's matrix is solved in this space.
        //
        // Skipped entirely when InputPrimaries is null, which is every roll today — nothing
        // sets it. A profiled scanner TIFF is already carried into the working space by
        // IccRead.ReadMatrix at load; RAW and unprofiled TIFF are not, and are treated as
        // working-space data without being converted. That gap does not affect the density
        // inversion (which is self-referential through t_base) but it does affect step 4.
        if (inputMatrix != null)
            InputTransform.Apply(src.Data, inputMatrix);

        // ── Path A: RGB-light decoupling (linear domain, after vignette) ──────────
        if (cal.DecoupleMatrix != null)
            Decouple.Apply(src.Data, cal.DecoupleMatrix, cal.DecoupleMode);

        // ── Stage 1: density inversion (chroma_amp / chroma_matrix from decouple) ─
        //
        // NO BLACK-POINT NORMALISATION. Stage 1 used to end by mapping the sampled film base to
        // linear zero — (v - floor)/(1 - floor) — so that the base came out pure black. That was
        // Stage 1 deciding, on the display rendering's behalf, that a calibrated film base is
        // black. In the Cineon workflow it is not: the base lands on code 95, which reads as a
        // GREY, and it only becomes black when a display transform takes it there. Normalising
        // here forced the two exits to disagree — pass-through rendered the base at 0 while a
        // print-film cube rendered code 95 as its own toe — and the only lever a user had to
        // reconcile them was D_min, which is calibration, not rendering.
        //
        // So Stage 1 now stops at 10^D_adj and both exits go through the same Cineon encoding.
        ImageBuffer result = Inversion.Invert(src, cal, cal.DecoupleChromaAmp, ResolveChromaMatrix(cal));

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
