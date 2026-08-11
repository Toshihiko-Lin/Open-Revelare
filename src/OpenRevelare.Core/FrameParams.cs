namespace OpenRevelare.Core;

/// <summary>
/// Per-frame parameters. Port of the Stage-1 (FilmBase) fields of Python's
/// <c>FrameParams</c> dataclass (negative/types.py). Stage-2 (SceneBase) fields
/// and geometry arrive in phase 3; only what the density inversion reads lives
/// here for now.
///
/// Defaults match the Python dataclass exactly, so a frame processed by either
/// implementation with default calibration yields identical output.
/// </summary>
public sealed class FrameParams
{
    /// <summary>Per-channel film-base transmittance (removes D_min + shadow WB). Must be &gt; 0.</summary>
    public double[] TBase { get; set; } = { 0.82, 0.51, 0.29 };

    /// <summary>Scalar physical max density of the film.</summary>
    public double DMax { get; set; } = 2.0;

    /// <summary>Per-channel MULTIPLICATIVE highlight-end density WB (Negadoctor wb_high).</summary>
    public double[] WbHigh { get; set; } = { 1.0, 1.0, 1.0 };

    /// <summary>Per-channel ADDITIVE shadow-end density offset (Negadoctor offset/wb_low).</summary>
    public double[] WbOffset { get; set; } = { 0.0, 0.0, 0.0 };

    /// <summary>Per-channel chroma compression for the RGB-decouple path (× before chroma_grade).</summary>
    public double[] ChromaChannelScale { get; set; } = { 1.0, 1.0, 1.0 };

    /// <summary>Density-domain zero-point correction: D_corr = D + ev * log10(2).</summary>
    public double ScanExposureEv { get; set; } = 0.0;

    /// <summary>Density-domain contrast = digital "paper grade".</summary>
    public double Grade { get; set; } = 1.65;

    /// <summary>Mid-tone anchor that grade rotates about.</summary>
    public double Pivot { get; set; } = 0.9;

    /// <summary>"none" (linear) | "basic" (sRGB gamma).</summary>
    public OutputIntent OutputIntent { get; set; } = OutputIntent.Basic;

    /// <summary>
    /// Which Stage-2 semantics this roll uses. Default <c>true</c> for new rolls; projects saved
    /// before the rework load as <c>false</c> and keep rendering exactly as they did.
    ///
    /// The rework splits Stage 2 by what each operation physically IS. White balance and exposure
    /// scale light, so they are only correct in linear; everything after them — levels, contrast,
    /// highlights/shadows, curves, saturation — is perceptual and is only meaningful once the
    /// data is display-encoded. The old chain ran all seven in linear light and had each
    /// perceptual op improvise its own encoding, which is why contrast pivoted on 0.5 while
    /// linear 0.5 is 73.5% display brightness, and why the curve step encoded and decoded a
    /// private gamma 2.2 that the sRGB exit then applied a second time.
    ///
    /// It is a version flag rather than a preference because the slider VALUES change meaning:
    /// the same contrast number produces a different picture under each. Nothing about an
    /// existing project can be reinterpreted safely, so it is pinned per roll.
    /// </summary>
    public bool DisplayReferredStage2 { get; set; } = true;

    // ── Pre-inversion linear-domain corrections (before density inversion) ─────
    /// <summary>Manual radial distortion coefficient. k1&lt;0 barrel, k1&gt;0 pincushion; 0 = off.</summary>
    public double DistortionK1 { get; set; } = 0.0;
    /// <summary>Manual radial vignette corner gain strength. 0 = pass-through.</summary>
    public double VignetteAmount { get; set; } = 0.0;
    /// <summary>Vignette radial falloff exponent (larger = corners only).</summary>
    public double VignetteFalloff { get; set; } = 2.5;
    /// <summary>Mean-normalised LCC flat field (from <see cref="Lcc.LoadFlatField"/>); null = off.
    /// Applied after distortion, before vignette. Resized to the frame if dimensions differ.</summary>
    public ImageBuffer? LccFlatField { get; set; }
    /// <summary>
    /// The colour space the decoded negative is treated as being in — CIE xy for the R, G and B
    /// primaries, in that order. Null = sRGB's primaries, which is what the pipeline has always
    /// assumed implicitly.
    ///
    /// WHY THIS EXISTS. Nothing ever declared what space the decoded negative occupied. Nothing
    /// converted it either, so the density inversion silently treated the sensor's own primaries
    /// as sRGB's — an assumption, never a measurement. Measured against DiVERE's Kodak Gold 200
    /// dataset, letting these primaries move instead of pinning them to sRGB drops the fit error
    /// by 28% (docs/calibration/solve_input_primaries.py). The fixed assumption is a real,
    /// quantifiable error, and it sits at the INPUT — which is where it has to be fixed, not in a
    /// downstream scalar like chroma_grade.
    ///
    /// WHAT IT IS NOT: the camera manufacturer's ColorMatrix. That describes how the sensor sees
    /// a real SCENE, and a negative holds no scene — it holds dye densities. Three separate
    /// attempts to push the camera matrix through this pipeline all failed on real film for that
    /// reason. What belongs here is the EQUIVALENT primaries of the
    /// whole chain, sensor spectral response composed with the film's dye transmission, and that
    /// can only be solved from a chart — which is exactly what DiVERE's primaries_xy is.
    ///
    /// COUPLED TO t_base. The base is sampled in whatever space this declares, so changing one
    /// invalidates the other. Both are roll-level calibration and must be re-established together.
    /// </summary>
    public double[,]? InputPrimaries { get; set; }

