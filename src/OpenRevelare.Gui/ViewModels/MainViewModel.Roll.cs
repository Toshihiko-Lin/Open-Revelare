using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> —— 卷的结构：增删帧、虚拟副本。
/// </summary>
public partial class MainViewModel
{
    // ── Roll structure: add images / virtual copies / remove frame ──────────────
    // Structural edits change Frames.Count, which the index-keyed undo snapshots can't
    // track — so each one re-baselines undo (history is dropped, current state kept).
    private void ResetUndoAfterStructural()
    {
        _undo.Clear(); _redo.Clear(); _committed = null;
        SetUndoBaseline();
        UpdateUndoState();
        MarkRollDirty();   // frames added / copied / removed — the roll's shape changed
    }

    /// <summary>True when the roll's source files are RAW (else TIFF) — decided by the first frame.</summary>
    private bool RollIsRaw => Frames.Count > 0 && RawDecode.IsRawExtension(Frames[0].Path);

    /// <summary>
    /// Step-4 targets, in the order the picker lists them.
    ///
    /// Every one is display-referred; ACEScg is absent on purpose — it is the WORKING space, and
    /// Stage 2's operations have no meaning in a scene-linear unbounded space. All of these are
    /// now genuinely different renders rather than simulations, including the two wider-than-sRGB
    /// ones: the working space is ACEScg, so there is real saturation for Adobe RGB and P3 to
    /// hold that sRGB cannot.
    /// </summary>
    /// Three spaces are registered in <see cref="ColorSpaces"/> but deliberately NOT offered here.
    ///
    /// Rec709 shares sRGB's primaries exactly and differs only in transfer function, so listing
    /// both would be two entries with the same gamut and a shadow-only difference between them —
    /// a choice without a decision behind it.
    ///
    /// The two Kodak spaces (Endura Premier paper, 2383 print film) are out because what they
    /// deliver does not match what their names promise. Their primaries measure 127% and 141% of
    /// sRGB's area — WIDER than Adobe RGB — whereas real photographic paper reproduces a gamut
    /// NARROWER than sRGB. They describe the dye set's encoding primaries, not the medium's
    /// reproducible gamut, so selecting them performs a gamut expansion plus a D65→D60 white
    /// shift rather than reproducing a darkroom print or a projection print. That look lives in
    /// density curves and a 3D LUT; three chromaticity coordinates cannot carry it.
    private static readonly ColorSpaceDef[] OutputSpaces =
    {
        ColorSpaces.Srgb,
        ColorSpaces.DisplayP3,
        ColorSpaces.AdobeRgb,
    };
    // The display-space picker is gone: the preview is always handed to the compositor unmanaged,
    // which is what the app did before any of this and what it does again.
    //
    // The honest reason is that the app cannot do the job properly. Doing it the way Photoshop and
    // Lightroom do means the OS's registered display profile — a calibrator's measurement, with
    // per-channel TRC curves and the panel's real primaries. A three-entry dropdown of standard
    // spaces is not that; it is a guess, and a wrong guess actively misleads, because the user
    // then grades against colours the panel is not showing. EDID cannot fill the gap either: it is
    // factory boilerplate, and this very laptop reports a panel covering 63.5% of sRGB, which
    // would desaturate everything if applied.
    //
    // Unmanaged is at least a KNOWN state: the numbers reach the panel untouched, and anyone who
    // needs accuracy calibrates their display and trusts the OS to do the conversion.

    private int _outputSpaceIndex;

    /// <summary>
    /// The roll's step-4 target: what the positive is converted into, what Stage 2 adjusts in, and
    /// what the exported file is written in. One control for all three, which is what makes the
    /// preview WYSIWYG.
    ///
    /// This is NOT a view setting. It changes the rendered
    /// pixels, so it is saved with the roll and marks the project dirty. Changing it keeps the
    /// Stage-2 slider VALUES and lets the picture move — those numbers mean "this much adjustment
    /// in the current output space", so re-interpreting them in a new space is the honest
    /// behaviour and the reason grading for 2383 works at all.
    /// </summary>
    public int OutputSpaceIndex
    {
        get => _outputSpaceIndex;
        set
        {
            int v = Math.Clamp(value, 0, OutputSpaces.Length - 1);
            if (_outputSpaceIndex == v) return;
            _outputSpaceIndex = v;
            OnPropertyChanged(nameof(OutputSpaceIndex));
            OnPropertyChanged(nameof(OutputSpaceHint));
            OnPropertyChanged(nameof(CurrentOutputSpace));

            // Roll-uniform: a strip whose frames sat in different output spaces would be a contact
            // sheet of incomparable renders.
            foreach (RollFrame f in Frames) f.Params.OutputSpace = OutputSpaces[v].Name;
            if (Frames.Count > 0) MarkRollDirty();

            // Thumbnails are rebuilt too: unlike the old soft proof, this changes what each frame
            // IS, so a strip still showing the previous space would be showing the wrong picture.
            foreach (RollFrame f in Frames) SetThumbnail(f, null);
            RestartThumbnails();
            ScheduleRender();
        }
    }

    /// <summary>The roll's output space — what an export will be written in.</summary>
    public ColorSpaceDef CurrentOutputSpace => OutputSpaces[_outputSpaceIndex];

    // ══ 黑白负片 ═══════════════════════════════════════════════════════════════
    //
    // 与【输出空间】一样是卷级的：一卷要么整卷是黑白片，要么整卷不是。打开后三通道在进密度域
    // 之前折成一路亮度信号，反相只用一对端点，输出按构造中性——见 Core 的 Monochrome。
    // 界面上随之收起所有对黑白片没有意义的控件（白平衡、色偏修正、逐通道端点、R/G/B 曲线）：
    // 留着它们既无处可用，又会让人以为这张片子有色彩可调。
    private bool _monochrome;

    /// <summary>This roll is black-and-white negative film.</summary>
    public bool Monochrome
    {
        get => _monochrome;
        set
        {
            if (_monochrome == value) return;
            _monochrome = value;
            OnPropertyChanged(nameof(Monochrome));
            OnPropertyChanged(nameof(IsColourRoll));
            OnPropertyChanged(nameof(MonochromeHint));
            OnPropertyChanged(nameof(CastCardHeader));
            OnPropertyChanged(nameof(CastCardHint));
            OnPropertyChanged(nameof(GreyCardSpan));

            foreach (RollFrame f in Frames) f.Params.Monochrome = value;
            if (Frames.Count > 0) MarkRollDirty();

            // Every thumbnail is now a different picture, exactly as an output-space change makes
            // them: a strip still showing the colour render of a black-and-white roll would be
            // showing something the export will not produce.
            foreach (RollFrame f in Frames) SetThumbnail(f, null);
            RestartThumbnails();
            ScheduleRender();
        }
    }

    /// <summary>Adopt a loaded roll's mode without treating it as a change the user made: no
    /// dirty mark, no thumbnail rebuild, no render — the caller is already doing all three.</summary>
    private void SyncMonochrome(bool value)
    {
        if (_monochrome == value) return;
        _monochrome = value;
        OnPropertyChanged(nameof(Monochrome));
        OnPropertyChanged(nameof(IsColourRoll));
        OnPropertyChanged(nameof(MonochromeHint));
        OnPropertyChanged(nameof(CastCardHeader));
        OnPropertyChanged(nameof(CastCardHint));
        OnPropertyChanged(nameof(GreyCardSpan));
    }

    /// <summary>The inverse, for binding visibility of the colour-only controls.</summary>
    public bool IsColourRoll => !_monochrome;

    // The 色偏修正 card holds two tools that are NOT the same kind of thing, and hiding the card
    // wholesale on a black-and-white roll took the wrong one away. Deep-WB infers a COLOUR cast and
    // has nothing to infer here. The grey card is an EXPOSURE anchor — it puts a measured neutral on
    // Cineon's standard grey — and a black-and-white roll needs that more than a colour one does,
    // because it has no colour left to judge exposure by. So the card stays and only Deep-WB goes.

    /// <summary>What the card is FOR, which is not the same thing on the two kinds of film.</summary>
    public string CastCardHeader => _monochrome ? Loc.T("灰卡锚点") : Loc.T("色偏修正");

    public string CastCardHint => _monochrome
        ? Loc.T("把画面里拍到的灰卡定为 Cineon 标准灰（码值 470），也就是确定这一卷的曝光位置。黑白片没有色偏可解，但灰卡仍然是唯一客观的曝光参照。")
        : Loc.T("解这一卷的色偏——胶片、扫描与片基留在两端上的偏差。灰卡是测量，智能色偏修正是推测。结果写入 D_max 的三个密度，与高光采样互为替代，后执行者生效。");

    /// <summary>The grey-card button takes the whole row once Deep-WB is gone from beside it.</summary>
    public int GreyCardSpan => _monochrome ? 2 : 1;

    public string MonochromeHint => _monochrome
        ? Loc.T("三通道折成一路亮度后反相，只用一对端点；白平衡与逐通道端点对银影没有意义，已收起。印片风格仍会叠加它自己的色偏。")
        : Loc.T("彩色负片：橙色片基与三层染料，六个自由度各自独立。");

    // ══ HDR ═══════════════════════════════════════════════════════════════════
    //
    // 与【输出空间】并列而不并入其中，因为它们回答的是不同的问题：输出空间决定画面装进哪个
    // 容器，HDR 决定 diffuse white 之上还留不留东西。关 = SDR，也就是既有的印相渲染。
    //
    // 两个控件，两层意思（D-028 的分界）：底栏一个开关，直方图下一个上限滑块。
    // 上限的单位是 SDR 白之上的**档数**，与 Lightroom 的 HDR Limit 同义、同刻度（0.5 … +4）；
    // 存进工程时换算成 nits（peak = 203 × 2^stops），HdrPeakNits 的持久化形式不变。它是母版
    // 参数——导出文件里高光保留到哪——**不是**预览：预览在本机余量内自动软校样（D-028）。
    // 以前是四个 nits 预设（400/600/1000/4000，供参考：+1.0/+1.6/+2.3/+4.3 档），照片交付没有
    // 母版监视器那几台设备可挂靠，连续值才是同行（LR、gain map、ACES 2.0）的做法。

    /// <summary>The slider's range in stops above SDR white; +4 is Lightroom's ceiling and the histogram's axis.</summary>
    public const double HdrLimitMinStops = 0.5;
    public const double HdrLimitMaxStops = 4.0;
    private const double HdrLimitStep = 0.1;

    private bool _hdrEnabled;
    private double _hdrLimitStops = 2.3;   // 1000 nits: the streaming-delivery mastering peak, when nothing better is known

    /// <summary>
    /// The peak this roll's extended render aims at, from the slider; zero while HDR is off.
    /// This is what <see cref="FrameParams.HdrPeakNits"/> stores.
    /// </summary>
    private double HdrPeakNits => _hdrEnabled ? NitsForStops(_hdrLimitStops) : 0d;

    private static double NitsForStops(double stops) => OutputTarget.ReferenceWhiteNits * Math.Pow(2d, stops);
    private static double StopsForNits(double nits) => Math.Log2(nits / OutputTarget.ReferenceWhiteNits);

    /// <summary>The selected target's headroom above diffuse white (1 for SDR); see <see cref="OutputTarget.HighlightHeadroom"/>.</summary>
    private float CurrentTargetHeadroom
        => new FrameParams { HdrPeakNits = HdrPeakNits }.ResolvedOutputTarget.HighlightHeadroom;

