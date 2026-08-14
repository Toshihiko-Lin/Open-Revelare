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
    // ══ 反相的全部自由度：两端各三个绝对密度 ═══════════════════════════════════
    //
    // 渲染只消费 DensityEndpoints 的 scale[3] + offset[3] = 六个自由度。所以这里也只存六个数，
    // 一一对应，不多不少。历史上这里曾有 grade/pivot、wb_high、wb_offset、d_max、scan_ev 等
    // 十余个参数描述这同样的六个自由度——多出来的每一个都必然表现为「两个滑块做同一件事」，
    // 而且必然有一天会被同时写入、把同一个校正应用两遍。参数数量等于自由度数量是唯一的解。
    //
    // 两端都是**绝对密度**，量纲相同、可直接相减：
    //
    //   D_min[c]  该通道读作黑的密度      D_max[c]  该通道读作白的密度
    //
    // 由此导出的三件事，正好就是用户想调的三件事：
    //
    //   两端中点   -> 整体明暗（密度零点）  两端同向移动，反差与色偏都不变
    //   两端距离   -> 反差                  两端反向移动
    //   通道间差   -> 色偏                  展开三个分量各自调整
    //
    // 注意这三者**不需要额外参数**，它们就是这六个数的不同读法。任何试图为「亮度」「反差」
    // 「色温」单独立一个字段的做法，都是在重新制造上面那个十余参数的局面。

    /// <summary>
    /// 密度的参考透射率：<c>D = -log10(T / TBase)</c>。
    ///
    /// **恒为 1,1,1，不再承载片基。** 它曾经是片基透射率，于是片基信息藏在这里、而
    /// <see cref="DMinPerChannel"/> 恒为 0,0,0——两端说是同量纲，实际一个是绝对值一个恒为零，
    /// 而且界面上看不到黑端的任何客观数值（片基是自动测的，用户无从判断对错）。
    ///
    /// 固定成 1 之后参考点是「完全透光」，两端都成为对 T=1 的绝对密度：橙色片基读出
    /// ~0.09/0.29/0.54（R&lt;G&lt;B，一眼可验），高光读出 ~2.2/2.3/2.8，反差就是两者之差。
    /// 渲染逐位不变——把片基从除数移到减数是同一个仿射变换的两种写法。
    ///
    /// 保留字段而非删除：Path A 的解耦标定仍可能需要一个非中性的参考，且旧工程要能读回。
    /// </summary>
    public double[] TBase { get; set; } = { 1.0, 1.0, 1.0 };

    /// <summary>
    /// 输出范围：黑端落在 10^-OutputRange。
    ///
    /// **常量，不是参数。** 它曾经是可调的 d_max，于是与亮端端点争夺同一个自由度——两者都
    /// 同时改亮度和反差，用户看到两个滑块做同一件事。固定之后黑位恒定，明暗与反差改由两端
    /// 端点表达（见上），语义才各归其位。
    /// </summary>
    public const double OutputRange = 2.0;

    /// <summary>
    /// 亮端：每个通道读作白的密度（典型 1.8–2.4）。
    ///
    /// **高光白平衡就是这三个数。** 它们是绝对密度，不是相对某个基准的修正量，所以不需要
    /// 参考零点——三个通道之间的差即色偏。凡是自动解白平衡的（整卷标定、高光对齐、Deep-WB）
    /// 都写这里，因为反相只从这里读白端。
    /// </summary>
    public double[] DMaxPerChannel { get; set; } = { 2.0, 2.0, 2.0 };

    /// <summary>
    /// 暗端：每个通道读作黑的密度，<see cref="DMaxPerChannel"/> 的对称伙伴。
    ///
    /// **裸片基的实测密度**，对 T=1 而言。C-41 的橙色片基必然 R&lt;G&lt;B（红光透过最多），
    /// 典型 ~0.09 / 0.29 / 0.54——这三个数本身就能让人判断标定对不对，这正是它要显示绝对值
    /// 而不是恒为 0 的原因。三通道之差即暗部色偏。
    /// </summary>
    public double[] DMinPerChannel { get; set; } = { 0.0, 0.0, 0.0 };

    /// <summary>Per-channel chroma compression for the RGB-decouple path (× before chroma_grade).</summary>
    public double[] ChromaChannelScale { get; set; } = { 1.0, 1.0, 1.0 };

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

    /// <summary>
    /// The Cineon step-4 target: the space the positive is converted into, Stage 2 adjusts in, and
    /// the file is written in. By <see cref="ColorSpaceDef.Name"/>.
    ///
    /// A PER-ROLL RENDER PARAMETER, not a view setting and not an export setting. It has to be:
    /// Stage 2 runs inside this space, so it changes the rendered pixels, and the same adjustment
    /// numbers land differently in a narrower space. That is the intended behaviour — picking
    /// Kodak 2383 and then grading is how you grade FOR 2383 — but it means the choice belongs
    /// with the roll's other render parameters and must be saved alongside them.
    ///
    /// Stored as a string rather than an enum so a project written by a newer build naming a space
    /// this one lacks degrades to the default instead of failing to parse.
    /// </summary>
    public string OutputSpace { get; set; } = "sRGB";

    /// <summary>
    /// The resolved step-4 target; <see cref="ColorPipeline.DefaultOutput"/> when the stored name
    /// is unknown or names a scene-referred space.
    ///
    /// ACEScg is rejected here rather than trusted: it is a legitimate registered space and a
    /// legitimate WORKING space, but Stage 2's operations have no meaning in it (it is unbounded
    /// and scene-linear), so accepting it would silently produce a render whose contrast pivot
    /// sits nowhere near mid-grey.
    /// </summary>
    public ColorSpaceDef ResolvedOutputSpace
    {
        get
        {
            var s = ColorSpaces.ByName(OutputSpace, ColorPipeline.DefaultOutput);
            return OutputRender.IsDisplayReferred(s) ? s : ColorPipeline.DefaultOutput;
        }
    }

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
    ///
    /// NOTHING SETS THIS TODAY, and that is deliberate rather than an oversight. Two shortcuts
    /// look obvious and are both wrong:
    ///
    ///   • Defaulting it to sRGB would apply a real transform to data that is not in sRGB —
    ///     swapping one error for another and re-rendering every existing roll.
    ///   • Solving it from a chart the way docs/calibration/solve_input_primaries.py does returns
    ///     a blue primary sitting on the white point and a triangle spanning 7% of sRGB's area.
    ///     The optimiser used the primaries as free matrix coefficients to absorb the model's
    ///     residual; it measured nothing about any sensor.
    ///
    /// The real calibration needs a chart PHOTOGRAPHED ONTO the film, copied on the rig being
    /// calibrated, and solved JOINTLY with t_base and the endpoints — see THEORY.md, "Known
    /// limitation: the input primaries are never declared". Until that exists, leaving this null
    /// costs an off-diagonal residual on saturated colour (~0.53 on G→R); the diagonal part is
    /// absorbed by t_base and the endpoints, which is why the picture still looks right.
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
        DMaxPerChannel = (double[])DMaxPerChannel.Clone(),
        DMinPerChannel = (double[])DMinPerChannel.Clone(),
        ChromaChannelScale = (double[])ChromaChannelScale.Clone(),
        OutputIntent = OutputIntent,
        DisplayReferredStage2 = DisplayReferredStage2,
        OutputSpace = OutputSpace,
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
        Require3(DMaxPerChannel, nameof(DMaxPerChannel));
        Require3(DMinPerChannel, nameof(DMinPerChannel));
        Require3(ChromaChannelScale, nameof(ChromaChannelScale));

        foreach (var v in TBase)
            if (v <= 0) throw new ArgumentException($"TBase values must be positive, got [{string.Join(',', TBase)}]");
        // 白端必须严格高于黑端，否则该通道的跨度反号，斜率变负，画面正负颠倒。这是两端模型
        // 唯一的硬约束——两端各自的绝对值都可以是任意实数（暗端为负也合法：比裸片基还透光的
        // 区域确实存在），只有它们的**顺序**不能颠倒。
        for (int c = 0; c < 3; c++)
            if (DMaxPerChannel[c] <= DMinPerChannel[c])
                throw new ArgumentException(
                    $"DMax must exceed DMin per channel, got DMax=[{string.Join(',', DMaxPerChannel)}] " +
                    $"DMin=[{string.Join(',', DMinPerChannel)}]");
    }

    private static void Require3(double[] v, string name)
    {
        if (v is null || v.Length != 3)
            throw new ArgumentException($"{name} must have length 3");
    }
}