    /// <summary>White point of <see cref="InputPrimaries"/> as CIE xy; null = D65.</summary>
    public double[]? InputWhitePoint { get; set; }

    /// <summary>Path-A decouple matrix applied to the linear RAW before inversion (row-major
    /// 3×3, t_dec = t·Mᵀ); null = white-light passthrough. From import-time calibration.</summary>
    public double[,]? DecoupleMatrix { get; set; }
    /// <summary>Domain for <see cref="DecoupleMatrix"/> (default linear).</summary>
    public DecoupleMode DecoupleMode { get; set; } = DecoupleMode.Linear;
    /// <summary>Axis-accurate 3×3 chroma-compensation matrix fed into inversion; null = off.</summary>
    public double[,]? DecoupleChromaMatrix { get; set; }

    /// <summary>
    /// Use the C-41 process crosstalk matrix (<see cref="C41Crosstalk.Direction"/>) in place of
    /// chroma_grade's isotropic scalar. Off by default, so existing projects render unchanged.
    ///
    /// This is the structurally correct form of what chroma_grade approximates. The chroma C-41
    /// loses is an inter-channel effect, so no per-channel operation reaches it and no scalar
    /// describes it; measured across eight modelled stocks the relationship is one shared matrix
    /// direction with a per-stock strength (cosine similarity 0.9957–0.9997, and giving each
    /// stock its own matrix improves the fit by 0.1%). See docs/calibration/universal_crosstalk.py.
    ///
    /// When on, <see cref="Grade"/> scales the matrix — the direction is universal, the amount
    /// follows the single Cineon gamma. Ignored on Path A rolls, whose
    /// <see cref="DecoupleChromaMatrix"/> already occupies the same slot in the inversion and is
    /// solved for that roll's own light source.
    /// </summary>
    public bool UseC41Crosstalk { get; set; }
    /// <summary>Per-channel chroma amplification fed into inversion (chroma_grade ÷ amp); null = 1.
    /// IGNORED when <see cref="DecoupleChromaMatrix"/> is set — the two are alternatives, and the
    /// matrix already carries the amplification per chroma axis. Only callers without a matrix
    /// (the CLI's --decouple-chroma-amp) need this; the GUI sets the matrix instead.</summary>
    public double[]? DecoupleChromaAmp { get; set; }
    /// <summary>Enable sprocket/light-board masking (fill masked pixels white after inversion).</summary>
    public bool SprocketEnabled { get; set; } = false;
    /// <summary>Absolute luma cut for the sprocket mask; null = disabled.</summary>
    public double? SprocketThreshold { get; set; } = 0.9;

    // ── SceneBase adjustments (Stage 2, active only when intent == BASIC) ──────
    /// <summary>Per-channel white-balance gains (linear). Default 1 = pass-through.</summary>
    public double[] WbGains { get; set; } = { 1.0, 1.0, 1.0 };
    /// <summary>Exposure in stops (linear ×2^ev). 0 = pass-through.</summary>
    public double ExposureEv { get; set; } = 0.0;
    /// <summary>Levels black point (remap [black,white]→[0,1]). 0 = default.</summary>
    public double BlackPoint { get; set; } = 0.0;
    /// <summary>Levels white point. 1 = default.</summary>
    public double WhitePoint { get; set; } = 1.0;
    /// <summary>Contrast about mid-grey (gain 2^contrast). 0 = pass-through.</summary>
    public double Contrast { get; set; } = 0.0;
    /// <summary>Highlights lift/crush [-1,1]. 0 = pass-through.</summary>
    public double Highlights { get; set; } = 0.0;
    /// <summary>Shadows lift/crush [-1,1]. 0 = pass-through.</summary>
    public double Shadows { get; set; } = 0.0;
    /// <summary>Dedicated chroma scale (factor 1+sat). 0 = pass-through.</summary>
    public double Saturation { get; set; } = 0.0;