    /// <summary>
    /// Whether this roll renders above diffuse white (D-021). Off, the rendering is bit-identical
    /// to what it was before HDR existed.
    ///
    /// Roll-level, like the output space: a roll whose frames disagree on dynamic range has no
    /// comparable contact sheet. Changing it rebuilds the thumbnails, because it changes what
    /// each frame IS, not how it is viewed.
    /// </summary>
    public bool HdrEnabled
    {
        get => _hdrEnabled;
        set
        {
            // A LegacyV1 roll renders through the frozen v1 path, which never reaches the
            // parameterized terminal (D-013): a peak stored on it would be a lie the file tells.
            if (UsesLegacyColorPipeline) value = false;
            if (_hdrEnabled == value) return;
            _hdrEnabled = value;
            OnPropertyChanged(nameof(HdrEnabled));
            OnPropertyChanged(nameof(CanChooseOutputSpace));
            OnPropertyChanged(nameof(PrintLutTargetNote));
            // Said at the moment it starts mattering, not left for the greyed controls to imply:
            // the extended render keeps neither the roll's output space (D-024) nor an SDR print
            // stock (D-021), so a roll that had either set changes more than its highlights. A
            // LUT declared as HDR output is the opposite case and is said so too (D-033).
            if (value && (_outputSpaceIndex != 0 || HasPrintLut))
            {
                StatusText = ActivePrintLutContract() switch
                {
                    { IsExtendedOutput: true } => Loc.T("HDR 开启：本卷的 LUT 渲染到 Rec2020 PQ，扩展渲染走它（肩部由 LUT 决定）；输出空间不参与（载体恒为线性扩展 sRGB）。"),
                    { } => Loc.T("HDR 开启：印片的颜色保留，影调由解析肩部铺到所设上限（拐点以下与印片逐位相同）；输出空间不参与（载体恒为线性扩展 sRGB）。"),
                    _ => Loc.T("HDR 开启：输出空间不参与扩展渲染（载体恒为线性扩展 sRGB）；关闭 HDR 即恢复。"),
                };
            }
            else if (!value && ActivePrintLutContract() is { IsExtendedOutput: true })
                StatusText = Loc.T("HDR 关闭：本卷的 LUT 渲染到 HDR，SDR 渲染下让位给标准显示渲染；开启 HDR 即恢复。");
            // The picker's rows depend on the state (SDR stocks vs HDR LUTs), so rebuild them
            // around whatever the roll currently names.
            RebuildPrintLutList(HasPrintLut ? _printLutPaths[_printLutIndex] : "");
            NotifyPrintLutContract();
            ApplyHdrToRoll(rebuildThumbnails: true);
        }
    }

    /// <summary>
    /// How far above SDR white the render may reach, in stops — Lightroom's HDR Limit. Snapped to
    /// a tenth of a stop from the slider; a value loaded from a roll keeps its exact figure.
    /// </summary>
    public double HdrLimitStops
    {
        get => _hdrLimitStops;
        set
        {
            double v = Math.Clamp(Math.Round(value / HdrLimitStep) * HdrLimitStep, HdrLimitMinStops, HdrLimitMaxStops);
            if (Math.Abs(_hdrLimitStops - v) < 1e-9) return;
            _hdrLimitStops = v;
            OnPropertyChanged(nameof(HdrLimitStops));
            if (_hdrEnabled) ApplyHdrToRoll(rebuildThumbnails: false);
            else NotifyHdrText();
        }
    }

    /// <summary>
    /// What the export dialog carries: the limit in stops while HDR is on, zero otherwise — the
    /// same distinction <see cref="FrameParams.HdrPeakNits"/> makes with zero.
    /// </summary>
    public double ExportHdrLimitStops => _hdrEnabled ? _hdrLimitStops : 0d;

    /// <summary>
    /// Writes the roll's peak onto every frame and re-renders. The thumbnails are rebuilt only
    /// when the SWITCH moved: the strip shows the SDR rendition (D-031), which the limit does not
    /// enter — a drag of the slider used to restart the roll's thumbnail pass for a strip that
    /// came back pixel for pixel the same. The switch does change it, because with HDR on the
    /// rendition drops the roll's output space and print stock (D-021/D-024).
    /// </summary>
    private void ApplyHdrToRoll(bool rebuildThumbnails)
    {
        double nits = HdrPeakNits;
        foreach (RollFrame f in Frames) f.Params.HdrPeakNits = nits;
        if (Frames.Count > 0) MarkRollDirty();
        NotifyHdrText();
        ScheduleRender();
        if (rebuildThumbnails) RebuildThumbnails();
    }

    private void RebuildThumbnails()
    {
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
    }

    private void NotifyHdrText()
    {
        OnPropertyChanged(nameof(HdrLimitText));
        OnPropertyChanged(nameof(HdrLimitHint));
        OnPropertyChanged(nameof(HdrToggleTooltip));
        OnPropertyChanged(nameof(HistogramTooltip));
    }

    /// <summary>
    /// What the display under the window can physically show above SDR white, as the composition
    /// root last reported it. INFORMATIONAL ONLY: invariant I5 forbids the display from changing
    /// the render, and nothing here feeds a render — it feeds the hint under the slider and the
    /// default for a NEW roll (D-027), so the user learns where the panel stops.
    /// </summary>
    private DisplayHdrCapability? _displayCapability;

    /// <summary>Called by the composition root whenever the display contract changes.</summary>
    public void SetDisplayHdrCapability(DisplayHdrCapability? capability)
    {
        if (Equals(_displayCapability, capability)) return;
        _displayCapability = capability;
        OnPropertyChanged(nameof(DisplayHdrHeadroom));
        OnPropertyChanged(nameof(DisplayHdrStops));
        OnPropertyChanged(nameof(HasDisplayHdrStops));
        NotifyHdrText();
    }

    /// <summary>For the histogram's "the panel stops here" marker; one when unknown or SDR.</summary>
    public double DisplayHdrHeadroom => _displayCapability?.Headroom ?? 1d;

    // Windows gives nits and a ratio, macOS only the ratio (see DescribeDisplayFit); a failure to
    // read the panel leaves the hint without a verdict rather than guessing.
    private bool DisplayHeadroomIsKnown =>
        _displayCapability is { FailureReason: null } d && d.Headroom > 1f && float.IsFinite(d.Headroom);

    /// <summary>Stops above SDR white this display shows in full; NaN when unknown or not HDR.</summary>
    public double DisplayHdrStops => DisplayHeadroomIsKnown ? Math.Log2(_displayCapability!.Headroom) : double.NaN;

    public bool HasDisplayHdrStops => DisplayHeadroomIsKnown;

    /// <summary>
    /// What a NEW roll starts at (D-027): HDR on, with the limit at what the display under the
    /// window shows in full (to the tenth below, inside the slider's range); off when there is no
    /// such display. Read once, at import. From then on the value is the roll's own, saved in
    /// its file, and no display change touches it — a roll that rendered differently on another
    /// machine is the whole of I5's concern, and the reason this stops at the default instead of
    /// following the display.
    /// </summary>
    private double DefaultHdrPeakNitsForNewRoll()
    {
        if (!DisplayHeadroomIsKnown) return 0d;
        double stops = Math.Floor(DisplayHdrStops / HdrLimitStep) * HdrLimitStep;
        if (stops < HdrLimitMinStops) return 0d;
        return NitsForStops(Math.Min(stops, HdrLimitMaxStops));
    }

    /// <summary>
    /// What the histogram's zones and lines mean for the frame on screen. An extended render's
    /// histogram has two different axes side by side — encoded value on the left, stops on the
    /// right — and a line whose position depends on the monitor; none of that is guessable from
    /// the picture alone.
    /// </summary>
    public string HistogramTooltip
    {
        get
        {
            if (Histogram is not { IsExtended: true })
                return Loc.T("已渲染正片的 RGB 直方图，横轴为输出空间的编码值 0–1；刻度在图下的直尺上。");

            string zones = Loc.T("左侧 3/4：SDR 区，按 sRGB 编码值 0–1，与 SDR 渲染的直方图同形；直尺上的「1」即 SDR 白（diffuse white）。")
                + "\n" + Loc.F($"右侧 1/4：白点以上 +{Histogram.ExtendedStops:0} 档（对数刻度，与 Lightroom 同为固定 4 档），直尺每格 1 档。");
            double headroom = DisplayHdrHeadroom;
            string clip = headroom > 1d && double.IsFinite(headroom)
                ? "\n" + Loc.F($"红色虚线与直尺红刻度：这块屏能显示到 +{Math.Log2(headroom):0.0} 档（余量 {headroom:0.0}×）；其右的红区在预览里被压进红线以内（高光软校样），导出保留原值。")
                : "\n" + Loc.T("当前显示器不在 HDR 模式，或读不到面板峰值：直尺上没有红色刻度。");
            return zones + clip;
        }
    }

    /// <summary>
    /// False while HDR is on. The extended render's carrier is fixed to linear extended sRGB
    /// (D-024), so the output-space picker would change nothing. It is HIDDEN and replaced by a
    /// line saying what applies instead — not greyed: a greyed "sRGB" reads as "HDR is clipped
    /// to sRGB", and the truth is the opposite (the carrier clips no gamut at all). The reason is
    /// also in the toggle's tooltip, the hint under the slider and the status bar at the moment
    /// of switching.
    ///
    /// The film-style picker used to hide with it. It no longer does (D-033): a LUT declared as
    /// HDR output IS the extended rendering, so the picker stays, and
    /// <see cref="PrintLutTargetNote"/> says when the chosen cube stands aside instead.
    /// </summary>
    public bool CanChooseOutputSpace => !_hdrEnabled;

    /// <summary>The toggle's hover text: what the control is, then where the roll stands on this display.</summary>
    public string HdrToggleTooltip =>
        Loc.T("关：印相渲染，高光收进纸白，一直以来的行为；不确定就关。开：纸白之上的宽容度铺到直方图下方【HDR 上限】所设的档数，导出随之。预览在本机余量内自动软校样。")
        + "\n" + Loc.T("开启时【输出空间】不参与：扩展渲染不裁到任何色域（D-024）。【胶片风格】照常参与：印片取其颜色，影调由解析肩部打开、拐点以下与印片逐位相同（D-034）；声明为 Rec2020 PQ 输出的 LUT 则是 HDR 渲染本身（D-033）。")
        + "\n" + HdrLimitHint;

    /// <summary>
    /// False for a LegacyV1 roll. Its rendering is frozen by D-013 and never reaches the
    /// parameterized terminal, so offering the toggle would let the user choose something that
    /// does nothing — which is exactly what happened before this existed.
    /// </summary>
    public bool CanChooseHdrPeak => !UsesLegacyColorPipeline;

    /// <summary>The slider's readout: stops, and the mastering peak they mean in nits (diffuse white 203, BT.2408).</summary>
    public string HdrLimitText => Loc.F($"+{_hdrLimitStops:0.0} 档 · {NitsForStops(_hdrLimitStops):0} nits");

