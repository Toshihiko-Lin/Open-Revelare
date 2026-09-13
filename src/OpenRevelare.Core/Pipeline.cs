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
            OutputTarget target = cal.ResolvedOutputTarget;
            if (target.IsExtended)
            {
                // The pixels are LINEAR in the canonical carrier's primaries, so they must be
                // tagged with the carrier's profile and not with the roll's display-referred
                // output selection — an sRGB or Adobe RGB profile asserts a TRC these numbers do
                // not carry, which is exactly the "relabel without converting" failure D-003
                // exists to prevent. The encoding model already had every term for this; it is
                // how OutputIntent.None describes scene-linear ACEScg.
                //
                // The print LUT is not part of this rendering (D-021), so the recipe must not
                // claim one: the effective identity stays empty whatever the roll selected.
                requested = BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output);
                encoding = new CharacterizedPixelEncoding(
                    requested,
                    ColorReference.SceneReferred,
                    TransferState.LinearInProfilePrimaries,
                    NumericRange.Extended);
                gamutPolicy =
                    $"scene-referred extended, unclamped primaries, shoulder to {target.HighlightHeadroom:0.###}× diffuse white";
                effectivePrintLut = string.Empty;
            }
            else
            {
                gamutPolicy = hasPrintLut
                    ? "print-LUT native Rec709 -> exact output ICC (relative colorimetric, BPC off)"
                    : "managed exact built-in output profile";
                encoding = new CharacterizedPixelEncoding(
                    requested,
                    ColorReference.DisplayReferred,
                    TransferState.ProfileEncoded,
                    NumericRange.Normalized);
            }
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
            () => RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
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
            OutputTarget target = cal.ResolvedOutputTarget;

            // Reuse every frozen operation through Stage 1 and geometry, stopping at the existing
            // OutputIntent.None gate. Managed step 4 then produces exact target-encoded pixels
            // before Stage 2. This is the intentional v2 migration of old implicit-false projects
            // whose legacy route either bypassed the cube or applied only a target TRC.
            FrameParams scene = cal.Clone();
            scene.OutputIntent = OutputIntent.None;
            // An extended target needs to know which output pixels are FILL — the sprocket mask
            // and the rotation's corners — because fill is not picture and must not be rendered
            // as one (see below). The SDR path never asks, so it stays bit-identical.
            pixels = ProcessFrame(source.Pixels, scene, target.IsExtended, out bool[]? fill);
            ColorPipeline.ToOutputTargetFor(
                pixels.Data,
                cal,
                target,
                ColorPipelineVersion.ManagedV2,
                colorManagement);

            if (target.IsExtended)
            {
                // The shoulder itself ran inside step 4, exactly where the SDR rendering runs
                // its own — they are one family of curves and differ only in where they aim
                // (D-021). Stage 2 then runs against the roll's own range (D-032), and what is
                // left is the same guard the display-referred path ends with: exposure is
                // multiplicative and levels/contrast/curves are open-ended above, so they can push
                // a rendered value back past the ceiling the shoulder established.
                Stage2.ApplyManagedToExtendedTarget(pixels.Data, cal, target.HighlightHeadroom);
                HighlightRolloff.BoundAbove(pixels.Data, target.HighlightHeadroom);

                // FILL IS PAPER WHITE, NOT A HIGHLIGHT. The mask and the rotation corners are
                // written as linear 1.0 before the log encode, which is the TOP of the Cineon
                // domain (code 1032): the SDR shoulder squeezes that to paper white, which is what
                // the fill has always meant, but the extended shoulder aims at the headroom and
                // would carry the same value to the peak — sprocket holes blazing at the roll's
                // HDR limit, and a gain map that lights them up on every HDR display. Nothing was
                // measured there, so there is nothing for the headroom to show; the fill is pinned
                // to the carrier's diffuse white, where it reads exactly as it does in SDR and the
                // gain map stays empty. It runs after the guard because exposure is multiplicative
                // and the fill is not something exposure applies to either.
                if (fill is not null)
                    Sprocket.ApplyMask(pixels.Data, fill);
            }
            else
            {
                Stage2.ApplyManagedAfterTargetEncoding(pixels.Data, cal, target.Space);
            }
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
        => ProcessFrame(img, cal, trackFill: false, out _);

    /// <summary>
    /// <see cref="ProcessFrame(ImageBuffer, FrameParams)"/>, optionally reporting which OUTPUT
    /// pixels are fill rather than picture: the sprocket/light-board mask and the corners the
    /// straighten rotation uncovers, both carried through the same orientation → rotation → crop
    /// the pixels go through. Null when not tracked or when the frame has neither.
    ///
    /// A pixel the rotation's bilinear blends with fill counts as fill: in the linear domain the
    /// fill is far above every picture value, so any share of it dominates the blend.
    /// </summary>
    private static ImageBuffer ProcessFrame(ImageBuffer img, FrameParams cal, bool trackFill, out bool[]? fill)
    {
        fill = null;
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

        if (trackFill && (sprocketMask != null || cal.Rotation != 0.0))
            fill = MapFillThroughGeometry(sprocketMask, src.Width, src.Height, cal);


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

    /// <summary>
    /// The fill mask in OUTPUT coordinates. The mask is known on the source grid; the picture
    /// then goes through the geometry ops, so the mask goes through the very same ops — as a
    /// 1.0/0.0 image, with the rotation's own fill so its corners are counted — rather than
    /// through a second, hand-kept copy of their arithmetic. Any non-zero share of fill after the
    /// rotation's bilinear marks the pixel; see the tracking overload of <c>ProcessFrame</c>.
    /// </summary>
    private static bool[] MapFillThroughGeometry(bool[]? sprocketMask, int width, int height, FrameParams cal)
    {
        var mask = new ImageBuffer(width, height);
        if (sprocketMask != null)
            Sprocket.ApplyMask(mask.Data, sprocketMask);

        if (cal.QuarterTurns != 0 || cal.FlipH || cal.FlipV)
            mask = Geometry.ApplyOrientation(mask, cal.QuarterTurns, cal.FlipH, cal.FlipV);
        if (cal.Rotation != 0.0)
            mask = Geometry.ApplyRotation(mask, cal.Rotation, fill: 1.0f);
        if (cal.CropRect != null)
            mask = Geometry.ApplyCrop(mask, cal.CropRect.Value);

        var fill = new bool[mask.PixelCount];
        float[] data = mask.Data;
        ParallelSweep.Over(fill.Length, (from, to) =>
        {
            for (int p = from; p < to; p++)
                fill[p] = data[p * 3] > 0.0f;
        });
        return fill;
    }
}