    // Per-channel tone curves in gamma-2.2 domain: control points (x,y) in [0,1].
    // Empty = identity. Master (M) applies first, then R/G/B.
    public List<(double X, double Y)> CurvePointsM { get; set; } = new();
    public List<(double X, double Y)> CurvePointsR { get; set; } = new();
    public List<(double X, double Y)> CurvePointsG { get; set; } = new();
    public List<(double X, double Y)> CurvePointsB { get; set; } = new();
    /// <summary>Master curve: true = hue-preserving luminance map; false = per-channel RGB.</summary>
    public bool CurvePreserveHue { get; set; } = true;

    // ── Geometry (export path: orientation → rotation → crop) ──────────────────
    /// <summary>Normalised crop rect (x,y,w,h) in [0,1]; null = no crop.</summary>
    public (double X, double Y, double W, double H)? CropRect { get; set; }
    /// <summary>Straighten rotation in degrees (clockwise). 0 = none.</summary>
    public double Rotation { get; set; } = 0.0;
    /// <summary>Discrete 90° clockwise turns (0–3), applied before straighten + crop.</summary>
    public int QuarterTurns { get; set; } = 0;
    /// <summary>Mirror left↔right (after the 90° turns).</summary>
    public bool FlipH { get; set; } = false;
    /// <summary>Mirror top↔bottom.</summary>
    public bool FlipV { get; set; } = false;

    /// <summary>Deep copy for undo snapshots. Arrays/lists are cloned; large immutable
    /// roll-level references (LCC field, decouple matrices) are shared by reference.</summary>
    public FrameParams Clone() => new()
    {
        TBase = (double[])TBase.Clone(),
        DMax = DMax,
        WbHigh = (double[])WbHigh.Clone(),
        WbOffset = (double[])WbOffset.Clone(),
        ChromaChannelScale = (double[])ChromaChannelScale.Clone(),
        ScanExposureEv = ScanExposureEv,
        Grade = Grade,
        Pivot = Pivot,
        OutputIntent = OutputIntent,
        DisplayReferredStage2 = DisplayReferredStage2,
        DistortionK1 = DistortionK1,
        VignetteAmount = VignetteAmount,
        VignetteFalloff = VignetteFalloff,
        LccFlatField = LccFlatField,
        InputPrimaries = InputPrimaries,
        InputWhitePoint = InputWhitePoint,
        DecoupleMatrix = DecoupleMatrix,
        DecoupleMode = DecoupleMode,
        DecoupleChromaMatrix = DecoupleChromaMatrix,
        UseC41Crosstalk = UseC41Crosstalk,
        DecoupleChromaAmp = DecoupleChromaAmp,
        SprocketEnabled = SprocketEnabled,
        SprocketThreshold = SprocketThreshold,
        WbGains = (double[])WbGains.Clone(),
        ExposureEv = ExposureEv,
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        Saturation = Saturation,
        CurvePointsM = new List<(double, double)>(CurvePointsM),
        CurvePointsR = new List<(double, double)>(CurvePointsR),
        CurvePointsG = new List<(double, double)>(CurvePointsG),
        CurvePointsB = new List<(double, double)>(CurvePointsB),
        CurvePreserveHue = CurvePreserveHue,
        CropRect = CropRect,
        Rotation = Rotation,
        QuarterTurns = QuarterTurns,
        FlipH = FlipH,
        FlipV = FlipV,
    };

    /// <summary>Validate the invariants Python enforces in <c>__post_init__</c>.</summary>
    public void Validate()
    {
        Require3(TBase, nameof(TBase));
        Require3(WbHigh, nameof(WbHigh));
        Require3(WbOffset, nameof(WbOffset));
        Require3(ChromaChannelScale, nameof(ChromaChannelScale));

        foreach (var v in TBase)
            if (v <= 0) throw new ArgumentException($"TBase values must be positive, got [{string.Join(',', TBase)}]");
        foreach (var v in WbHigh)
            if (v <= 0) throw new ArgumentException($"WbHigh values must be positive, got [{string.Join(',', WbHigh)}]");
    }

    private static void Require3(double[] v, string name)
    {
        if (v is null || v.Length != 3)
            throw new ArgumentException($"{name} must have length 3");
    }
}