    /// <summary>
    /// What the limit means on this display right now, under the slider.
    ///
    /// <para>
    /// The D-032 / D-036 consequence is stated here for the same reason D-023's restriction used
    /// to be: the control that sets the range is the right place to say what the range does.
    /// Under HDR the span-defined tone controls (highlights/shadows, curves) reach the limit — a
    /// curve's right end is the peak — so moving this slider re-grades them, which is not what it
    /// does in SDR; contrast, levels and saturation are anchored to SDR white and do not move.
    /// </para>
    /// </summary>
    public string HdrLimitHint
    {
        get
        {
            if (UsesLegacyColorPipeline)
                return Loc.T("此卷仍是旧版色彩管线（v1），渲染按 D-013 冻结，HDR 不会生效。迁移到 v2 之后可用。");

            if (!_hdrEnabled)
                return Loc.T("HDR 关闭：印相渲染，高光收进纸白。");

            string graded = CurrentFrame is { } frame &&
                            Stage2.HasDisplayReferredAdjustments(frame.Params)
                ? Loc.T("高光阴影与曲线在 HDR 下跨到这个上限（曲线右端 = 峰值），改上限会一起改它们的影调；反差／色阶／饱和度锚在 SDR 白，与 SDR 下相同（D-036）。")
                : string.Empty;

            string ignored = _outputSpaceIndex != 0 || _printLutIndex != 0
                ? Loc.T("本卷选的输出空间／胶片风格在 HDR 下不参与渲染（D-024 / D-021）。")
                : string.Empty;

            return DescribeDisplayFit(_hdrLimitStops)
                + (ignored.Length == 0 ? string.Empty : "\n" + ignored)
                + (graded.Length == 0 ? string.Empty : "\n" + graded);
        }
    }

    /// <summary>
    /// Where THIS display stops showing what the limit puts above SDR white.
    ///
    /// <para>
    /// The target's shoulder aims at <c>2^limit</c> times diffuse white; the panel shows up to
    /// <c>panelPeak / sdrWhite</c> times it (D-020, with the headroom from
    /// <c>IDXGIOutput6::GetDesc1</c>). Everything between those two numbers is soft-proofed into
    /// the panel's range (D-028) — the reason the user needs to see both numbers is that the
    /// export keeps the first.
    /// </para>
    /// </summary>
    private string DescribeDisplayFit(double targetStops)
    {
        if (_displayCapability is not { } display)
            return Loc.T("当前显示器不在 HDR 模式：这里的选择只影响导出，以及在 HDR 屏上的显示。");
        double displayStops = Math.Log2(display.Headroom);
        string fit = targetStops <= displayStops
            ? Loc.F($"上限 +{targetStops:0.0} 档可完整显示。")
            : displayStops > 0d
                ? Loc.F($"上限 +{targetStops:0.0} 档超出 {targetStops - displayStops:0.0} 档：预览把超出部分压进 +{displayStops:0.0} 档以内（高光软校样），导出不受影响；把 SDR 内容亮度调低可换到更多余量。")
                : Loc.F($"上限 +{targetStops:0.0} 档，此屏没有纸白之上的余量：预览裁切在纸白，导出不受影响。");
        if (display.PanelPeakNits is not { } panelPeak)
        {
            // macOS reports headroom as a ratio and never as nits: the EDR carrier is defined
            // relative to SDR white, so a ratio is the whole truth there, not a missing number.
            if (display.FailureReason is null && display.Headroom > 1f)
                return Loc.F($"此屏当前 EDR 余量 +{displayStops:0.0} 档（{display.Headroom:0.0}×，随亮度设置变化）。") + fit;
            return Loc.F($"显示器在 HDR 模式，但读不到面板峰值亮度（{display.FailureReason}），无法判断会不会裁。");
        }

        string tier = panelPeak >= 1000f ? "DisplayHDR 1000"
            : panelPeak >= 600f ? "DisplayHDR 600"
            : panelPeak >= 400f ? "DisplayHDR 400"
            : Loc.T("低于 DisplayHDR 400");
        string screen = Loc.F(
            $"此屏 ≈ {tier} 级（峰值 {panelPeak:0} nits），SDR 白 {display.SdrWhiteNits:0} nits → 余量 +{displayStops:0.0} 档（{display.Headroom:0.0}×）。");
        return screen + fit;
    }

    /// <summary>
    /// Adopts a roll's stored peak into the toggle and slider. Nothing is written back — loading
    /// is not an edit — but the next save will record the truth.
    /// </summary>
    private void SyncHdrPeak(double nits)
    {
        // What a v1 roll RENDERS is SDR whatever its file says, so that is what the controls show.
        if (UsesLegacyColorPipeline) nits = 0d;

        bool enabled = double.IsFinite(nits) && nits > OutputTarget.ReferenceWhiteNits;
        if (enabled)
        {
            // A peak outside the slider's range is pulled to its nearest end rather than refusing
            // to open the roll. Announced, because the picture will differ from the one that was
            // saved — and written through, so the file stops disagreeing with the controls.
            double stops = StopsForNits(nits);
            double clamped = Math.Clamp(stops, HdrLimitMinStops, HdrLimitMaxStops);
            if (Math.Abs(clamped - stops) > 1e-9)
            {
                foreach (RollFrame f in Frames) f.Params.HdrPeakNits = NitsForStops(clamped);
                if (Frames.Count > 0)
                {
                    MarkRollDirty();
                    StatusText = Loc.F($"HDR 上限 +{stops:0.0} 档（{nits:0} nits）超出本版本范围，本卷改按 +{clamped:0.0} 档——高光会与上次打开时不同。");
                }
            }
            _hdrLimitStops = clamped;
        }
        _hdrEnabled = enabled;
        OnPropertyChanged(nameof(HdrEnabled));
        OnPropertyChanged(nameof(CanChooseOutputSpace));
        NotifyPrintLutContract();
        OnPropertyChanged(nameof(HdrLimitStops));
        NotifyHdrText();
    }

    // ══ 胶片风格（印片 LUT） ═══════════════════════════════════════════════════
    //
    // 与【输出空间】并列而不是并入其中，因为两者正交：LUT 决定画面被渲染成什么样，输出空间
    // 决定它被装进哪个容器。曾经把 "Kodak2383" 当成一个 ColorSpaceDef 塞进输出空间下拉——
    // 那是类型错误，三个色度坐标表达不了一张印片的响应，后来删掉了。
    //
    // 随程序发行的 LUT 文件夹（程序旁的 luts/，见 PrintLuts.BundledDir）排在最前面：Resolve 的
    // 六张 Rec709 Film Looks 加两张烘出来的标准 HDR 渲染。工程存的是 :luts/<文件名> 这样的
    // 记号而不是本机路径，所以换机器打开照样渲染。用户自己的 cube 放 per-user 的 LUT 文件夹
    // 按路径存，下拉里的名字取自各自文件的 TITLE。D-033 之前的 :kodak-2383 记号仍能解析。

    /// <summary>Cubes the picker offers, in order:
    /// 标准渲染 → 随程序发行的 → LUT 文件夹里的 → 选择文件…</summary>
    public ObservableCollection<string> PrintLutNames { get; } = new();

    /// <summary>
    /// The picker's shape: row 0 is the standard display rendering, then the built-in print
    /// stocks, then the drop-in folder, then whatever cubes the user picked before, then the
    /// "choose a file" verb.
    /// Only row 0 and the trailing verb are not paths, which is why the lookups below scan
    /// <c>1 .. Count-2</c>.
    ///
    /// A pure CST (Cineon log decoded, no rendering) briefly sat at row 1. It is gone from the
    /// pipeline too, not merely hidden here — an unrendered log plate is a step in someone else's
    /// grading pipeline rather than a look a roll picks, and it had no users. A roll naming the
    /// old <c>:cineon-log</c> sentinel now falls through to the standard rendering, which is what
    /// <see cref="ColorPipeline.ToOutputSpaceFor"/> does with any value that is not a resolvable
    /// cube.</summary>
    /// <summary>Full paths parallel to <see cref="PrintLutNames"/>; "" for 无.</summary>
    private readonly List<string> _printLutPaths = new();

    private int _printLutIndex;

    /// <summary>
    /// The roll's print-film emulation, as an index into <see cref="PrintLutNames"/>. The last
    /// entry is the "choose a file" action rather than a stock, so selecting it opens a dialog and
    /// the index lands wherever that ends up.
    ///
    /// Roll-uniform for the same reason the output space is: a strip whose frames used different
    /// stocks would be a contact sheet of incomparable renders.
    /// </summary>
    public int PrintLutIndex
    {
        get => _printLutIndex;
        set
        {
            if (value < 0 || value >= PrintLutNames.Count) return;

            // The trailing "选择 .cube 文件…" row is a verb, not a choice.
            if (value == PrintLutNames.Count - 1)
            {
                OnPropertyChanged(nameof(PrintLutIndex));   // snap the box back
                _ = PickPrintLutAsync();
                return;
            }

            if (_printLutIndex == value) return;
            _printLutIndex = value;
            ApplyPrintLut(_printLutPaths[value]);
        }
    }

    /// <summary>What the selected entry is, shown under the picker.</summary>
    ///
    /// Entry 0 is NOT "no transform" — it is a display RENDERING, doing analytically the job a
    /// cube does (see ColorPipeline.CineonToDisplay). It used to be labelled 无（直通）, which read
    /// as "nothing happens here", and then 标准（Cineon → 输出空间）, which claimed to be a plain
    /// standard conversion. Neither was true: it folds in a response gamma and normalises the film
    /// base to black, which is a look, not a container change.
    ///
    /// It does not name Rec709, because the conversion lands in whatever the OUTPUT SPACE picker
    /// says — naming a fixed space here would contradict the control beside it.
    ///
    /// Row 1 was the pure CST and is now the first cube; the switch below therefore has no
    /// special case left between the standard rendering and the stocks.
    public string PrintLutHint => _printLutIndex switch
    {
        0 => Loc.T("标准显示渲染：解 Cineon 编码并套用显示渲染（响应 gamma 0.6，片基归零）。想直接看片子就用它。"),
        _ => Loc.F($"印片模拟：{PrintLutNames[_printLutIndex]}。反差与色彩由该胶片决定，帧编辑在它之后。"),
    };

    // ══ LUT 合同（D-033） ══════════════════════════════════════════════════════
    //
    // .cube 文件不携带色彩空间。输入这一端没有选择：本程序的图像处理基于 Cineon，喂给 cube 的
    // 永远是 Cineon（LutInputEncoding 只有这一个成员）。要声明的只有输出——LUT 出来的是什么
    // 编码：Rec.709 2.4 / DCI-P3 2.6 / sRGB（SDR 印片，两种状态下都能用：SDR 下就是渲染本身，
    // HDR 下取它的颜色、影调由解析肩部打开，D-034）或 Rec.2100 PQ（HDR LUT，只在 HDR 下能用）。
    //
    // 选片器按状态过滤：SDR 下不列 PQ 输出的 cube（它在 SDR 下只会让位）；HDR 下全列。输出
    // 下拉里 PQ 那一项也只在 HDR 下可见。没有声明的 cube（Resolve 生成的一律不写）按 Rec.709
    // 2.4 预填并在状态栏说明——世上绝大多数 cube 是 SDR 的；真是 HDR LUT 的在下拉里改成 PQ。
    // 卷载入时若它的 cube 与状态不匹配（带着 PQ LUT 关了 HDR），仍列出来并注明让位。

    private static readonly LutOutputEncoding[] LutOutputs =
    {
        LutOutputEncoding.Rec709,
        LutOutputEncoding.DciP3,
        LutOutputEncoding.Srgb,
        LutOutputEncoding.Rec2020Pq,   // the dropdown shows this row only while HDR is on
    };

    private static string ShortName(LutOutputEncoding e) => e switch
    {
        LutOutputEncoding.Rec709 => "Rec.709 2.4",
        LutOutputEncoding.DciP3 => "DCI-P3 2.6",
        LutOutputEncoding.Srgb => "sRGB",
        LutOutputEncoding.Rec2020Pq => "Rec.2100 PQ",
        _ => "?",
    };

