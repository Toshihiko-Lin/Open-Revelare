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

    /// <summary>
    /// Density-domain chroma scale. Weakly founded and slated for replacement by colour-space
    /// rendering; the default is held at the historical value so existing projects render
    /// unchanged. See docs/CALIBRATION.md.
    /// </summary>
    public double ChromaGrade { get; set; } = 3.05;

    /// <summary>Density-domain contrast = digital "paper grade".</summary>
    public double Grade { get; set; } = 1.65;

    /// <summary>Mid-tone anchor that grade rotates about.</summary>
    public double Pivot { get; set; } = 0.9;

    /// <summary>"none" (linear) | "basic" (sRGB gamma).</summary>
    public OutputIntent OutputIntent { get; set; } = OutputIntent.Basic;

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
    /// Input characterisation: linear camera-native RGB → linear sRGB, row-major 3×3. Null =
    /// uncharacterised, the historical behaviour, where the decoded data was simply treated as
    /// though it were already sRGB.
    ///
    /// Read from the camera's own colorimetry at import (<see cref="RawDecode.CameraToSrgbMatrix"/>)
    /// and applied to the POSITIVE after inversion, not to the negative before it: the density
    /// maths — t_base normalisation, wb_high/wb_offset, d_max — is calibrated against the sensor's
    /// own numbers, and moving it into another space beforehand would invalidate every one of
    /// those measurements. Rows sum to 1, so neutrals are untouched and the white the inversion
    /// established survives the conversion.
    ///
    /// This is what makes the pipeline's colour input KNOWN. Measured on an E-M5 III, the matrix
    /// expands chroma by about 1.32× and does so per hue (1.14–1.53 across the probes) — the same
    /// job chroma_grade was doing with one isotropic scalar. See docs/CALIBRATION.md.
    /// </summary>
    public double[,]? InputToSrgbMatrix { get; set; }

    /// <summary>Path-A decouple matrix applied to the linear RAW before inversion (row-major
    /// 3×3, t_dec = t·Mᵀ); null = white-light passthrough. From import-time calibration.</summary>
    public double[,]? DecoupleMatrix { get; set; }
    /// <summary>Domain for <see cref="DecoupleMatrix"/> (default linear).</summary>
    public DecoupleMode DecoupleMode { get; set; } = DecoupleMode.Linear;
    /// <summary>Axis-accurate 3×3 chroma-compensation matrix fed into inversion; null = off.</summary>
    public double[,]? DecoupleChromaMatrix { get; set; }
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
        ChromaGrade = ChromaGrade,
        Grade = Grade,
        Pivot = Pivot,
        OutputIntent = OutputIntent,
        DistortionK1 = DistortionK1,
        VignetteAmount = VignetteAmount,
        VignetteFalloff = VignetteFalloff,
        LccFlatField = LccFlatField,
        InputToSrgbMatrix = InputToSrgbMatrix,
        DecoupleMatrix = DecoupleMatrix,
        DecoupleMode = DecoupleMode,
        DecoupleChromaMatrix = DecoupleChromaMatrix,
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
