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
            OnPropertyChanged(nameof(CanChooseOutputSpaceAndPrintLut));
            // Said at the moment it starts mattering, not left for the greyed controls to imply:
            // the extended render keeps neither the roll's output space (D-024) nor its print
            // stock (D-021), so a roll that had either set changes more than its highlights.
            if (value && (_outputSpaceIndex != 0 || _printLutIndex != 0))
                StatusText = Loc.T("HDR 开启：输出空间与胶片风格不参与扩展渲染（载体恒为线性扩展 sRGB，印片 LUT 不走）；关闭 HDR 即恢复。");
            ApplyHdrToRoll(rebuildThumbnailsNow: true);
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
            if (_hdrEnabled) ApplyHdrToRoll(rebuildThumbnailsNow: false);
            else NotifyHdrText();
        }
    }

    /// <summary>
    /// Writes the roll's peak onto every frame and re-renders. Thumbnails are rebuilt — after a
    /// pause when the change came from the slider, which fires on every tick of a drag and would
    /// otherwise restart the whole roll's decode pass a dozen times per second.
    /// </summary>
    private void ApplyHdrToRoll(bool rebuildThumbnailsNow)
    {
        double nits = HdrPeakNits;
        foreach (RollFrame f in Frames) f.Params.HdrPeakNits = nits;
        if (Frames.Count > 0) MarkRollDirty();
        NotifyHdrText();
        ScheduleRender();
        if (rebuildThumbnailsNow) RebuildThumbnails();
        else ScheduleThumbnailRebuild();
    }

    private void RebuildThumbnails()
    {
        _thumbRebuildCts?.Cancel();
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
    }

    private CancellationTokenSource? _thumbRebuildCts;

    private async void ScheduleThumbnailRebuild()
    {
        _thumbRebuildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbRebuildCts = cts;
        try
        {
            await Task.Delay(500, cts.Token);
            foreach (RollFrame f in Frames) SetThumbnail(f, null);
            RestartThumbnails();
        }
        catch (OperationCanceledException) { }
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
    /// (D-024) and it does not run the print LUT (D-021), so the two pickers would change
    /// nothing; they are greyed rather than left to look effective, and the reason is stated in
    /// the toggle's tooltip, the hint under the slider and the status bar at the moment of
    /// switching — a disabled control shows no tooltip of its own.
    /// </summary>
    public bool CanChooseOutputSpaceAndPrintLut => !_hdrEnabled;

    /// <summary>The toggle's hover text: what the control is, then where the roll stands on this display.</summary>
    public string HdrToggleTooltip =>
        Loc.T("关：印相渲染，高光收进纸白，一直以来的行为；不确定就关。开：纸白之上的宽容度铺到直方图下方【HDR 上限】所设的档数，导出随之。预览在本机余量内自动软校样。")
        + "\n" + Loc.T("开启时【输出空间】与【胶片风格】不参与：扩展渲染的载体恒为线性扩展 sRGB（D-024），印片 LUT 是把高光收进纸白的显示参考表，与 HDR 互斥（D-021）。两个下拉随之变灰，关闭 HDR 即恢复。")
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
    /// THE D-023 RESTRICTION IS STATED HERE RATHER THAN DISCOVERED AT RENDER TIME. An extended
    /// render refuses display-referred Stage 2 adjustments instead of silently dropping them, and
    /// <c>ReportRenderFailure</c> would catch that — but a failure message after the fact is a
    /// worse way to learn a rule than the control that sets it saying so.
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

            string blocked = CurrentFrame is { } frame &&
                             Stage2.HasDisplayReferredAdjustments(frame.Params)
                ? Loc.T("当前有色阶／对比度／高光阴影／曲线／饱和度的调整，HDR 渲染会拒绝——请先把它们复位。")
                : string.Empty;

            string ignored = _outputSpaceIndex != 0 || _printLutIndex != 0
                ? Loc.T("本卷选的输出空间／胶片风格在 HDR 下不参与渲染（D-024 / D-021）。")
                : string.Empty;

            return DescribeDisplayFit(_hdrLimitStops)
                + (ignored.Length == 0 ? string.Empty : "\n" + ignored)
                + (blocked.Length == 0 ? string.Empty : "\n" + blocked);
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
        OnPropertyChanged(nameof(CanChooseOutputSpaceAndPrintLut));
        OnPropertyChanged(nameof(HdrLimitStops));
        NotifyHdrText();
    }

    // ══ 胶片风格（印片 LUT） ═══════════════════════════════════════════════════
    //
    // 与【输出空间】并列而不是并入其中，因为两者正交：LUT 决定画面被渲染成什么样，输出空间
    // 决定它被装进哪个容器。曾经把 "Kodak2383" 当成一个 ColorSpaceDef 塞进输出空间下拉——
    // 那是类型错误，三个色度坐标表达不了一张印片的响应，后来删掉了。
    //
    // 内置两张印片（Kodak 2383、Fujifilm 3513DI），嵌在程序集里，见 PrintLuts.Builtins；
    // 工程存的是 :kodak-2383 这样的记号而不是本机路径，所以换机器打开照样渲染。用户自选的
    // cube 仍按路径存，下拉里的名字取自各自文件的 TITLE。

    /// <summary>Cubes the picker offers, in order:
    /// 标准渲染 → 内置印片 → LUT 文件夹里的 → 最近用过的 → 选择文件…</summary>
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

    /// <summary>
    /// Writes the chosen cube to every frame, rebases each frame's Stage-2 adjustments for the new
    /// rendering, and re-renders the roll.
    ///
    /// WHY THE SCENE IS NOT CARRIED OVER. Stage 2 runs AFTER the display rendering, on top of
    /// whatever the standard conversion or the cube produced, so its numbers are relative to THAT
    /// render's zero. Carrying them across a look change applies a correction fitted to a picture
    /// that is no longer on screen: the controls keep their values while silently changing
    /// meaning, which is the worst of both.
    ///
    /// WHY NO PATH GETS AUTO-LEVELS. Every rendering places its own ends, so measuring the result
    /// and stretching it back to 0..1 overrides the very thing the user selected:
    ///
    ///   • The standard rendering normalises code 95 to display black and rolls the latitude above
    ///     685 off toward white. Its black end is already 0, so the black slider always solved to
    ///     0 and only the white slider moved — pushing the highlights the shoulder had just rolled
    ///     off back up against the clip.
    ///   • A print stock's ends are its OWN and deliberately not 0 and 1 — measured on Kodak 2383,
    ///     code 685 renders at 0.880 and code 95 at 0.037. That toe and shoulder ARE the film
    ///     look; at a 99.9th percentile of 0.70 a levels stretch is a 1.43× gain that flattens it.
    ///   • The pure CST renders no look at all, which is its entire point; normalising it would be
    ///     a display decision smuggled into the one path defined by making none.
    ///
    /// So levels stay neutral everywhere. The CONTROLS stay available — the 自动色阶 button and
    /// the sliders both work — because a scan whose highlight never reaches the shoulder has a
    /// real gap that levels is the right tool to close. What this decides is only the DEFAULT.
    ///
    /// Everything the user dialled in by eye — exposure, contrast, hi/sh, curves, saturation, WB —
    /// returns to neutral on both paths, because there is nothing to re-derive it from.
    ///
    /// The calibration is untouched throughout: it describes the NEGATIVE (t_base, D_min, D_max)
    /// and is independent of which stock renders it.
    ///
    /// Undo covers the whole thing: the roll's params are snapshotted before the rebase, so an
    /// accidental switch is one Ctrl+Z away rather than a lost grade.
    /// </summary>
    private void ApplyPrintLut(string path)
    {
        CommitLiveParams(CurrentFrame);   // fold the live sliders in before they are discarded
        CommitUndo();                     // the rebase below is destructive; make it undoable

        foreach (RollFrame f in Frames)
        {
            f.Params.PrintLut = path;
            RollFrame.ResetScene(f.Params);
        }
        if (Frames.Count > 0) MarkRollDirty();

        // Push the neutralised params into the sliders before re-measuring: AutoLevels renders
        // through BuildParams(), so the controls must already describe the new look or it would
        // measure the outgoing one.
        if (CurrentFrame is { } cur) LoadParams(cur.Params);

        // NO auto-levels on any path. Every rendering here places its own ends — the standard
        // one normalises code 95 to black and rolls off above 685, a cube has its own toe and
        // shoulder, the pure CST deliberately renders none — so measuring the result and
        // stretching it back to 0..1 overrides whichever one the user just chose. ResetScene has
        // already left levels neutral; they stay that way until the user asks otherwise.

        OnPropertyChanged(nameof(PrintLutIndex));
        OnPropertyChanged(nameof(PrintLutHint));

        // Thumbnails change too — this alters what each frame IS, not how it is shown.
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
        ScheduleRender();
    }

    /// <summary>Rebuilds the picker from settings, selecting <paramref name="active"/>.</summary>
    private void RebuildPrintLutList(string active)
    {
        // Selecting an EXISTING entry must not touch the collection. Clearing an ObservableCollection
        // that a ComboBox is bound to drives its SelectedIndex to -1, and -1 renders as an empty
        // box — so rebuilding on every frame load blanked the picker even though the roll's LUT
        // was unchanged and still rendering. Frame switches are the common case and they never
        // change the list, only which row is current.
        // Row 0 is the standard display rendering, which "" already selects above, so a path
        // lookup starts at 1 — that includes the BUILT-IN rows, whose sentinels are perfectly
        // findable paths. The trailing "choose a file" verb is excluded as before.
        //
        int existing = _printLutPaths.Count == 0 ? -1
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

        PrintLutNames.Clear();
        _printLutPaths.Clear();

        PrintLutNames.Add(Loc.T("标准显示渲染（CST + 显示渲染）"));
        _printLutPaths.Add("");

        // The built-in stocks, always offered and never "文件缺失": they ship inside the assembly,
        // so unlike a recent path they cannot go missing between sessions.
        foreach ((string id, string name) in PrintLuts.Builtins)
        {
            PrintLutNames.Add(name);
            _printLutPaths.Add(id);
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
        var paths = new List<string>(PrintLuts.InFolder(Settings.LutDir));

        // A roll can still name a cube that is in neither place — a project from another machine,
        // or a file picked before it was moved. It goes in at the top so the picker shows what the
        // render is actually using rather than 无, and it is the only path here not from the
        // folder.
        if (!string.IsNullOrWhiteSpace(active)
            && !PrintLuts.IsBuiltin(active)          // already a fixed row above
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
            PrintLutNames.Add(label);
            _printLutPaths.Add(p);
        }

        PrintLutNames.Add(Loc.T("选择 .cube 文件…"));
        _printLutPaths.Add("");

        int i = _printLutPaths.FindIndex(1, _printLutPaths.Count - 2,
                                         p => p.Equals(active, StringComparison.OrdinalIgnoreCase));
        _printLutIndex = string.IsNullOrWhiteSpace(active) ? 0 : (i < 0 ? 0 : i);

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

    /// <summary>Adopt a roll's saved cube into the picker. Loading, not choosing — not dirty.</summary>
    private void SyncPrintLut(string path) => RebuildPrintLutList(path ?? "");

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

        // The managed pipeline cannot render through a cube whose output space is unproven, and
        // the render path's way of saying so is an exception three layers down that surfaces as a
        // status line after the picture has already gone stale. Say it here instead, where the
        // person who chose the file is standing, and do not adopt a cube that cannot be used.
        if (_colorPipelineVersion == ColorPipelineVersion.ManagedV2
            && lut.OutputEncoding == LutOutputEncoding.Unknown)
        {
            StatusText =
                Loc.F($"「{lut.Title}」的文件头没有声明输出色彩空间，色彩管理版没有采用它。")
                + Loc.T("Resolve 导出的胶片 LUT 会在头部写明「Display: ITU-Rec.709, Gamma 2.4」；若这个文件的注释被删过，用原始导出重试，否则请改用内置的胶片风格。");
            return;
        }

        StatusText = Loc.F($"已载入胶片风格：{lut.Title}（{lut.Size}³）。");

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

    /// <summary>Create a virtual copy of the current frame (inserted right after it) and select it.</summary>
    public void CreateVirtualCopyOfCurrent()
    {
        if (CurrentFrame is not { } parent) return;
        if (parent.IsVirtual) { StatusText = Loc.T("只能对真实帧创建副本（当前已是副本）"); return; }

        CommitUndo();
        CommitLiveParams(parent);   // capture live edits into the parent first
        RollFrame copy = RollFrame.MakeVirtualCopy(parent);
        int pos = Frames.IndexOf(parent) + 1;
        Frames.Insert(pos, copy);
        ResetUndoAfterStructural();
        CurrentFrame = copy;             // switch to the copy so it can be adjusted immediately
        StatusText = Loc.T("已创建虚拟副本（继承标定、场景已重置）");
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

    /// <summary>Remove the current frame. Removing a real frame also drops every virtual copy of it.</summary>
    public void RemoveCurrentFrame()
    {
        if (CurrentFrame is not { } target) return;
        if (Frames.Count <= 1) { StatusText = Loc.T("至少保留一帧，无法移除"); return; }

        // Collect victims: the target, plus (if it's a real frame) all its virtual copies.
        var victims = new HashSet<RollFrame> { target };
        if (!target.IsVirtual)
            foreach (RollFrame f in Frames)
                if (f.IsVirtual && string.Equals(f.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                    victims.Add(f);

        int targetIdx = Frames.IndexOf(target);
        _prevFrame = null;               // don't persist the frame we're deleting on the coming switch
        for (int i = Frames.Count - 1; i >= 0; i--)
            if (victims.Contains(Frames[i])) { Retire(Frames[i].Thumbnail); Frames.RemoveAt(i); }

        ResetUndoAfterStructural();
        CurrentFrame = Frames[Math.Clamp(targetIdx, 0, Frames.Count - 1)];
        StatusText = victims.Count > 1
            ? Loc.F($"已移除该帧及其 {victims.Count - 1} 个副本")
            : Loc.T("已从卷中移除该帧");
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

    private static void WriteExport(RenderedFrame rendered, string path, ExportOptions opt)
    {
        // Downsample AFTER the render, not before: averaging finished pixels supersamples them,
        // whereas shrinking the negative first would throw away detail the render still needed
        // — and would move every Stage-1 measurement with it.
        RenderedFrame output = opt.Downsample
            ? rendered.WithPixels(Resample.Box(rendered.Pixels, opt.MaxLongEdge))
            : rendered;
        // Scene-linear/extended output is only portable with its exact linear ACEScg profile.
        // The old dialog setting may contain a stale "omit ICC" value from an sRGB export; it
        // must not turn a later linear export into an error or an uncharacterized file.
        ExportProfilePolicy profilePolicy = opt.ExportLinear || opt.EmbedIcc
            ? ExportProfilePolicy.EmbedExact
            : ExportProfilePolicy.OmitExactSrgb;

        if (opt.Format == ExportFormat.Jpeg)
            JpegIO.ExportJpeg(
                output,
                path,
                opt.JpegQuality,
                profilePolicy: profilePolicy);
        else
            TiffIO.ExportTiff(
                output,
                path,
                opt.TiffCompression,
                profilePolicy: profilePolicy);
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
            int renamed = 0, skipped = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                RollFrame f = frames[i];
                StatusText = Loc.F($"导出 {i + 1}/{frames.Count}：{f.FileName} …");
                FrameParams p = f.Params;
                // Virtual copies share the source file name — disambiguate so they don't overwrite.
                string baseName = Path.GetFileNameWithoutExtension(f.Path);
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
                await Task.Run(() =>
                {
                    WorkingFrame working = ImageIo.LoadWorking(
                        f.Path,
                        pipelineVersion,
                        ColorManagement,
                        tiffInputAssumption);
                    RenderedFrame rendered = Pipeline.Render(
                        working,
                        ep,
                        pipelineVersion,
                        ColorManagement);
                    WriteExport(rendered, outPath, opt);
                });
            }
            string detail = "";
            if (renamed > 0) detail += Loc.F($"，其中 {renamed} 帧重名已另存");
            if (skipped > 0) detail += Loc.F($"，跳过 {skipped} 帧同名");
            StatusText = Loc.F($"整卷导出完成（{frames.Count - skipped}/{frames.Count} 帧{detail}）· {opt.Summary()} → {folder}");
        }
        catch (Exception ex) { StatusText = Loc.T("整卷导出失败：") + ex.Message; }
        finally { IsBusy = false; ReleaseBulkBuffers(); }
    }

    /// <summary>
    /// Process every frame down to a contact-sheet thumbnail. Returns null if there is nothing to
    /// build. Deliberately stops at the thumbnails rather than the finished sheet: this is the
    /// expensive half (a pass over the whole roll), while laying them out and printing the
    /// surround is cheap — so restyling the sheet in the dialog must not come back through here.
    /// </summary>
    public async Task<IReadOnlyList<ImageBuffer>?> BuildContactThumbsAsync()
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

            List<ImageBuffer> thumbs = await Task.Run(() =>
            {
                var t = new List<ImageBuffer>(total);
                for (int i = 0; i < total; i++)
                {
                    WorkingFrame source = sources[i].WithPixels(
                        Resample.Box(sources[i].Pixels, 900));
                    t.Add(Pipeline.Render(
                        source,
                        cellParams[i],
                        pipelineVersion,
                        ColorManagement).Pixels);
                }
                return t;
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
    public async Task ExportContactSheetAsync(IReadOnlyList<ImageBuffer> thumbs, SheetStyle style,
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
                () => SheetComposer.BuildGrid(thumbs, maxLong: 2048, opt));

            using RenderTargetBitmap composed = SheetComposer.Compose(grid, Notes, opt);
            ImageBuffer outImg = SheetComposer.ToBuffer(composed);

            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } outDir) ExportFile.CleanupStale(outDir);
            await Task.Run(() =>
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                RenderedFrame rendered = ContactSheetRenderedFrame(outImg);
                if (ext is ".jpg" or ".jpeg")
                    JpegIO.ExportJpeg(rendered, path, quality: 92);
                else
                    TiffIO.ExportTiff(rendered, path, TiffIO.CompressionMode.Lzw);
            });
            StatusText = Loc.F($"印样已导出：{Path.GetFileName(path)}（{outImg.Width}×{outImg.Height}）");
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