    /// <summary>The output the roll's cube is declared with; Rec709 when there is no cube.</summary>
    private LutOutputEncoding _printLutOutput = LutOutputEncoding.Rec709;

    /// <summary>Whether a cube (not the standard rendering, not the "choose a file" verb) is selected.</summary>
    public bool HasPrintLut => _printLutIndex > 0 && _printLutIndex < PrintLutNames.Count - 1;

    /// <summary>
    /// Whether the output declaration is offered at all. Only for a cube whose header declares
    /// NOTHING — a LUT generated from a Resolve grade. A cube whose header names its output
    /// (Resolve's film looks: "Display: ITU-Rec.709, Gamma 2.4") has stated a fact about
    /// itself, and offering to override it only invites reading gamma-2.4 code values as
    /// absolute PQ luminance, which is what the user saw when they tried. Also never on a
    /// legacy roll: the frozen v1 exit only ever knew Rec709 out (D-013).
    /// </summary>
    public bool CanEditPrintLutContract =>
        HasPrintLut && !UsesLegacyColorPipeline
        && HeaderOutput(_printLutPaths[_printLutIndex]) == LutOutputEncoding.Unknown;

    /// <summary>The declared output as the name <see cref="FrameParams.PrintLutOutput"/> stores.</summary>
    private string PrintLutOutputName => _printLutOutput.ToString();

    /// <summary>The declared output, as an index into the flyout's rows.</summary>
    public int PrintLutOutputIndex
    {
        get => Math.Max(0, Array.IndexOf(LutOutputs, _printLutOutput));
        set
        {
            int v = Math.Clamp(value, 0, LutOutputs.Length - 1);
            if (_printLutOutput == LutOutputs[v]) return;
            _printLutOutput = LutOutputs[v];
            WritePrintLutContract();
        }
    }

    /// <summary>The declaration in one line, for the button that opens the dropdown.</summary>
    public string PrintLutContractText => Loc.F($"输出 {ShortName(_printLutOutput)}");

    /// <summary>
    /// Says when the chosen cube is NOT being applied — an HDR LUT on a roll with HDR off
    /// (D-033) — and, while HDR is on with a print stock, that the render is the print's colour
    /// with the HDR tone (D-034). Empty otherwise. Shown beside the picker so the user is never
    /// looking at a selected stock and a picture that does not carry it.
    /// </summary>
    public string PrintLutTargetNote
    {
        get
        {
            if (!HasPrintLut) return "";
            bool hdrLut = _printLutOutput == LutOutputEncoding.Rec2020Pq;
            if (!_hdrEnabled && hdrLut) return Loc.T("让位：HDR LUT 不参与 SDR 渲染");
            if (_hdrEnabled && !hdrLut) return Loc.T("印片色 · HDR 影调");
            return "";
        }
    }

    /// <summary>The contract the render is using for the roll's cube, or null when there is none.</summary>
    private LutContract? ActivePrintLutContract() =>
        HasPrintLut ? new LutContract(LutInputEncoding.Cineon, _printLutOutput) : null;

    /// <summary>
    /// The output a cube that declares nothing is taken to have. Rec709 regardless of state:
    /// nearly every cube in existence is SDR, and a print stock is usable under HDR too
    /// (D-034), so this is the guess that is right most often and wrong most visibly.
    /// </summary>
    private static LutOutputEncoding DefaultOutputForState() => LutOutputEncoding.Rec709;

    /// <summary>
    /// The output to show and store for <paramref name="path"/>: the roll's own declaration
    /// where it has one, else what the file's header prefilled, else the convention
    /// (<see cref="DefaultOutputForState"/>).
    /// </summary>
    private LutOutputEncoding PrefillOutput(string path, FrameParams? roll)
    {
        CubeLut? lut = PrintLuts.Resolve(path);
        LutOutputEncoding o = lut is null
            ? LutOutputEncoding.Unknown
            : roll?.LutContractFor(lut).Output ?? lut.OutputEncoding;
        return o == LutOutputEncoding.Unknown ? DefaultOutputForState() : o;
    }

    /// <summary>What the cube's own header says, or Unknown — used to decide which state's list it belongs in.</summary>
    private static LutOutputEncoding HeaderOutput(string path) =>
        PrintLuts.Resolve(path)?.OutputEncoding ?? LutOutputEncoding.Unknown;

    /// <summary>
    /// Whether a cube with this header belongs in the picker under the roll's current state:
    /// everything under HDR (a print stock is its colour, D-034; a PQ LUT is the rendering,
    /// D-033); everything but a PQ LUT under SDR, where it would only stand aside.
    /// </summary>
    private bool OfferedInCurrentState(LutOutputEncoding header) =>
        header != LutOutputEncoding.Rec2020Pq || _hdrEnabled;

    private void SetContractOutput(LutOutputEncoding o)
    {
        _printLutOutput = o;
        NotifyPrintLutContract();
    }

    private void NotifyPrintLutContract()
    {
        OnPropertyChanged(nameof(PrintLutOutputIndex));
        OnPropertyChanged(nameof(PrintLutContractText));
        OnPropertyChanged(nameof(PrintLutTargetNote));
        OnPropertyChanged(nameof(HasPrintLut));
        OnPropertyChanged(nameof(CanEditPrintLutContract));
    }

    /// <summary>
    /// Writes the picker's output declaration to every frame and re-renders. Roll-uniform like
    /// the cube itself. Stage 2 is NOT reset here, unlike <see cref="ApplyPrintLut"/>: trying
    /// the three outputs to see which one the file was made for is the normal use, and losing
    /// the grade on each try would make that unusable.
    /// </summary>
    private void WritePrintLutContract()
    {
        foreach (RollFrame f in Frames) f.Params.PrintLutOutput = _printLutOutput.ToString();
        if (Frames.Count > 0) MarkRollDirty();
        NotifyPrintLutContract();
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
        ScheduleRender();
    }

    /// <summary>
    /// Writes the chosen cube to every frame and re-renders the roll. Stage 2 is CARRIED OVER.
    ///
    /// It used to be reset on every switch, on the argument that Stage 2 runs after the display
    /// rendering and its numbers are relative to that render's zero, so a grade fitted to one
    /// look is a different correction on another. True — and the user asked for the opposite
    /// anyway (2026-09-13): comparing stocks on a frame you have already graded is the normal
    /// use, and losing the grade on each switch made that unusable. The numbers are re-read
    /// against the new render, which is exactly what "在当前输出空间里调这么多" already means for
    /// a change of output space; a stock is a bigger change of the same kind, not a different
    /// kind. The calibration is untouched throughout: it describes the NEGATIVE.
    ///
    /// NO PATH GETS AUTO-LEVELS either: every rendering places its own ends (a print's ends are
    /// its own and deliberately not 0 and 1 — on Kodak 2383 code 685 renders at 0.880 and code
    /// 95 at 0.037; that toe and shoulder ARE the look), so measuring the result and stretching
    /// it back to 0..1 would override the very thing the user just chose.
    ///
    /// Undo still covers the switch: the roll's params are snapshotted before it.
    /// </summary>
    private void ApplyPrintLut(string path)
    {
        CommitLiveParams(CurrentFrame);   // fold the live sliders in so they travel with the switch
        CommitUndo();

        // The declaration travels with the cube: a new file gets its own header's prefill (or
        // the convention for the roll's state), never the previous cube's declaration.
        LutOutputEncoding output = PrefillOutput(path, roll: null);
        foreach (RollFrame f in Frames)
        {
            f.Params.PrintLut = path;
            f.Params.PrintLutOutput = string.IsNullOrEmpty(path) ? "" : output.ToString();
        }
        if (Frames.Count > 0) MarkRollDirty();
        SetContractOutput(output);

        OnPropertyChanged(nameof(PrintLutIndex));
        OnPropertyChanged(nameof(PrintLutHint));
        NotifyPrintLutContract();

        // Thumbnails change too — this alters what each frame IS, not how it is shown.
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
        ScheduleRender();
    }

    /// <summary>The SDR/HDR state the picker's rows were last filtered for; null = never built.</summary>
    private bool? _printLutListForHdr;

    /// <summary>
    /// Rebuilds the picker from settings, selecting <paramref name="active"/>. Rows are filtered
    /// to the roll's SDR/HDR state (see the section comment above): a list that offered an SDR
    /// print stock under HDR would offer something the render will stand aside from.
    /// </summary>
    private void RebuildPrintLutList(string active)
    {
        // Selecting an EXISTING entry must not touch the collection. Clearing an ObservableCollection
        // that a ComboBox is bound to drives its SelectedIndex to -1, and -1 renders as an empty
        // box — so rebuilding on every frame load blanked the picker even though the roll's LUT
        // was unchanged and still rendering. Frame switches are the common case and they never
        // change the list, only which row is current.
        // Row 0 is the standard display rendering, which "" already selects above, so a path
        // lookup starts at 1 — that includes the BUNDLED rows, whose sentinels are perfectly
        // findable paths. The trailing "choose a file" verb is excluded as before.
        //
        int existing = _printLutPaths.Count == 0 || _printLutListForHdr != _hdrEnabled ? -1
            : string.IsNullOrWhiteSpace(active) ? 0
            : _printLutPaths.FindIndex(1, Math.Max(_printLutPaths.Count - 2, 0),
                                       p => p.Equals(active, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (_printLutIndex != existing)
            {
                _printLutIndex = existing;
                OnPropertyChanged(nameof(PrintLutIndex));
                OnPropertyChanged(nameof(PrintLutHint));
            }
            return;
        }

        _printLutListForHdr = _hdrEnabled;
        var names = new List<string>();
        var ids = new List<string>();

        names.Add(Loc.T("标准显示渲染（CST + 显示渲染）"));
        ids.Add("");

        // The shipped folder first — Resolve's film looks. Stored as :luts/ sentinels, so they
        // are as portable as the old embedded pair was.
        foreach ((string id, string name) in PrintLuts.Bundled())
        {
            if (!OfferedInCurrentState(HeaderOutput(id))) continue;
            names.Add(name);
            ids.Add(id);
        }

        // The drop-in folder (Settings.LutDir), and nothing else: a deliberate library rather than
        // a history.
        //
        // NO "RECENTLY USED" ROWS. The picker used to append every cube ever chosen through the
        // file dialog. That made sense when the app shipped no stocks at all, but it now carries
        // two built-ins and offers a drop-in folder, and the history had become actively
        // confusing: a cube downloaded to ~/Downloads and picked once sat in the list forever,
        // labelled by its filename — which for the very stocks we bundle is the SAME text as the
        // built-in row, so the picker showed what looked like duplicated entries with no way to
        // tell which was which. A file worth keeping belongs in the LUT folder, which is one copy
        // away and says so in the menu.
        var paths = new List<string>();
        foreach (string p in PrintLuts.InFolder(Settings.LutDir))
            if (OfferedInCurrentState(HeaderOutput(p))) paths.Add(p);

        // A roll can still name a cube that is in neither place — a project from another machine,
        // a file picked before it was moved, a pre-D-033 project's :kodak-2383 sentinel, or a
        // cube the current state filtered out (a print stock on a roll that then turned HDR on).
        // It goes in at the top so the picker shows what the render is actually using rather
        // than 无, labelled by the cube's own title; PrintLutTargetNote says if it stands aside.
        if (!string.IsNullOrWhiteSpace(active)
            && !ids.Contains(active, StringComparer.OrdinalIgnoreCase)
            && !paths.Contains(active, StringComparer.OrdinalIgnoreCase))
            paths.Insert(0, active);

        foreach (string p in paths)
        {
            // The cube's own TITLE when it loads, so the vendor name on screen is the user's file
            // describing itself. A file that has gone missing is still listed, marked, so the user
            // can see WHY the roll stopped looking right instead of finding 无 selected.
            string label;
            try { label = PrintLuts.Validate(p).Title; }
            catch { label = Loc.F($"{Path.GetFileNameWithoutExtension(p)}（文件缺失）"); }
            names.Add(label);
            ids.Add(p);
        }

        names.Add(Loc.T("选择 .cube 文件…"));
        ids.Add("");

        int i = ids.FindIndex(1, ids.Count - 2, p => p.Equals(active, StringComparison.OrdinalIgnoreCase));
        int index = string.IsNullOrWhiteSpace(active) ? 0 : (i < 0 ? 0 : i);

        // Only touch the bound collection when the rows actually changed. Clearing it drives
        // the ComboBox's SelectedIndex to -1 and the box goes blank until the posted
        // notification below lands; the HDR toggle rebuilds on every flip and, with no HDR LUT
        // installed, produces the very same rows — so it must not clear anything.
        if (ids.SequenceEqual(_printLutPaths, StringComparer.OrdinalIgnoreCase)
            && names.SequenceEqual(PrintLutNames, StringComparer.Ordinal))
        {
            if (_printLutIndex != index)
            {
                _printLutIndex = index;
                OnPropertyChanged(nameof(PrintLutIndex));
                OnPropertyChanged(nameof(PrintLutHint));
            }
            return;
        }

        PrintLutNames.Clear();
        _printLutPaths.Clear();
        foreach (string n in names) PrintLutNames.Add(n);
        _printLutPaths.AddRange(ids);
        _printLutIndex = index;

        // Posted rather than raised inline. The collection change above reaches the ComboBox
        // first and resets its selection to -1; a notification raised in the same turn is
        // overwritten by that reset and the box is left blank. Queuing it puts the selection
        // back after the items have settled.
        OnPropertyChanged(nameof(PrintLutIndex));
        OnPropertyChanged(nameof(PrintLutHint));
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(PrintLutIndex));
            OnPropertyChanged(nameof(PrintLutHint));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Adopt a roll's saved cube and its output declaration into the picker. Loading, not
    /// choosing — not dirty, with one exception: a roll whose cube declares no output anywhere
    /// (a project from before D-033 that named an external file) is given the convention for
    /// its state and told so, the same way <see cref="SyncOutputSpace"/> migrates a space the
    /// picker no longer offers. Left alone, that roll could not render at all.
    /// </summary>
    private void SyncPrintLut(FrameParams p)
    {
        string path = p.PrintLut ?? "";
        RebuildPrintLutList(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetContractOutput(LutOutputEncoding.Rec709);
            return;
        }

        LutOutputEncoding o = PrefillOutput(path, p);
        bool undeclared = PrintLuts.Resolve(path) is { } lut
                          && p.LutContractFor(lut).Output == LutOutputEncoding.Unknown;
        if (undeclared && Frames.Count > 0)
        {
            foreach (RollFrame f in Frames) f.Params.PrintLutOutput = o.ToString();
            MarkRollDirty();
            StatusText = Loc.F($"本卷的 LUT 没有声明输出色彩空间，已按 {ShortName(o)} 采用；可在「输出」按钮里改。");
        }
        SetContractOutput(o);
    }

    /// <summary>
    /// Asks for a .cube and adopts it. Validation happens here, where there is a user to tell:
    /// the render path silently degrades to pass-through, which is right for rendering and wrong
    /// for the moment someone hands us a file.
    /// </summary>
    public async Task PickPrintLutAsync()
    {
        if (PickFileAsync is null) return;
        string? path = await PickFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        CubeLut lut;
        try
        {
            PrintLuts.Forget(path);
            lut = PrintLuts.Validate(path);
        }
        catch (Exception ex)
        {
            StatusText = Loc.F($"无法载入 LUT：{ex.Message}");
            return;
        }

        // A cube that declares nothing is the normal case for a LUT generated from a Resolve
        // grade — the .cube format has no field for it. It is adopted under the convention for
        // the roll's state and the status line says which, so the person who chose the file
        // knows what to correct if the file was made for something else (D-033). A cube whose
        // header names the OTHER state's output is refused here, where there is someone to tell:
        // the render would only stand aside from it.
        LutOutputEncoding header = lut.OutputEncoding;
        if (!OfferedInCurrentState(header))
        {
            StatusText = Loc.F($"「{lut.Title}」是 HDR LUT（输出 {ShortName(header)}），HDR 关闭时不参与渲染；开启 HDR 再选它。");
            return;
        }
        LutOutputEncoding prefill = PrefillOutput(path, roll: null);
        StatusText = header == LutOutputEncoding.Unknown
            ? (_hdrEnabled
                ? Loc.F($"已载入胶片风格：{lut.Title}（{lut.Size}³）。文件头未声明输出，已按 Rec.709 2.4 采用——若它是 Resolve 以 Rec.2100 ST2084 输出生成的 HDR LUT，请在「输出」按钮里改成 Rec.2100 PQ。")
                : Loc.F($"已载入胶片风格：{lut.Title}（{lut.Size}³）。文件头未声明输出，已按 Rec.709 2.4 采用——Resolve 生成 LUT 时的输出若是 DCI-P3 或 sRGB，请在「输出」按钮里改。"))
            : Loc.F($"已载入胶片风格：{lut.Title}（{lut.Size}³），文件头声明输出 {ShortName(prefill)}。");

        // Not remembered anywhere: the picker lists the built-ins and the LUT folder, and a
        // one-off pick is exactly that. The roll itself stores the path it uses, so this cube
        // stays selected for this roll and reappears when the roll is reopened.
        RebuildPrintLutList(path);
        ApplyPrintLut(path);
    }

    /// <summary>Supplied by the view: shows a .cube open dialog, null if cancelled.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    /// <summary>What the selected output space is for, shown under the picker.</summary>
    public string OutputSpaceHint => OutputSpaces[_outputSpaceIndex].Name switch
    {
        "sRGB" => Loc.T("网页与大多数屏幕的通用选择。不确定就选它。"),
        // The wide-gamut hints say what the PREVIEW can and cannot show. Avalonia renders into an
        // sRGB surface and offers no way to change that (AvaloniaUI/Avalonia#8450), so colour
        // outside sRGB is compressed on the way to the screen no matter how good the panel is.
        // The exported file is unaffected — it carries the real profile — and saying so is the
        // difference between a known limit and the user mistrusting their own monitor.
        "DisplayP3" => Loc.T("现代屏幕（Apple 设备、多数新款显示器）的宽色域，编码曲线与 sRGB 相同。预览窗口受限于 sRGB，超出 sRGB 的颜色看不到，但导出文件是完整的。"),
        "AdobeRGB" => Loc.T("色域比 sRGB 宽，青绿方向尤其明显，适合送印刷或继续修图。预览窗口受限于 sRGB，超出 sRGB 的颜色看不到，但导出文件是完整的；在不做色彩管理的软件里打开会偏淡。"),
        // Spaces still resolvable from older projects, so they need a label even though the picker
        // no longer offers them.
        "Rec709" => Loc.T("标准 Cineon 流程的第 4 步目标，Gamma 2.4。色域与 sRGB 相同，反差略高。"),
        _ => "",
    };

    /// <summary>
    /// Adopt a saved output space into the picker. Called when a frame or roll is loaded — this is
    /// loading, not choosing, so it does not mark the roll dirty on its own.
    ///
    /// A roll naming a space the picker no longer offers (Rec709, or the two Kodak dye-set spaces
    /// that older versions registered) is MIGRATED to sRGB and the frames are rewritten to say so. Leaving the name in place while
    /// the picker showed index 0 would be the worst outcome: the label would read sRGB while the
    /// render still used the old space, and the next edit would silently rewrite it anyway. The
    /// migration is stated in the status bar rather than done behind the user's back.
    /// </summary>
    private void SyncOutputSpace(string name)
    {
        int i = Array.FindIndex(OutputSpaces,
                                s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            string target = OutputSpaces[0].Name;
            foreach (RollFrame f in Frames) f.Params.OutputSpace = target;
            if (Frames.Count > 0)
            {
                MarkRollDirty();
                StatusText = Loc.F($"输出空间 {name} 已不再提供，本卷改用 {target}——画面会与上次打开时不同。");
            }
        }
        _outputSpaceIndex = i < 0 ? 0 : i;
        OnPropertyChanged(nameof(OutputSpaceIndex));
        OnPropertyChanged(nameof(OutputSpaceHint));
        OnPropertyChanged(nameof(CurrentOutputSpace));
    }

    /// <summary>Append more scans to the current roll (must match the roll's RAW/TIFF type);
    /// new frames inherit the current frame's Stage-1 calibration + roll-level ops, scene reset.</summary>
    public async Task AddImagesAsync(IReadOnlyList<string> paths)
    {
        if (Frames.Count == 0 || paths.Count == 0) return;

        // Type consistency: can't mix RAW and TIFF in one roll (they calibrate differently).
        bool rollRaw = RollIsRaw;
        foreach (string p in paths)
            if (RawDecode.IsRawExtension(p) != rollRaw)
            {
                StatusText = Loc.F($"类型不匹配：当前卷是 {(rollRaw ? "RAW" : "TIFF")}，无法混入 {Path.GetFileName(p)}");
                return;
            }

        // Dedup against existing real (non-virtual) source files.
        var existing = new HashSet<string>(
            Frames.Where(f => !f.IsVirtual).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var toAdd = paths.Where(p => existing.Add(p)).ToList();
        if (toAdd.Count == 0) { StatusText = Loc.T("所选文件已在当前卷中"); return; }

        // Fold the current frame's live edits in, then use its calibration as the template.
        CommitLiveParams(CurrentFrame);
        FrameParams template = (CurrentFrame?.Params ?? new FrameParams()).Clone();
        RollFrame.ResetScene(template);
        // Geometry is per-scan; don't inherit crop/straighten. The cell goes with the crop — these
        // are different files, so the current frame's place in ITS strip says nothing about them.
        template.CropRect = null; template.SplitCell = null; template.Rotation = 0;

        // The batch is sorted, but appended rather than merged into the existing frames: the roll's
        // order is the user's to own once they have dragged anything, and re-sorting the whole
        // strip on every add would undo that silently.
        foreach (string p in SortedByName(toAdd))
            Frames.Add(new RollFrame(p) { Params = template.Clone() });

        ResetUndoAfterStructural();
        StatusText = Loc.F($"已添加 {toAdd.Count} 帧（共 {Frames.Count} 帧）");
        RestartThumbnails();
        await Task.CompletedTask;
    }

    /// <summary>
    /// The frames a strip command acts on: the ticked ones when any are ticked, else the
    /// current frame. Ticking is how the strip says "these, not this one" — the copy/paste
    /// commands already read it, so the structural ones follow the same rule instead of
    /// making the user repeat a right-click per frame.
    /// </summary>
    private List<RollFrame> StripTargets()
    {
        var ticked = Frames.Where(f => f.IsSelected).ToList();
        if (ticked.Count > 0) return ticked;
        return CurrentFrame is { } cur ? new List<RollFrame> { cur } : new List<RollFrame>();
    }

    /// <summary>Finish a one-shot batch selection after its command has run.</summary>
    private void ClearStripSelection()
    {
        foreach (RollFrame frame in Frames) frame.IsSelected = false;
    }

    /// <summary>Select every frame as a batch target for the next strip operation.</summary>
    public void SelectAllFrames()
    {
        if (Frames.Count == 0) return;
        foreach (RollFrame frame in Frames) frame.IsSelected = true;
        StatusText = Loc.F($"已选择全部 {Frames.Count} 帧");
    }

    /// <summary>Clear the batch-target ticks without changing the current frame.</summary>
    public void ClearFrameSelection()
    {
        bool hadSelection = Frames.Any(frame => frame.IsSelected);
        ClearStripSelection();
        if (hadSelection) StatusText = Loc.T("已清除帧选择");
    }

    /// <summary>
    /// A virtual copy of each target frame (the ticked frames, else the current one), inserted
    /// right after its original. A single copy is selected so it can be adjusted at once; a batch
    /// leaves the selection where it was — jumping to the last of N copies would be arbitrary.
    ///
    /// A copy of a copy is allowed: it shares the same source file, sits in the same run, and is
    /// exactly what a third half-frame crop or a third grade is.
    /// </summary>
    public void CreateVirtualCopies()
    {
        List<RollFrame> parents = StripTargets();
        if (parents.Count == 0) return;
        bool usedSelection = parents.Any(f => f.IsSelected);

        CommitUndo();
        CommitLiveParams(CurrentFrame);   // capture live edits before anything is cloned
        RollFrame? last = null;
        // Walk the strip bottom-up so an insertion never shifts an index still to be visited.
        for (int i = Frames.Count - 1; i >= 0; i--)
        {
            RollFrame parent = Frames[i];
            if (!parents.Contains(parent)) continue;
            RollFrame copy = RollFrame.MakeVirtualCopy(parent);
            Frames.Insert(i + 1, copy);
            last ??= copy;
        }
        ResetUndoAfterStructural();
        if (parents.Count == 1) CurrentFrame = last;   // switch to the copy so it can be adjusted immediately
        StatusText = parents.Count == 1
            ? Loc.T("已创建虚拟副本（参数已复制）")
            : Loc.F($"已为 {parents.Count} 个勾选帧各创建虚拟副本");
        if (usedSelection) ClearStripSelection();
        RestartThumbnails();
    }

    /// <summary>
    /// Drag-reorder: move the frame at <paramref name="from"/> into the gap
    /// <paramref name="insertAt"/>.
    ///
    /// <paramref name="insertAt"/> is an INSERTION POINT, not an item index: it counts the gaps
    /// between frames, so 0 is above the first frame and <c>Frames.Count</c> is below the last.
    /// Taking an item index instead is what makes a drag ambiguous — index 2 cannot say whether
    /// the frame belongs above or below the frame already sitting there, and resolving it by
    /// direction puts every forward drag one slot too far.
    ///
    /// A real frame travels with its virtual copies. They are alternate looks at ONE negative, so
    /// splitting them across the roll would put the same photograph in two places — and on a split
    /// scan the copies are separate negatives that only share a file, which makes the group the
    /// physical strip. Dragging a copy therefore moves its whole group too, from wherever the
    /// group starts, rather than tearing it out on its own.
    ///
    /// Reordering does not touch any frame's params, so unlike the other structural edits it does
    /// not have to re-baseline undo — the index-keyed snapshots would be wrong for exactly one
    /// step, which is the strip's own order, and that is what <see cref="MarkRollDirty"/> persists.
    /// </summary>
    public void MoveFrame(int from, int insertAt)
    {
        if (from < 0 || from >= Frames.Count) return;
        (int start, int count) = GroupAt(from);
        int gap = Math.Clamp(insertAt, 0, Frames.Count);
        // Snap the gap to a group boundary: dropping between a frame and its own virtual copy
        // would otherwise split the group the move is trying to keep together.
        if (gap > 0 && gap < Frames.Count)
        {
            (int gStart, int gCount) = GroupAt(gap);
            if (gap > gStart) gap = gStart + gCount;   // inside a group → past its end
        }
        // A drop anywhere inside the moving group's own span leaves the roll as it is.
        if (gap >= start && gap <= start + count) return;
        // Re-express the gap for the list WITHOUT the moving group, which is what the insert below
        // runs against: everything after the group shifts down by its length once it is lifted out.
        int target = gap > start ? gap - count : gap;

        var moving = new List<RollFrame>(count);
        for (int i = 0; i < count; i++) moving.Add(Frames[start + i]);
        Reorder(() =>
        {
            for (int i = count - 1; i >= 0; i--) Frames.RemoveAt(start + i);
            int at = Math.Clamp(target, 0, Frames.Count);
            for (int i = 0; i < count; i++) Frames.Insert(at + i, moving[i]);
        });

        MarkRollDirty();   // frame order is saved with the project
        StatusText = Loc.T("已调整帧顺序");
    }

    /// <summary>
    /// Rearrange <see cref="Frames"/> without disturbing the current frame.
    ///
    /// The film strip binds SelectedItem to CurrentFrame, so taking the selected frame out of the
    /// collection makes the ListBox push null back through the binding — and re-inserting it pushes
    /// it in again as a "new" selection. That round-trip runs the whole frame-switch path: the
    /// outgoing frame's live edits get folded in against a null _prevFrame, and the frame is
    /// re-decoded to arrive at the state it was already in. Reordering changes no pixels, so the
    /// selection is restored by hand afterwards and the switch is suppressed while it happens.
    /// </summary>
    private void Reorder(Action shuffle)
    {
        RollFrame? keep = CurrentFrame;
        _reordering = true;
        try
        {
            shuffle();
            CurrentFrame = keep;   // the binding may have nulled it while the frame was out
        }
        finally { _reordering = false; }
        _prevFrame = CurrentFrame;   // the outgoing-frame link the next real switch relies on
    }

    /// <summary>Put the whole roll back into file-name order, groups intact.</summary>
    public void SortFramesByName()
    {
        if (Frames.Count < 2) return;
        var groups = new List<List<RollFrame>>();
        for (int i = 0; i < Frames.Count;)
        {
            (int start, int count) = GroupAt(i);
            var g = new List<RollFrame>(count);
            for (int k = 0; k < count; k++) g.Add(Frames[start + k]);
            groups.Add(g);
            i = start + count;
        }
        List<RollFrame> sorted = groups
            .OrderBy(g => g[0].Path, NaturalOrder.Instance)
            .SelectMany(g => g)
            .ToList();
        // Already in order: say so rather than dirtying the roll and rewriting the project file
        // for a no-op — the sheet cover would be regenerated too.
        if (sorted.SequenceEqual(Frames)) { StatusText = Loc.T("已经是文件名顺序"); return; }

        Reorder(() =>
        {
            Frames.Clear();
            foreach (RollFrame f in sorted) Frames.Add(f);
        });

        MarkRollDirty();
        StatusText = Loc.T("已按文件名排序");
    }

    /// <summary>
    /// The contiguous run of frames sharing the source file of <paramref name="index"/> — a real
    /// frame plus the virtual copies that follow it. Returns just that one frame when the run is
    /// broken, which is what an older project reordered by hand can look like.
    /// </summary>
    private (int Start, int Count) GroupAt(int index)
    {
        string path = Frames[index].Path;
        int start = index;
        while (start > 0 &&
               Frames[start].IsVirtual &&
               string.Equals(Frames[start - 1].Path, path, StringComparison.OrdinalIgnoreCase))
            start--;
        int end = start;
        while (end + 1 < Frames.Count &&
               Frames[end + 1].IsVirtual &&
               string.Equals(Frames[end + 1].Path, path, StringComparison.OrdinalIgnoreCase))
            end++;
        return (start, end - start + 1);
    }

    /// <summary>Remove the target frames (the ticked ones, else the current one). Removing a real
    /// frame also drops every virtual copy of it.</summary>
    public void RemoveFrames()
    {
        List<RollFrame> targets = StripTargets();
        if (targets.Count == 0) return;

        // Collect victims: the targets, plus (for a real frame) all its virtual copies.
        var victims = new HashSet<RollFrame>(targets);
        foreach (RollFrame target in targets)
            if (!target.IsVirtual)
                foreach (RollFrame f in Frames)
                    if (f.IsVirtual && string.Equals(f.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                        victims.Add(f);
        if (victims.Count >= Frames.Count) { StatusText = Loc.T("至少保留一帧，无法移除"); return; }

        int anchor = CurrentFrame is { } cur ? Frames.IndexOf(cur) : 0;
        bool currentGoes = CurrentFrame is { } c && victims.Contains(c);
        if (currentGoes) _prevFrame = null;   // don't persist the frame we're deleting on the coming switch
        for (int i = Frames.Count - 1; i >= 0; i--)
            if (victims.Contains(Frames[i])) { Retire(Frames[i].Thumbnail); Frames.RemoveAt(i); }

        ResetUndoAfterStructural();
        if (currentGoes) CurrentFrame = Frames[Math.Clamp(anchor, 0, Frames.Count - 1)];
        int copies = victims.Count - targets.Count;
        StatusText = targets.Count > 1
            ? (copies > 0 ? Loc.F($"已移除 {targets.Count} 帧及其 {copies} 个副本") : Loc.F($"已移除 {targets.Count} 帧"))
            : (copies > 0 ? Loc.F($"已移除该帧及其 {copies} 个副本") : Loc.T("已从卷中移除该帧"));
        ClearStripSelection();
    }

    /// <summary>
    /// The one place an <see cref="ExportOptions"/> becomes a file. Single-frame, roll and any
    /// later export path go through here so the dialog cannot promise a setting that only some
    /// of them honour.
    /// </summary>
    /// <summary>
    /// The render params for one export: the roll's own, with the intent forced to NONE when this
    /// export was asked to be scene-linear.
    ///
    /// Applied here rather than on the roll so the preview never moves — "linear" describes this
    /// file, not the way the roll is being worked on.
    /// </summary>
    private static FrameParams ForExport(FrameParams p, ExportOptions opt)
    {
        if (!opt.ExportLinear) return p;
        FrameParams q = p.Clone();
        q.OutputIntent = OutputIntent.None;
        return q;
    }

    /// <summary>
    /// The pixels an export renders from, and the params rewritten to match them.
    ///
    /// A split TIFF frame decodes only its margin box — the same box its preview is rendered
    /// from — at full resolution (<see cref="ImageIo.LoadWorkingTiffRegionFull"/>), and its crop
    /// is re-expressed against that box exactly as <see cref="ForPreview"/> does for the editor.
    /// The export therefore frames the picture the way the preview showed it, and holds one
    /// cell of the strip rather than all of it: a whole Flextight strip is 1.5–2.3 GB as float,
    /// and decoding it to export a third of it is what an 8 GB machine could not do. Anything
    /// else — a whole-file frame, a RAW — still takes the full decode, through the single
    /// full-resolution slot when <paramref name="sharedSlot"/> asks for it (the editor's
    /// export → tweak → re-export loop) and transiently otherwise (the roll export, which visits
    /// every frame once).
    /// </summary>
    private (WorkingFrame Working, FrameParams Params) LoadForExport(
        string sourcePath,
        (double X, double Y, double W, double H)? box,
        FrameParams ep,
        ColorPipelineVersion pipelineVersion,
        TiffInputAssumption tiffInputAssumption,
        bool sharedSlot)
    {
        if (box is { } bx
            && ImageIo.LoadWorkingTiffRegionFull(
                sourcePath, bx, pipelineVersion, ColorManagement, tiffInputAssumption) is { } window)
            return (window, ForBox(ep, bx));

        WorkingFrame full = sharedSlot
            ? LoadFullWorking(sourcePath, pipelineVersion, tiffInputAssumption)
            : ImageIo.LoadWorking(sourcePath, pipelineVersion, ColorManagement, tiffInputAssumption);
        return (full, ep);
    }

    /// <summary>
    /// <see cref="ForRegion"/> for params that already carry the frame's LIVE crop and orientation
    /// (<see cref="BuildParams"/>, or a stored frame's own params): the crop, stored against the
    /// whole file, re-expressed against <paramref name="box"/>, the file-space rect that was
    /// decoded. Down to file space, in against the box, back out to oriented space.
    /// </summary>
    private static FrameParams ForBox(FrameParams p, (double X, double Y, double W, double H) box)
    {
        if (p.CropRect is not { } rect) return p;
        p = p.Clone();
        p.CropRect = OrientRect(Relative(UnorientRect(rect, p)!.Value, box), p);
        return p;
    }

    /// <summary>
    /// The SDR base of a gain-map JPEG: the roll's SDR rendition (<see cref="FrameParams.SdrRendition"/>,
    /// D-031) in the base space the export chose.
    ///
    /// The two renditions of a gain-map file are one picture at two headrooms — a reader blends
    /// between them by how much headroom its display has — so they must be members of one
    /// rendering. The extended rendition never runs the print stock (D-021); a base that did
    /// would make every intermediate display show a picture that is half print look and half
    /// analytic shoulder, and an SDR display show a print the HDR display never shows. The base
    /// is therefore the asymptote-1 member of the same shoulder family, bit-identical to the HDR
    /// rendition below the knee, which is also what makes the map nearly empty.
    /// </summary>
    private static FrameParams GainMapBaseParams(FrameParams hdr, ColorSpaceDef baseSpace) =>
        hdr.SdrRendition(baseSpace);

    /// <summary>
    /// Renders and writes one export. A JPEG of an extended render is a gain-map JPEG and needs a
    /// second, SDR render of the same frame; everything else is one render and one write.
    /// Returns what a size-limited JPEG actually settled on, null for every other export.
    /// </summary>
    private JpegIO.JpegFit? RenderAndWriteExport(
        WorkingFrame working,
        FrameParams ep,
        string path,
        ExportOptions opt,
        ColorPipelineVersion pipelineVersion)
    {
        if (opt.Format == ExportFormat.Dng)
        {
            // A DNG is a BOUNDED, linear file, so it starts from the SDR rendition in the roll's
            // output space — on an SDR roll that is the ordinary render (SdrRendition returns the
            // params unchanged), and on an HDR roll it is the same bounded rendition a gain-map
            // JPEG uses for its base. LinearDng then undoes that space's encoding curve; the
            // highlights above diffuse white belong to the float32 master, not here.
            ColorSpaceDef dngSpace = opt.ResolvedColorSpace;
            RenderedFrame positive = Pipeline.Render(
                working, ep.SdrRendition(dngSpace), pipelineVersion, ColorManagement);
            ImageBuffer pixels = opt.Downsample
                ? Resample.ToLongEdge(positive.Pixels, opt.MaxLongEdge, opt.AllowUpscale)
                : positive.Pixels;
            LinearDng.Write(pixels, dngSpace, path);
            return null;
        }

        RenderedFrame rendered = Pipeline.Render(working, ep, pipelineVersion, ColorManagement);
        // Decided off the frame the render actually produced rather than the params: a LegacyV1
        // roll ignores its peak and comes back normalized, and then this is an ordinary JPEG.
        bool gainMap = opt.Format == ExportFormat.Jpeg && !opt.ExportLinear
            && rendered.Encoding.Range == NumericRange.Extended;
        if (!gainMap) return WriteExport(rendered, path, opt);

        ColorSpaceDef baseSpace = opt.ResolvedHdrBaseSpace;
        RenderedFrame sdrBase = Pipeline.Render(
            working, GainMapBaseParams(ep, baseSpace), pipelineVersion, ColorManagement);
        if (opt.Downsample)
        {
            // Both layers, with the same call: a gain map is only valid against a base of exactly
            // its own dimensions, and ToLongEdge is a function of the source size alone.
            sdrBase = sdrBase.WithPixels(
                Resample.ToLongEdge(sdrBase.Pixels, opt.MaxLongEdge, opt.AllowUpscale));
            rendered = rendered.WithPixels(
                Resample.ToLongEdge(rendered.Pixels, opt.MaxLongEdge, opt.AllowUpscale));
        }
        if (opt.MaxFileBytes is { } gainMapCeiling)
        {
            return JpegIO.ExportGainMapJpeg(
                sdrBase,
                rendered,
                baseSpace,
                ep.ResolvedOutputTarget,
                path,
                opt.JpegQuality,
                gainMapCeiling,
                profilePolicy: ProfilePolicyFor(opt));
        }
        JpegIO.ExportGainMapJpeg(
            sdrBase,
            rendered,
            baseSpace,
            ep.ResolvedOutputTarget,
            path,
            opt.JpegQuality,
            profilePolicy: ProfilePolicyFor(opt));
        return null;
    }

    /// <summary>
    /// Output sharpening, on a COPY of the render's pixels — the caller's frame may be the retained
    /// preview, and a sharpened preview would be a different picture from the one that was graded.
    ///
    /// Applied here, after the resize and before the encode, because that is what output sharpening
    /// means: the correction is sized to the delivered pixels. Anything the option does not apply to
    /// (see <see cref="ExportOptions.SharpenApplies"/>) passes through untouched.
    /// </summary>
    private static RenderedFrame Sharpened(RenderedFrame output, ExportOptions opt)
    {
        if (!opt.SharpenApplies || opt.Sharpen == OutputSharpen.Level.None) return output;

        ImageBuffer pixels = output.Pixels;
        var copy = new ImageBuffer(pixels.Width, pixels.Height, (float[])pixels.Data.Clone())
            .InheritSourceFrom(pixels);
        OutputSharpen.Apply(copy, opt.Sharpen);
        return output.WithPixels(copy);
    }

    /// <summary>
    /// Scene-linear/extended output is only portable with its exact profile. The old dialog
    /// setting may contain a stale "omit ICC" value from an sRGB export; it must not turn a later
    /// linear or HDR export into an error or an uncharacterized file.
    /// </summary>
    private static ExportProfilePolicy ProfilePolicyFor(ExportOptions opt) =>
        opt.ExportLinear || opt.EmbedIcc || (opt.IsHdr && opt.Format != ExportFormat.Jpeg)
            ? ExportProfilePolicy.EmbedExact
            : ExportProfilePolicy.OmitExactSrgb;

    private static JpegIO.JpegFit? WriteExport(RenderedFrame rendered, string path, ExportOptions opt)
    {
        // Downsample AFTER the render, not before: averaging finished pixels supersamples them,
        // whereas shrinking the negative first would throw away detail the render still needed
        // — and would move every Stage-1 measurement with it.
        RenderedFrame output = opt.Downsample
            ? rendered.WithPixels(Resample.ToLongEdge(rendered.Pixels, opt.MaxLongEdge, opt.AllowUpscale))
            : rendered;
        output = Sharpened(output, opt);
        ExportProfilePolicy profilePolicy = ProfilePolicyFor(opt);

        if (opt.Format == ExportFormat.Jpeg)
        {
            // The size ceiling applies on top of the long-edge one: the encoder starts from the
            // already-shrunk picture and only shrinks further when the floor quality is still over.
            if (opt.MaxFileBytes is { } ceiling)
                return JpegIO.ExportJpeg(
                    output,
                    path,
                    opt.JpegQuality,
                    ceiling,
                    profilePolicy: profilePolicy);
            JpegIO.ExportJpeg(
                output,
                path,
                opt.JpegQuality,
                profilePolicy: profilePolicy);
            return null;
        }
        TiffIO.ExportTiff(
            output,
            path,
            opt.TiffCompression,
            profilePolicy: profilePolicy);
        return null;
    }

    /// <summary>
    /// What the size limit did to one file, for the status bar: nothing to say when the file fit
    /// as asked, otherwise the quality and — when the picture had to shrink — the long edge it
    /// settled on, and the size it landed at.
    /// </summary>
    internal static string DescribeFit(JpegIO.JpegFit? fit)
    {
        if (fit is null || (!fit.QualityReduced && !fit.Shrunk)) return "";
        string mb = $"{fit.Bytes / (1024d * 1024d):0.#} MB";
        return fit.Shrunk
            ? " · " + Loc.F($"实际 {mb}（品质 {fit.Quality}，长边 {fit.LongEdge}px）")
            : " · " + Loc.F($"实际 {mb}（品质 {fit.Quality}）");
    }

    /// <summary>
    /// What a filename template may spend on one frame: the roll's own fields as they stand right
    /// now (<see cref="Notes"/>, not the last saved project — the person may have just typed the
    /// film stock), plus the two things that vary per frame.
    /// </summary>
    /// <param name="sequence">1-based position in the roll, as the film strip shows it.</param>
    public ExportNaming.Fields ExportNameFields(RollFrame frame, int sequence) => new(
        Roll: _roll?.Title ?? "",
        RollNumber: Notes.RollNumber,
        Camera: Notes.CameraBody,
        Film: Notes.FilmStock,
        Date: Notes.DevDate,
        Original: Path.GetFileNameWithoutExtension(frame.Path),
        Sequence: sequence);

    /// <summary>
    /// The name a SINGLE-frame export should be offered in its save dialog. The template is a roll
    /// export's rule, but suggesting the same name here is what makes one frame exported by hand sit
    /// next to the batch instead of standing out — and the dialog still lets it be changed.
    /// </summary>
    public string SuggestedExportName(ExportOptions opt) => ExportNamePreview(opt.NameTemplate);

    /// <summary>
    /// A template expanded against a real frame — the current one, or the roll's first when nothing
    /// is open. This is what the export dialog's live preview shows: a template is only checkable
    /// against its own output.
    /// </summary>
    public string ExportNamePreview(string template)
    {
        RollFrame? frame = CurrentFrame ?? Frames.FirstOrDefault();
        if (frame is null)
            return ExportNaming.Expand(template, new ExportNaming.Fields(
                Roll: "", RollNumber: "", Camera: "", Film: "", Date: "",
                Original: "positive", Sequence: 1));
        return ExportNaming.Expand(template, ExportNameFields(frame, Math.Max(1, Frames.IndexOf(frame) + 1)));
    }

    /// <summary>Export every frame at full resolution into a folder, each with its own params.</summary>
    public async Task ExportRollAsync(string folder, ExportOptions opt)
    {
        if (Frames.Count == 0) return;
        CommitLiveParams(CurrentFrame);   // capture live edits
        IsBusy = true;
        try
        {
            var frames = Frames.ToList();
            ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
            TiffInputAssumption tiffInputAssumption = _tiffInputAssumption;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reserved = ExportFile.NewReservations();
            string extension = opt.Extension;
            ExportFile.CleanupStale(folder);
            int renamed = 0, skipped = 0, qualityReduced = 0, shrunk = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                RollFrame f = frames[i];
                StatusText = Loc.F($"导出 {i + 1}/{frames.Count}：{f.FileName} …");
                FrameParams p = f.Params;
                // Virtual copies share the source file name — disambiguate so they don't overwrite.
                // (They also share every OTHER field a template can name, so this stays necessary
                // whatever the template is.)
                string baseName = ExportNaming.Expand(opt.NameTemplate, ExportNameFields(f, i + 1));
                string name = baseName;
                for (int dup = 2; !usedNames.Add(name); dup++) name = Loc.F($"{baseName}_副本{dup - 1}");
                // A roll export names its files after the SCANS and runs unattended, so the folder
                // may already hold an earlier export or unrelated matching files. What happens
                // then is the user's call, made in the export dialog — the default being the one
                // that cannot destroy anything.
                string? outPath = ExportFile.Reserve(folder, name, extension, opt.Conflict, reserved);
                if (outPath is null) { skipped++; continue; }
                if (!string.Equals(Path.GetFileNameWithoutExtension(outPath), name, StringComparison.Ordinal))
                    renamed++;
                FrameParams ep = ForExport(p, opt);
                var exportBox = SplitCropOf(f);
                JpegIO.JpegFit? fit = await Task.Run(() =>
                {
                    var (working, boxed) = LoadForExport(
                        f.Path, exportBox, ep, pipelineVersion, tiffInputAssumption, sharedSlot: false);
                    return RenderAndWriteExport(working, boxed, outPath, opt, pipelineVersion);
                });
                if (fit is { Shrunk: true }) shrunk++;
                else if (fit is { QualityReduced: true }) qualityReduced++;
            }
            string detail = "";
            if (renamed > 0) detail += Loc.F($"，其中 {renamed} 帧重名已另存");
            if (skipped > 0) detail += Loc.F($"，跳过 {skipped} 帧同名");
            if (qualityReduced > 0) detail += Loc.F($"，{qualityReduced} 帧为守住大小降了品质");
            if (shrunk > 0) detail += Loc.F($"，{shrunk} 帧为守住大小缩了尺寸");
            StatusText = Loc.F($"整卷导出完成（{frames.Count - skipped}/{frames.Count} 帧{detail}）· {opt.Summary()} → {folder}");
        }
        catch (Exception ex) { StatusText = Loc.T("整卷导出失败：") + ex.Message; }
        finally { IsBusy = false; ReleaseBulkBuffers(); }
    }

    /// <summary>
    /// The thumbnails a contact sheet is laid out from. <see cref="Sdr"/> is what every sheet is
    /// composed and previewed from — the roll's SDR rendition (D-031). <see cref="Extended"/> is
    /// the same frames rendered to the roll's HDR target, in the linear extended carrier, and is
    /// null on an SDR roll; it exists so the export can write the sheet as a gain-map JPEG or a
    /// float32 TIFF whose frames keep the highlights the preview shows. <see cref="Target"/> is
    /// the HDR target they were rendered for.
    /// </summary>
    public sealed record ContactThumbs(
        IReadOnlyList<ImageBuffer> Sdr,
        IReadOnlyList<ImageBuffer>? Extended,
        OutputTarget Target)
    {
        /// <summary>Whether an HDR sheet can be written from these.</summary>
        public bool HasExtended => Extended is not null;
    }

    /// <summary>
    /// Process every frame down to a contact-sheet thumbnail. Returns null if there is nothing to
    /// build. Deliberately stops at the thumbnails rather than the finished sheet: this is the
    /// expensive half (a pass over the whole roll), while laying them out and printing the
    /// surround is cheap — so restyling the sheet in the dialog must not come back through here.
    ///
    /// On an HDR roll the pass renders each frame twice — SDR rendition and extended — so the
    /// dialog's HDR switch is a choice about the FILE and never sends the roll back through here.
    /// </summary>
    public async Task<ContactThumbs?> BuildContactThumbsAsync()
    {
        if (Frames.Count == 0) return null;
        CommitLiveParams(CurrentFrame);
        IsBusy = true;
        StatusText = Loc.T("正在生成印样 …");
        try
        {
            var frames = Frames.ToList();
            int done = 0, total = frames.Count;
            var sources = new WorkingFrame[total];
            var cellParams = new FrameParams[total];
            ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
            TiffInputAssumption tiffInputAssumption = _tiffInputAssumption;
            // Warm previews first, on the shared decode path — this used to re-decode the entire
            // roll at full resolution just to shrink each frame to 900 px.
            for (int i = 0; i < total; i++)
            {
                // Each frame's OWN region: on a split scan the bare path would give every cell the
                // strip's first negative. The region is the frame plus its margin, so the crop
                // still runs below — against the box rather than the whole scan.
                var pre = SplitCropOf(frames[i]);
                sources[i] = (await PreviewAsync(
                    frames[i].Path,
                    pre,
                    pipelineVersion,
                    tiffInputAssumption)).Working;
                cellParams[i] = ForRegion(frames[i].Params, frames[i], pre);
                ReportBackground(Loc.F($"印样 {++done}/{total} …"));
            }

            // The roll's target, off the params the cells are rendered with. A LegacyV1 roll
            // ignores its peak (D-013) and would come back normalized, so it gets no extended set.
            OutputTarget target = cellParams[0].ResolvedOutputTarget;
            bool extended = target.IsExtended && pipelineVersion == ColorPipelineVersion.ManagedV2;
            ContactThumbs thumbs = await Task.Run(() =>
            {
                var sdr = new List<ImageBuffer>(total);
                var hdr = extended ? new List<ImageBuffer>(total) : null;
                for (int i = 0; i < total; i++)
                {
                    WorkingFrame source = sources[i].WithPixels(
                        Resample.Box(sources[i].Pixels, 900));
                    sdr.Add(Pipeline.Render(
                        source,
                        cellParams[i].SdrRendition(),
                        pipelineVersion,
                        ColorManagement).Pixels);
                    hdr?.Add(Pipeline.Render(
                        source,
                        cellParams[i],
                        pipelineVersion,
                        ColorManagement).Pixels);
                }
                return new ContactThumbs(sdr, hdr, target);
            });
            StatusText = Loc.F($"印样已生成（{total} 帧）");
            return thumbs;
        }
        catch (Exception ex) { StatusText = Loc.T("印样生成失败：") + ex.Message; return null; }
        finally { ReportBackground(""); IsBusy = false; ReleaseBulkBuffers(); }
    }

    /// <summary>Compose the finished sheet at export resolution and write it. Must be called from
    /// the UI thread: the surround goes through Avalonia's rasteriser. Only the grid pass and the
    /// encode move to a worker.</summary>
    /// <param name="hdr">Write the HDR sheet (D-031): the same page with the frames at the roll's
    /// HDR target — a gain-map JPEG whose base is the SDR sheet, or a float32 TIFF. Ignored when
    /// <paramref name="thumbs"/> carries no extended set.</param>
    public async Task ExportContactSheetAsync(ContactThumbs thumbs, bool hdr, SheetStyle style,
                                              SheetAspect aspect, SheetOrientation orient,
                                              string path)
    {
        IsBusy = true;
        StatusText = Loc.T("正在导出印样 …");
        try
        {
            var opt = new SheetComposer.Options { Style = style, Aspect = aspect, Orientation = orient };
            // Laid out at full width so the header, frame numbers and strip are rendered at
            // export resolution rather than upscaled from the dialog's cheap preview.
            SheetComposer.Grid grid = await Task.Run(
                () => SheetComposer.BuildGrid(thumbs.Sdr, maxLong: 2048, opt));

            using RenderTargetBitmap composed = SheetComposer.Compose(grid, Notes, opt);
            ImageBuffer outImg = SheetComposer.ToBuffer(composed);
            // The page is composed ONCE, from the SDR cells: the HDR sheet is that page taken
            // back to linear with the frames swapped for their extended renders at the same
            // geometry, so the two files (or the two halves of one gain-map file) are one sheet.
            IReadOnlyList<ImageBuffer>? extendedCells = hdr ? thumbs.Extended : null;
            (int gridX, int gridY) = SheetComposer.GridOrigin(grid.Layout, opt);

            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } outDir) ExportFile.CleanupStale(outDir);
            await Task.Run(() =>
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool jpeg = ext is ".jpg" or ".jpeg";
                RenderedFrame rendered = ContactSheetRenderedFrame(outImg);
                if (extendedCells is null)
                {
                    if (jpeg) JpegIO.ExportJpeg(rendered, path, quality: 92);
                    else TiffIO.ExportTiff(rendered, path, TiffIO.CompressionMode.Lzw);
                    return;
                }
                RenderedFrame extendedSheet = ExtendedContactSheetRenderedFrame(
                    ContactSheet.WithExtendedCells(
                        outImg, ColorSpaces.Srgb, extendedCells, grid.Layout, gridX, gridY),
                    thumbs.Target);
                if (jpeg)
                    JpegIO.ExportGainMapJpeg(rendered, extendedSheet, ColorSpaces.Srgb, thumbs.Target, path, quality: 92);
                else
                    TiffIO.ExportTiffFloat32(extendedSheet, path, TiffIO.CompressionMode.Lzw);
            });
            StatusText = extendedCells is null
                ? Loc.F($"印样已导出：{Path.GetFileName(path)}（{outImg.Width}×{outImg.Height}）")
                : Loc.F($"HDR 印样已导出：{Path.GetFileName(path)}（{outImg.Width}×{outImg.Height}）");
        }
        catch (Exception ex) { StatusText = Loc.T("印样导出失败：") + ex.Message; }
        finally { IsBusy = false; }
    }

    private static RenderedFrame ContactSheetRenderedFrame(ImageBuffer pixels)
    {
        ColorProfileRef profile = BuiltInColorProfiles.Srgb(ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(
            profile,
            ColorReference.DisplayReferred,
            TransferState.ProfileEncoded,
            NumericRange.Normalized);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            profile,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            "contact sheet fixed exact sRGB",
            printLutIdentity: string.Empty,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    /// <summary>The HDR sheet, typed the way an extended frame render is (linear in the canonical
    /// carrier, unbounded above) so the gain-map and float32 writers accept it as one.</summary>
    private static RenderedFrame ExtendedContactSheetRenderedFrame(ImageBuffer pixels, OutputTarget target)
    {
        ColorProfileRef profile = BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(
            profile,
            ColorReference.SceneReferred,
            TransferState.LinearInProfilePrimaries,
            NumericRange.Extended);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            profile,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            $"contact sheet: frames scene-referred extended to {target.HighlightHeadroom:0.###}× diffuse white, surround pinned at diffuse white",
            printLutIdentity: string.Empty,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    /// <summary>The current frame at full resolution, decoded on demand. Nothing else in the GUI
    /// needs full-res, so this is the only place it is paid for; the single slot means an
    /// export → tweak → re-export loop on the same frame decodes once, and switching frames
    /// releases the buffer instead of accumulating hundreds of MB per visited frame.</summary>
    private WorkingFrame LoadFullWorking(
        string sourcePath,
        ColorPipelineVersion pipelineVersion,
        TiffInputAssumption tiffInputAssumption)
    {
        lock (_fullSlotGate)
        {
            if (_fullSlot is { } slot
                && slot.PipelineVersion == pipelineVersion
                && slot.TiffInputAssumption == tiffInputAssumption
                && string.Equals(slot.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
                return slot.Working;
            WorkingFrame full = ImageIo.LoadWorking(
                sourcePath,
                pipelineVersion,
                ColorManagement,
                tiffInputAssumption);
            _fullSlot = new FullSlot(sourcePath, pipelineVersion, tiffInputAssumption, full);
            return full;
        }
    }

    /// <summary>
    /// Serialises the check-then-decode above. It used to be a bare read/write, which was fine
    /// while only the export path called it — one export at a time, on one worker. The sharp
    /// patch made it reachable from several workers at once, and two threads that both miss the
    /// slot both decode: at ~690 MB for a 60 MP frame, a handful of overlapping requests is
    /// gigabytes. The lock holds across the decode on purpose — the second caller is meant to
    /// WAIT for the first one's buffer, not start a second decode of the same file.
    /// </summary>
    private readonly object _fullSlotGate = new();

}
