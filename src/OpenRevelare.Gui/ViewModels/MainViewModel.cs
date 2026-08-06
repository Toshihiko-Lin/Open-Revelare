using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// Single-frame workflow: import a RAW/TIFF negative, calibrate the density-domain
/// inversion (Stage 1: film base / WB / d_max / grade), adjust the positive
/// (Stage 2: WB / exposure / tone / levels), and export.
///
/// Control mappings mirror Python's <c>gui/frame_edit_panel.py</c> +
/// <c>gui/roll_cal_panel.py</c> exactly (see <see cref="WbMath"/>): 色温/色调 are
/// log-space geomean-1 gains, 黑场/白场 are symmetric ±1 sliders, 反差 has paper-grade
/// presets with a d_max-linked pivot, and Stage-1 偏移 (scan_ev) is separate from
/// Stage-2 曝光 (exposure_ev). Rendering is debounced and off the UI thread.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const int PreviewMaxEdge = 1600;

    private ImageBuffer? _previewLinear;   // downsampled linear negative for the CURRENT frame
    private CancellationTokenSource? _renderCts;

    /// <summary>Decoded previews for the whole roll, keyed by source path — a frame switch is a
    /// dictionary lookup, not a decode. See <see cref="PreviewCache"/> for the memory model.</summary>
    private readonly PreviewCache _previews = new();

    /// <summary>In-flight decodes, keyed by path. A frame switch and the roll warm-up routinely
    /// want the same file at the same instant (the roll warms from the current frame outward, and
    /// that is exactly the frame being switched to); without this they would each decode it.</summary>
    private readonly Dictionary<string, Task<PreviewCache.Entry>> _decoding = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The preview for <paramref name="path"/> — cached, already being decoded by someone
    /// else, or decoded now. Deliberately NOT cancellable: if the switch that asked for it is
    /// superseded, the warm-up still wants the result, and throwing it away would mean decoding
    /// the same file again a moment later.</summary>
    private Task<PreviewCache.Entry> PreviewAsync(string path)
    {
        if (_previews.Get(path) is { } hit) { CaptureTile(path, hit.Preview); return Task.FromResult(hit); }
        lock (_decoding)
        {
            if (_decoding.TryGetValue(path, out Task<PreviewCache.Entry>? running)) return running;
            Task<PreviewCache.Entry> task = Task.Run(() =>
            {
                // Straight to preview size: the full-resolution float frame this used to decode
                // and immediately throw away is the biggest allocation in the program.
                var (preview, srcW, srcH) = ImageIo.LoadPreview(path, PreviewMaxEdge);
                var e = new PreviewCache.Entry(preview, srcW, srcH);
                _previews.Put(path, e.Preview, e.SourceWidth, e.SourceHeight);
                CaptureTile(path, e.Preview);
                return e;
            });
            _decoding[path] = task;
            _ = task.ContinueWith(_ => { lock (_decoding) _decoding.Remove(path); },
                                  TaskScheduler.Default);
            return task;
        }
    }

    // ── Sheet tiles: one small LINEAR negative per source file, resident for the whole roll ─────
    //
    // The roll's cover contact sheet has to stay current as frames are edited, and re-deriving it
    // from the preview cache would not do: previews are ~20 MB each and get evicted, so a sheet
    // rebuild after a long session would re-decode the roll (60 MP × 36 ≈ 28 s). These tiles are
    // ~0.8 MB each — a 36-frame roll is ~29 MB, which is nothing against the preview budget — so
    // they simply stay for as long as the roll is open, and ANY parameter change re-renders the
    // affected cells with zero decoding. The film strip's thumbnails are re-rendered from them
    // too, which is what stops 「应用标定到整卷」 from triggering a decode pass.
    //
    // LINEAR negatives, deliberately: the pipeline still has to run per frame, because that is
    // what a params change changes. Keyed by source path, so virtual copies share one tile.
    private const int TileMaxEdge = 320;   // ≈ the cell width of a 2048 px sheet at 6 columns
    private readonly Dictionary<string, ImageBuffer> _tiles = new(StringComparer.OrdinalIgnoreCase);

    private void CaptureTile(string path, ImageBuffer preview)
    {
        lock (_tiles)
        {
            if (_tiles.ContainsKey(path)) return;
            _tiles[path] = Resample.Box(preview, TileMaxEdge);
        }
    }

    private ImageBuffer? TileFor(string path)
    {
        lock (_tiles) return _tiles.TryGetValue(path, out ImageBuffer? t) ? t : null;
    }

    private void ClearTiles() { lock (_tiles) _tiles.Clear(); }

    /// <summary>Single-slot full-resolution buffer, kept ONLY between the decode and the export
    /// that asked for it. Full-res is ~288 MB for 24 MP, so it is decoded lazily (export path
    /// only) and never held for the roll — the same rule the Python GUI states for _hires_current.</summary>
    /// <remarks>A record CLASS, not a tuple: it is written from the export worker thread and read
    /// on the UI thread, and only a reference assignment is atomic.</remarks>
    private sealed record FullSlot(string Path, ImageBuffer Buf);
    private FullSlot? _fullSlot;

    [ObservableProperty] private Bitmap? _previewImage;

    // ── Bitmap lifetime ─────────────────────────────────────────────────────────
    //
    // Every displayed bitmap owns an UNMANAGED framebuffer behind a tiny managed object — a
    // 1600 px preview is ~6.8 MB of pixels the GC cannot see and never feels pressure from. The
    // render path mints a fresh one per frame, so dragging one slider for a minute leaks a
    // gigabyte and a full editing session reached 7 GB resident. They have to be disposed.
    //
    // They cannot be disposed AT the moment they are displaced, though: the compositor may still
    // be drawing the outgoing frame on the render thread, and freeing its pixels there is an
    // access violation rather than a leak. So disposal is delayed by a grace period — long enough
    // that no in-flight pass can still hold the buffer, short enough that the backlog stays a
    // handful of frames. The count cap covers bursts (a roll-wide thumbnail rebuild retires the
    // whole film strip at once) where waiting on the clock alone would let the backlog grow.
    private const int RetireGraceMs = 500;
    private const int RetireMaxHeld = 12;
    private readonly Queue<(Bitmap Bmp, long Stamp)> _retired = new();

    /// <summary>Hand a no-longer-displayed bitmap to the delayed-disposal queue, and drain whatever
    /// has since aged out. Null-safe; never retires the negative-view stash, which
    /// <see cref="ShowPositiveView"/> still owns.</summary>
    private void Retire(Bitmap? displaced)
    {
        if (displaced is not null && !ReferenceEquals(displaced, _savedPositive))
            _retired.Enqueue((displaced, Environment.TickCount64));
        long now = Environment.TickCount64;
        while (_retired.Count > 0
               && (_retired.Count > RetireMaxHeld || now - _retired.Peek().Stamp > RetireGraceMs))
            _retired.Dequeue().Bmp.Dispose();
    }

    partial void OnPreviewImageChanging(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue)) Retire(oldValue);
    }

    partial void OnSprocketMaskOverlayChanging(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue)) Retire(oldValue);
    }

    /// <summary>Replace a film-strip thumbnail, retiring the one it displaces.</summary>
    private void SetThumbnail(RollFrame f, Bitmap? bmp)
    {
        Bitmap? old = f.Thumbnail;
        f.Thumbnail = bmp;
        Retire(old);
    }

    /// <summary>
    /// Give the big transient buffers back to the OS after a bulk operation.
    ///
    /// A full-resolution frame is ~288 MB at 24 MP, so an import, an export or a contact sheet
    /// parks hundreds of megabytes on the large object heap — which the runtime never compacts on
    /// its own, so the process keeps that footprint committed for the rest of the session even
    /// though every buffer in it is long dead. Compacting is expensive, which is exactly why it
    /// belongs here and nowhere else: these are the tail of an operation the user already waited
    /// seconds for, so the pause is invisible, and they are the only places that allocate at this
    /// size. Never call this on the render path.
    /// </summary>
    private static void ReleaseBulkBuffers()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [ObservableProperty] private string _statusText = Loc.T("打开一张负片（RAW 或 TIFF）开始。");
    /// <summary>Which background stage is running (识别校正图 / 解耦矩阵 / 色度补偿 / 后台解码).
    /// Empty when idle. Shown in the status bar beside <see cref="StatusText"/>.</summary>
    [ObservableProperty] private string _backgroundStatus = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private HistogramData? _histogram;   // RGB histogram of the rendered positive

    // ══ Roll (multi-frame) ══════════════════════════════════════════════════════
    public RollNotes Notes { get; } = new();
    public ObservableCollection<RollFrame> Frames { get; } = new();
    [ObservableProperty] private RollFrame? _currentFrame;
    private RollFrame? _prevFrame;
    private int _switchToken;
    private bool _suppressRender;
    private bool _configLoad;   // true while LoadRollWithConfigAsync drives LoadRollAsync (keeps roll ops)
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _warmCts;   // roll warm-up; must outlive thumbnail restarts

    /// <summary>True once the open roll's warm-up has walked every frame — i.e. the tile cache is
    /// as complete as it is ever going to get. Reopening a roll starts this over at false: tiles
    /// live in RAM only, so an untouched old project still decodes from scratch. The cover writer
    /// reads this to know whether a redraw would be a downgrade (see <see cref="MayWriteCover"/>).</summary>
    private bool _rollWarm;

    /// <summary>Raised after a frame's params load into the UI, so the view can sync the curve editor.</summary>
    public event Action<FrameParams>? FrameParamsLoaded;

    /// <summary>Raised once after a NEW roll's first frame is ready, so the view can prompt sprocket confirm.</summary>
    public event Action? RollImported;
    private bool _pendingSprocketPrompt;

    /// <summary>The current frame's downsampled linear negative — fed to the sprocket dialog.</summary>
    public ImageBuffer? PreviewForDialog => _previewLinear;

    partial void OnCurrentFrameChanged(RollFrame? value)
    {
        // Persist the outgoing frame's live edits before swapping in the new one.
        // Skipped during a restore switch — the frames already hold the restored params.
        if (_prevFrame is not null && HasImage && !_restoring)
        {
            CommitUndo();   // flush any pending edit on the outgoing frame
            _prevFrame.Params = BuildParams();
            RefreshThumbnail(_prevFrame);
        }
        _prevFrame = value;
        if (value is not null) _ = SwitchFrameAsync(value);
    }

    // ══ Undo / redo (full-roll snapshots, coalesced) ════════════════════════════
    private sealed record RollSnapshot(FrameParams[] Params, int Index);
    private readonly List<RollSnapshot> _undo = new();
    private readonly List<RollSnapshot> _redo = new();
    private RollSnapshot? _committed;
    private int _editVersion, _committedVersion;
    private bool _restoring;
    private CancellationTokenSource? _undoCts;
    private const int UndoDepth = 80;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    /// <summary>Snapshot every frame's params (folding current UI into the current frame) + index.</summary>
    private RollSnapshot CaptureSnapshot()
    {
        if (CurrentFrame is not null) CurrentFrame.Params = BuildParams();
        var arr = new FrameParams[Frames.Count];
        for (int i = 0; i < Frames.Count; i++) arr[i] = Frames[i].Params.Clone();
        return new RollSnapshot(arr, CurrentFrame is null ? 0 : Frames.IndexOf(CurrentFrame));
    }

    /// <summary>Establish the current state as the undo baseline (no history entry).</summary>
    private void SetUndoBaseline()
    {
        _undoCts?.Cancel();
        _committed = CaptureSnapshot();
        _committedVersion = _editVersion;
    }

    private void MarkEdit() { _editVersion++; ScheduleUndoCommit(); MarkRollDirty(); }

    private async void ScheduleUndoCommit()
    {
        _undoCts?.Cancel();
        var cts = new CancellationTokenSource();
        _undoCts = cts;
        try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { return; }
        CommitUndo();
    }

    /// <summary>Deposit the previous committed state as one undo step (if the roll changed).</summary>
    private void CommitUndo()
    {
        if (_restoring) return;
        if (_committed is null) { SetUndoBaseline(); return; }
        if (_editVersion == _committedVersion) return;
        _undo.Add(_committed);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        _committed = CaptureSnapshot();
        _committedVersion = _editVersion;
        UpdateUndoState();
    }

    private void UpdateUndoState() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }

    public void Undo()
    {
        CommitUndo();
        if (_undo.Count == 0) { StatusText = Loc.T("没有可撤销的操作"); return; }
        _redo.Add(CaptureSnapshot());
        RollSnapshot snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RestoreSnapshot(snap);
        StatusText = Loc.F($"已撤销（剩余 {_undo.Count} 步）");
    }

    public void Redo()
    {
        if (_redo.Count == 0) { StatusText = Loc.T("没有可重做的操作"); return; }
        _undo.Add(CaptureSnapshot());
        RollSnapshot snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        RestoreSnapshot(snap);
        StatusText = Loc.F($"已重做（剩余 {_redo.Count} 步）");
    }

    private void RestoreSnapshot(RollSnapshot snap)
    {
        _restoring = true;
        try
        {
            for (int i = 0; i < snap.Params.Length && i < Frames.Count; i++)
                Frames[i].Params = snap.Params[i].Clone();
            int idx = Math.Clamp(snap.Index, 0, Math.Max(0, Frames.Count - 1));
            if (idx < Frames.Count && ReferenceEquals(Frames[idx], CurrentFrame))
                LoadParams(CurrentFrame!.Params);   // same frame → reload UI from restored params
            else if (idx < Frames.Count)
                CurrentFrame = Frames[idx];          // different frame → switch reloads params
            RestartThumbnails();
        }
        finally { _restoring = false; }
        _committed = snap;
        _committedVersion = _editVersion;
        UpdateUndoState();
    }

    // ══ Stage 1 — 整卷校准 (FilmBase, density domain) ═══════════════════════════

    // Path A 分光解耦（卷级；导入时从 R/G/B 校正图算出，应用到整卷）
    private double[,]? _decoupleMatrix;
    private double[,]? _decoupleChromaMatrix;

    // Roll-level calibration SOURCE paths retained for .ncproj save (matrices/field are
    // recomputed from these on project load — the project file never stores the matrix itself).
    private string? _calSourceDir;      // Path-A calibration directory
    private string[]? _calRgbPaths;     // resolved [R, G, B] cal files
    private string? _lccSourcePath;     // LCC flat-field reference file

    // LCC 平场校正（卷级平场数据 + 逐帧启用开关；逐帧存在 FrameParams.LccFlatField）
    private ImageBuffer? _lccFlatField;
    [ObservableProperty] private bool _lccAvailable;
    [ObservableProperty] private bool _lccEnabled;
    [ObservableProperty] private string _lccStatus = Loc.T("未载入平场校正");
    partial void OnLccEnabledChanged(bool value) => ScheduleRender();

    /// <summary>Load a flat-field reference (RAW/TIFF) → mean-normalised LCC field, roll-level.</summary>
    public async Task LoadLccAsync(string path)
    {
        try
        {
            ImageBuffer ff = await Task.Run(() => Lcc.LoadFlatField(path, tiffIsLinear: true));
            _lccFlatField = ff;
            _lccSourcePath = path;
            LccAvailable = true;
            LccEnabled = true;   // triggers render
            LccStatus = Loc.T("已载入平场：") + Path.GetFileName(path);
        }
        catch (Exception ex) { LccStatus = Loc.T("平场载入失败：") + ex.Message; }
    }

    // 镜头校正（预反相线性域，不依赖镜头库）：手动畸变 + 手动暗角
    [ObservableProperty] private double _distortionK1;              // 畸变 k1（-0.5..0.5，负=修桶形）
    [ObservableProperty] private double _vignetteAmount;           // 暗角强度（-1..2，正=提亮四角）
    [ObservableProperty] private double _vignetteFalloff = 2.5;    // 暗角范围（1..6，大=只提最外圈）
    partial void OnDistortionK1Changed(double value) => ScheduleRender();
    partial void OnVignetteAmountChanged(double value) => ScheduleRender();
    partial void OnVignetteFalloffChanged(double value) => ScheduleRender();

    // 齿孔遮罩（反相后把遮罩像素填白）
    [ObservableProperty] private bool _sprocketEnabled;
    [ObservableProperty] private double _sprocketThreshold = 0.9;  // 绝对亮度切（0.5..1.0）
    [ObservableProperty] private bool _showSprocketMask;          // 预览上叠加红色遮罩（诊断）
    [ObservableProperty] private Bitmap? _sprocketMaskOverlay;
    partial void OnSprocketEnabledChanged(bool value) => ScheduleRender();
    partial void OnSprocketThresholdChanged(double value) { ScheduleRender(); if (ShowSprocketMask) UpdateSprocketOverlay(); }
    partial void OnShowSprocketMaskChanged(bool value) => UpdateSprocketOverlay();

    private void UpdateSprocketOverlay()
    {
        if (!ShowSprocketMask || _previewLinear is null) { SprocketMaskOverlay = null; return; }
        bool[] mask = Sprocket.MakeMask(_previewLinear.Data, _previewLinear.PixelCount, (float)SprocketThreshold);
        SprocketMaskOverlay = BitmapConvert.ToMaskOverlay(mask, _previewLinear.Width, _previewLinear.Height);
    }

    // 输出意图：0=基础（sRGB gamma + Stage 2），1=线性（跳过 Stage 2 与 sRGB）
    [ObservableProperty] private int _outputIntentIndex;
    partial void OnOutputIntentIndexChanged(int value) => ScheduleRender();

    // 片基透射率 T_base（默认 0.82/0.51/0.29；框选未曝光橙色片基采样）
    [ObservableProperty] private double _tBaseR = 0.82;
    [ObservableProperty] private double _tBaseG = 0.51;
    [ObservableProperty] private double _tBaseB = 0.29;
    [ObservableProperty] private double _dMax = 2.0;                 // 最大密度 D_max
    [ObservableProperty] private double _scanEv;                    // 偏移 scan_exposure_ev（零点校正）
    partial void OnTBaseRChanged(double value) => ScheduleRender();
    partial void OnTBaseGChanged(double value) => ScheduleRender();
    partial void OnTBaseBChanged(double value) => ScheduleRender();
    /// <summary>
    /// d_max moved — drag pivot along with it, unless the user has taken manual control.
    ///
    /// pivot is not an independent number: it is the mid-tone anchor at 0.45·d_max, and holding
    /// that relationship is the ONLY reason changing 反差 does not also change the picture's
    /// brightness. Linking it once when a paper grade is picked is not enough — every later d_max
    /// edit (the slider, 采样 D-max, 自动 D-max) leaves pivot behind at a value derived from the
    /// OLD d_max. The picture drifts quietly, and then the next 反差 change re-links pivot in one
    /// jump, which reads as "选个相纸号数把我的 D-max 改了". The source relinks on every emit
    /// (roll_cal_panel.py::_emit); this is the same rule at the same place in the chain.
    /// </summary>
    partial void OnDMaxChanged(double value)
    {
        if (!IsManualGrade)
        {
            double linked = WbMath.LinkedPivot(value);
            // Pivot's own handler schedules the render; don't queue a second one.
            if (Math.Abs(Pivot - linked) > 1e-4) { Pivot = linked; return; }
        }
        ScheduleRender();
    }

    partial void OnScanEvChanged(double value) => ScheduleRender();

    // 暗部 WB offset（加性，默认 0）
    [ObservableProperty] private double _wbOffR;
    [ObservableProperty] private double _wbOffG;
    [ObservableProperty] private double _wbOffB;
    partial void OnWbOffRChanged(double value) => ScheduleRender();
    partial void OnWbOffGChanged(double value) => ScheduleRender();
    partial void OnWbOffBChanged(double value) => ScheduleRender();

    // 亮部 WB high（乘性，默认 1）
    [ObservableProperty] private double _wbHighR = 1.0;
    [ObservableProperty] private double _wbHighG = 1.0;
    [ObservableProperty] private double _wbHighB = 1.0;
    partial void OnWbHighRChanged(double value) => ScheduleRender();
    partial void OnWbHighGChanged(double value) => ScheduleRender();
    partial void OnWbHighBChanged(double value) => ScheduleRender();

    // 反差（相纸号数）：预设 + 手动 grade/pivot
    [ObservableProperty] private int _gradePresetIndex = 1;         // 标准 2–3 号纸
    [ObservableProperty] private bool _isManualGrade;
    [ObservableProperty] private double _grade = 1.65;
    [ObservableProperty] private double _pivot = 0.9;
    partial void OnGradeChanged(double value) => ScheduleRender();
    partial void OnPivotChanged(double value) => ScheduleRender();
    partial void OnGradePresetIndexChanged(int value)
    {
        var (_, grade) = WbMath.GradePresets[Math.Clamp(value, 0, WbMath.GradePresets.Length - 1)];
        if (grade < 0) { IsManualGrade = true; return; }            // 手动 — reveal sliders, keep values
        IsManualGrade = false;
        // Preset: set grade AND auto-link pivot to lock the mid-tone (one render).
        _renderCts?.Cancel();
        Grade = grade;
        Pivot = WbMath.LinkedPivot(DMax);
    }

    // ══ Stage 2 — 帧编辑 (SceneBase, positive domain, geomean-1 WB) ═════════════
    [ObservableProperty] private double _temp;                     // 色温（±250，log 空间）
    [ObservableProperty] private double _tint;                     // 色调（±250，log 空间）
    [ObservableProperty] private double _exposureEv;               // 曝光（±3，output×2^EV）
    [ObservableProperty] private double _black;                    // 黑场（±1，0=透传）
    [ObservableProperty] private double _white;                    // 白场（±1，0=透传）
    [ObservableProperty] private double _contrast;                 // 反差（±1）
    [ObservableProperty] private double _highlights;               // 高光（±1）
    [ObservableProperty] private double _shadows;                  // 阴影（±1）
    [ObservableProperty] private double _saturation;               // 饱和度（±1）
    partial void OnTempChanged(double value) => ScheduleRender();
    partial void OnTintChanged(double value) => ScheduleRender();
    partial void OnExposureEvChanged(double value) => ScheduleRender();
    partial void OnBlackChanged(double value) => ScheduleRender();
    partial void OnWhiteChanged(double value) => ScheduleRender();
    partial void OnContrastChanged(double value) => ScheduleRender();
    partial void OnHighlightsChanged(double value) => ScheduleRender();
    partial void OnShadowsChanged(double value) => ScheduleRender();
    partial void OnSaturationChanged(double value) => ScheduleRender();

    // ══ Geometry (Core applies: orientation → straighten → crop) ════════════════
    [ObservableProperty] private double _rotation;                 // 拉直角度（CW）
    private int _quarterTurns;
    private bool _flipH, _flipV;
    private (double X, double Y, double W, double H)? _cropRect;
    partial void OnRotationChanged(double value) => ScheduleRender();

    // ── Orientation, and the crop that has to travel with it ────────────────────
    //
    // CropRect is normalised against the ORIENTED frame, so a quarter turn swaps that frame's
    // width and height underneath it. Leaving the numbers alone silently reshapes the crop: a
    // 4:3 selection came back as 0.59:1 after one turn instead of 3:4. The rect therefore gets
    // the same transform the pixels do.
    //
    // Only the incremental operation is applied, not the whole orientation — for the FLIPS that
    // is unconditionally sound (they are applied last, so toggling one mirrors the displayed
    // frame and nothing else). For a QUARTER TURN it holds only when the mirrors compose to a
    // rotation, i.e. zero or two of them: a single mirror CONJUGATES the turn into its inverse,
    // because Geometry.ApplyOrientation runs the turns FIRST and the flips after.
    //
    //   displayed = F ∘ R^k,  so  k → k+1  moves the picture by  F ∘ R ∘ F⁻¹
    //     F = identity or 180°  →  R      (clockwise, as advertised)
    //     F = a single mirror   →  R⁻¹    (counter-clockwise — the button lies)
    //
    // That is a real user-visible bug, not a technicality: 顺时针 90° turned a horizontally
    // flipped scan the other way, and the crop rect — transformed the way the button CLAIMED —
    // then travelled opposite to the pixels and framed a different part of the picture.
    //
    // Fixed by keeping the SCREEN as the contract: the rect always gets the transform the button
    // names, and the stored quarter turn absorbs the mirror parity so the pixels agree.

    /// <summary>Normalised rect under a 90° CW frame turn: (u,v) → (1-v, u).</summary>
    public static (double X, double Y, double W, double H) RotateCropCw(
        (double X, double Y, double W, double H) c) => (1 - (c.Y + c.H), c.X, c.H, c.W);

    /// <summary>The inverse: (u,v) → (v, 1-u).</summary>
    public static (double X, double Y, double W, double H) RotateCropCcw(
        (double X, double Y, double W, double H) c) => (c.Y, 1 - (c.X + c.W), c.H, c.W);

    public static (double X, double Y, double W, double H) FlipCropH(
        (double X, double Y, double W, double H) c) => (1 - (c.X + c.W), c.Y, c.W, c.H);

    public static (double X, double Y, double W, double H) FlipCropV(
        (double X, double Y, double W, double H) c) => (c.X, 1 - (c.Y + c.H), c.W, c.H);

    /// <summary>Orientation changes said out loud, including where the crop ended up. They
    /// used to report nothing at all, which made a turn indistinguishable from a no-op — and
    /// hid the fact that the crop travels with the frame.</summary>
    private string OrientationStatus(string what)
        => _cropRect is { } c
            ? Loc.F($"{what}（裁切已同步：{c.X:F2},{c.Y:F2},{c.W:F2},{c.H:F2}）")
            : what;

    /// <summary>An odd number of mirrors is in the chain, so a stored quarter turn reads BACKWARDS
    /// on screen — see the note above.</summary>
    private bool Mirrored => _flipH ^ _flipV;

    public void RotateCw()
    {
        _quarterTurns = (_quarterTurns + (Mirrored ? 3 : 1)) & 3;
        if (_cropRect is { } c) _cropRect = RotateCropCw(c);
        StatusText = OrientationStatus(Loc.T("顺时针 90°"));
        ScheduleRender();
    }

    public void RotateCcw()
    {
        _quarterTurns = (_quarterTurns + (Mirrored ? 1 : 3)) & 3;
        if (_cropRect is { } c) _cropRect = RotateCropCcw(c);
        StatusText = OrientationStatus(Loc.T("逆时针 90°"));
        ScheduleRender();
    }

    public void FlipHorizontal()
    {
        _flipH = !_flipH;
        if (_cropRect is { } c) _cropRect = FlipCropH(c);
        StatusText = OrientationStatus(Loc.T("水平翻转"));
        ScheduleRender();
    }

    public void FlipVertical()
    {
        _flipV = !_flipV;
        if (_cropRect is { } c) _cropRect = FlipCropV(c);
        StatusText = OrientationStatus(Loc.T("竖直翻转"));
        ScheduleRender();
    }
    /// <summary>
    /// Pixel dimensions the crop rect is normalised against: the frame AFTER orientation (and
    /// straighten, which preserves size) but BEFORE crop — exactly what
    /// <see cref="Geometry.ApplyCrop"/> is handed.
    ///
    /// The view needs this to build an aspect-ratio crop. It must NOT measure the displayed
    /// bitmap: that one is already cropped, so a rect derived from it describes a region of the
    /// crop while <see cref="SetCrop"/> stores a region of the whole frame. Switching presets
    /// then compounds the mismatch — measured 0.39x to 2.86x off the requested ratio by the
    /// third switch.
    ///
    /// Uses the SOURCE dimensions rather than the preview's: the preview's integer box factor
    /// truncates, so its aspect can differ slightly from what the export will actually be.
    /// </summary>
    public (int W, int H)? CropFrameSize
    {
        get
        {
            int w, h;
            if (CurrentFrame is { } f && _previews.Get(f.Path) is { } e)
                (w, h) = (e.SourceWidth, e.SourceHeight);
            else if (_previewLinear is { } p) (w, h) = (p.Width, p.Height);
            else return null;
            return (((_quarterTurns % 4) + 4) % 4) % 2 == 1 ? (h, w) : (w, h);
        }
    }

    /// <summary>
    /// While true the preview renders the frame UNCROPPED, whatever crop is stored.
    ///
    /// The crop frame is positioned by dragging it over the picture, so the user has to be able
    /// to see what is currently being excluded — and the draft rectangle is normalised against
    /// the un-cropped frame, which is only the same space the overlay is drawn in if the preview
    /// is showing that frame. Rendering the crop while editing it would mean drawing the handles
    /// in one coordinate space and storing them in another, which is the same class of mistake
    /// that made the presets drift.
    /// </summary>
    private bool _cropEditing;

    public bool CropEditing
    {
        get => _cropEditing;
        set { if (_cropEditing != value) { _cropEditing = value; ScheduleRender(); } }
    }

    /// <summary>The stored crop, so re-entering the crop tool ADJUSTS the existing frame instead
    /// of starting over.</summary>
    public (double X, double Y, double W, double H)? CurrentCrop => _cropRect;

    public void SetCrop((double X, double Y, double W, double H) rect)
    {
        _cropRect = rect;
        StatusText = Loc.F($"裁切 {rect.X:F2},{rect.Y:F2},{rect.W:F2},{rect.H:F2}");
        ScheduleRender();
    }
    public void ClearCrop() { _cropRect = null; StatusText = Loc.T("已清除裁切"); ScheduleRender(); }

    /// <summary>The 拉直 slider's range, and therefore the ceiling on a straighten measurement.</summary>
    public const double StraightenLimit = 15.0;

    /// <summary>
    /// Fold a drawn reference line's correction into 拉直.
    ///
    /// The angle is ADDED to the current rotation, not assigned: the preview the line was drawn on
    /// is ALREADY rotated by the current value, so the measurement is a residual, not an absolute.
    /// That is also what makes the tool repeatable — draw, look, draw again on what is left — and
    /// what lets it compose with the slider instead of fighting it.
    /// </summary>
    public void ApplyStraightenAngle(double deltaDeg)
    {
        double wanted = Rotation + deltaDeg;
        Rotation = Math.Clamp(wanted, -StraightenLimit, StraightenLimit);
        string measured = Loc.F($"拉线取直 {deltaDeg:+0.0;-0.0}° → 拉直 {Rotation:F1}°");
        StatusText = Math.Abs(wanted - Rotation) > 1e-9
            ? measured + Loc.F($"（已到 ±{StraightenLimit:F0}° 上限，如需更多请先用 90° 旋转）")
            : measured;
    }

    // ── Sampling state ──────────────────────────────────────────────────────────
    private Bitmap? _savedPositive;                   // positive stashed while showing negative
    [ObservableProperty] private string _filmBaseText = Loc.T("片基：默认（未采样）");

    /// <summary>Whether <see cref="FilmBaseText"/> is reporting a measured t_base rather than
    /// standing at its default. Only the default is re-translated on a language switch — a
    /// measured one is three numbers with a translated prefix, and the prefix is not worth
    /// keeping the sample around for. See <see cref="RetranslateText"/>.</summary>
    private bool _filmBaseSampled;

    // ── Tone curves (gamma-2.2 domain; set by the CurveEditor via SetCurves) ─────
    private List<(double X, double Y)> _curveM = new(), _curveR = new(), _curveG = new(), _curveB = new();
    private bool _curvePreserveHue = true;

    /// <summary>Push the four channel curves + hue-preserve flag from the editor and re-render.</summary>
    public void SetCurves(IReadOnlyList<(double X, double Y)> m, IReadOnlyList<(double X, double Y)> r,
                          IReadOnlyList<(double X, double Y)> g, IReadOnlyList<(double X, double Y)> b,
                          bool preserveHue)
    {
        _curveM = new List<(double, double)>(m);
        _curveR = new List<(double, double)>(r);
        _curveG = new List<(double, double)>(g);
        _curveB = new List<(double, double)>(b);
        _curvePreserveHue = preserveHue;
        ScheduleRender();
    }

    private double[] TBaseArr() => new[] { TBaseR, TBaseG, TBaseB };
    private double[] WbOffArr() => new[] { WbOffR, WbOffG, WbOffB };
    private double[] WbHighArr() => new[] { WbHighR, WbHighG, WbHighB };

    /// <summary>Snapshot the current state into a FrameParams for a render/export.</summary>
    private FrameParams BuildParams() => new()
    {
        OutputIntent = OutputIntentIndex == 0 ? OutputIntent.Basic : OutputIntent.None,
        // Stage 1 — lens corrections (pre-inversion, linear domain)
        DistortionK1 = DistortionK1,
        VignetteAmount = VignetteAmount,
        VignetteFalloff = VignetteFalloff,
        LccFlatField = LccEnabled && LccAvailable ? _lccFlatField : null,
        DecoupleMatrix = _decoupleMatrix,
        DecoupleMode = DecoupleMode.Linear,
        DecoupleChromaMatrix = _decoupleChromaMatrix,
        SprocketEnabled = SprocketEnabled,
        SprocketThreshold = SprocketThreshold,
        // Stage 1 — film base
        TBase = TBaseArr(),
        WbOffset = WbOffArr(),
        WbHigh = WbHighArr(),
        ScanExposureEv = ScanEv,
        Grade = Grade,
        Pivot = Pivot,
        DMax = DMax,
        // Stage 2 — 色温/色调 → geomean-1 gains; 黑/白场 → levels
        WbGains = WbMath.TempTintToGains(Temp, Tint),
        ExposureEv = ExposureEv,
        BlackPoint = WbMath.BlackSliderToPoint(Black),
        WhitePoint = WbMath.WhiteSliderToPoint(White),
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        Saturation = Saturation,
        CurvePointsM = _curveM,
        CurvePointsR = _curveR,
        CurvePointsG = _curveG,
        CurvePointsB = _curveB,
        CurvePreserveHue = _curvePreserveHue,
        // Geometry
        Rotation = Rotation,
        QuarterTurns = _quarterTurns,
        FlipH = _flipH,
        FlipV = _flipV,
        // Suppressed while the crop frame is being positioned — see CropEditing.
        CropRect = _cropEditing ? null : _cropRect,
    };

    // ── Sampling view: show the NEGATIVE while picking the film base ─────────────
    public void ShowNegativeView()
    {
        ImageBuffer? neg = _previewLinear;
        if (neg is null) return;
        // The patch holds POSITIVE pixels; leaving it up would paste a bright rectangle over
        // the negative. Same for the before/after compare below.
        ClearSharpPatch();
        _savedPositive = PreviewImage;
        var disp = new ImageBuffer(neg.Width, neg.Height, (float[])neg.Data.Clone());
        Srgb.ApplyForwardInPlace(disp.Data);   // gamma for display only
        PreviewImage = BitmapConvert.ToBitmap(disp);
    }

    public void ShowPositiveView()
    {
        if (_savedPositive is not null) { PreviewImage = _savedPositive; _savedPositive = null; }
        ScheduleRender();
    }

    // ── Before/after compare: show the positive WITHOUT Stage-2 (scene) edits ─────
    public void ShowBeforeEdits()
    {
        if (_previewLinear is null) return;
        ClearSharpPatch();   // patch was rendered WITH the Stage-2 edits this view strips
        FrameParams p = BuildParams();
        RollFrame.ResetScene(p);   // strip every Stage-2 adjustment
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, p);
        PreviewImage = BitmapConvert.ToBitmap(pos);
    }

    public void ShowAfterEdits() => ScheduleRender();   // re-render the fully edited positive

    // ══ Stage-1 sampling (reads the linear negative) ═══════════════════════════
    //
    // RESOLUTION: every sampler reads the PREVIEW, never a full-res decode — the same choice the
    // Python GUI makes (_sampling_source → _oriented_raw → the _raw_cache preview). These are all
    // rect means over a blurred patch, and box-downsampling is itself a local mean, so the numbers
    // barely move; holding a ~288 MB full-res buffer resident just to average a rectangle is what
    // used to force a multi-second re-decode on every frame selection.
    /// <summary>
    /// The negative in the pipeline's Stage-1 sampling domain — i.e. exactly the buffer that
    /// <see cref="Pipeline.ProcessFrame"/> hands to the density inversion. On Path A that is the
    /// DECOUPLED image, and the decoupled image is the START of every measurement: the pipeline
    /// runs LCC → vignette → decouple BEFORE dividing by t_base and applying wb_offset / wb_high /
    /// d_max, so a sample taken any earlier sits in a colour basis the renderer never produces and
    /// the positive drifts (magenta / WB cast).
    ///
    /// EVERY value sampler — t_base, wb_offset, wb_high, d_max, scan_ev, the film-base bright
    /// reference, Deep-WB's highlight density — must read this. Only the MASK/THRESHOLD family
    /// (sprocket luma cuts, dark valley) stays on the raw negative, where those thresholds are
    /// calibrated and where <see cref="Pipeline.ProcessFrame"/> also builds its runtime mask;
    /// that split is the <c>images</c> / <c>valueImages</c> contract in <see cref="FilmBase"/>.
    ///
    /// Distortion is deliberately NOT applied: it is geometric, and sampling rects arrive in the
    /// coordinates of the displayed (already-distortion-corrected) preview.
    /// </summary>
    private ImageBuffer? Stage1Source(ImageBuffer? neg)
    {
        if (neg is null) return null;
        ImageBuffer? lcc = LccEnabled && LccAvailable ? _lccFlatField : null;
        if (lcc is null && VignetteAmount == 0.0 && _decoupleMatrix is null) return neg;

        var src = new ImageBuffer(neg.Width, neg.Height, (float[])neg.Data.Clone());
        if (lcc is not null)
            Lcc.Apply(src.Data, src.Width, src.Height, lcc);
        if (VignetteAmount != 0.0)
            LensCorrections.ApplyVignette(src.Data, src.Width, src.Height, VignetteAmount, VignetteFalloff);
        if (_decoupleMatrix is not null)
            Decouple.Apply(src.Data, _decoupleMatrix, DecoupleMode.Linear);
        return src;
    }

    /// <summary>
    /// Run a rect sampler, turning a rejected selection into a status message.
    ///
    /// The FilmBase samplers throw ArgumentException on a region they cannot use (density ≤ 0,
    /// non-positive T_base). These run straight off a pointer-released handler, so an escaping
    /// exception unwinds through Avalonia's event dispatch and terminates the process — picking a
    /// slightly wrong rectangle must cost a message, not the whole session and every unsaved edit.
    /// </summary>
    private void TrySample(string what, Action sample)
    {
        try { sample(); }
        catch (Exception ex) { StatusText = Loc.F($"{what}失败：{ex.Message}"); }
    }

    /// <summary>Film-base t_base from a rect over clear film (removes the orange mask).</summary>
    public void SampleFilmBase((double X, double Y, double W, double H) rect) => TrySample(Loc.T("片基采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        double[] tb = FilmBase.SampleTBase(src, rect);
        TBaseR = tb[0]; TBaseG = tb[1]; TBaseB = tb[2];
        FilmBaseText = Loc.F($"片基 t_base = {tb[0]:F3}, {tb[1]:F3}, {tb[2]:F3}");
        _filmBaseSampled = true;
        // Sanity gate: the film base is the most transmissive part of a negative, so a t_base far
        // below the frame's p99.9 almost certainly missed it.
        //
        // The reference MUST come from the same buffer the t_base did — i.e. the Stage-1
        // (decoupled) domain. Comparing a decoupled t_base against a raw p99.9 is a domain
        // mismatch that fires this warning on perfectly good Path A picks. Reusing `src` is also
        // what keeps it cheap: deriving it separately cloned the whole preview a second time and
        // re-ran LCC → vignette → decouple over it — ~20 MB and a full photometric pass for one
        // 0.4× comparison. Measuring on the preview rather than full-res is fine; the gate is a
        // loose heuristic and box-downsampling barely moves a 99.9th percentile.
        double[] br = ImageIo.BrightReference(src);
        StatusText = tb[0] < br[0] * 0.4 || tb[1] < br[1] * 0.4 || tb[2] < br[2] * 0.4
            ? Loc.T("⚠ 采样区偏暗，可能不是片基——请在负片视图中对准最亮的橙色片基重采")
            : FilmBaseText;
    });

    /// <summary>Shadow-end density offset (wb_offset) from a dark rect.</summary>
    public void SampleWbOffset((double X, double Y, double W, double H) rect) => TrySample(Loc.T("暗部采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        double[] off = FilmBase.SampleWbOffsetFromRect(src, rect, TBaseArr());
        WbOffR = off[0]; WbOffG = off[1]; WbOffB = off[2];
        StatusText = Loc.F($"暗部 wb_offset = {off[0]:F3}, {off[1]:F3}, {off[2]:F3}");
    });

    /// <summary>Highlight-end WB (wb_high) from a neutral highlight rect.</summary>
    public void SampleWbHigh((double X, double Y, double W, double H) rect) => TrySample(Loc.T("高光采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        double[] hi = FilmBase.SampleWbHighFromRect(src, rect, TBaseArr(), WbOffArr());
        WbHighR = hi[0]; WbHighG = hi[1]; WbHighB = hi[2];
        StatusText = Loc.F($"高光 wb_high = {hi[0]:F3}, {hi[1]:F3}, {hi[2]:F3}");
    });

    /// <summary>D-max from the negative's darkest region (= scene highlights = positive white).</summary>
    public void SampleDMax((double X, double Y, double W, double H) rect) => TrySample(Loc.T("D-max 采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        DMax = FilmBase.SampleDMaxFromRect(src, rect, TBaseArr());
        StatusText = Loc.F($"D-max = {DMax:F3}（底片最暗区 = 场景高光端）");
    });

    /// <summary>Apply the import-time sprocket dialog result to the whole roll, then auto-detect film base.</summary>
    public void ApplySprocketFromDialog(bool enabled, double? threshold)
    {
        if (enabled && threshold is double thr)
        {
            SprocketEnabled = true; SprocketThreshold = thr;
            foreach (RollFrame f in Frames) { f.Params.SprocketEnabled = true; f.Params.SprocketThreshold = thr; }
        }
        else
        {
            SprocketEnabled = false;
            foreach (RollFrame f in Frames) f.Params.SprocketEnabled = false;
        }
        AutoFilmBaseFromRoll(threshold);   // threshold null (skip) → pure-brightness base
        UpdateSprocketOverlay();
    }

    /// <summary>Estimate T_base excluding the light-board (given the sprocket threshold) → all frames.</summary>
    private void AutoFilmBaseFromRoll(double? sprocketThreshold)
    {
        if (_previewLinear is null) return;
        try
        {
            // Path A: t_base must live in the DECOUPLED domain (the pipeline decouples BEFORE
            // dividing by t_base). Sample values from the decoupled negative; masks stay on the raw
            // (its luma is where the sprocket threshold was calibrated). Mirrors Python's valueImages.
            ImageBuffer? dec = Stage1Source(_previewLinear);
            double[] tb = ReferenceEquals(dec, _previewLinear) || dec is null
                ? FilmBase.EstimateTBaseFromRoll(new[] { _previewLinear }, sprocketThreshold)
                : FilmBase.EstimateTBaseFromRoll(new[] { _previewLinear }, sprocketThreshold,
                                                 valueImages: new[] { dec });
            TBaseR = tb[0]; TBaseG = tb[1]; TBaseB = tb[2];
            foreach (RollFrame f in Frames) f.Params.TBase = (double[])tb.Clone();
            FilmBaseText = Loc.F($"片基 t_base = {tb[0]:F3}, {tb[1]:F3}, {tb[2]:F3}（自动）");
            _filmBaseSampled = true;
            StatusText = Loc.T("已自动检测片基") + (sprocketThreshold is null ? Loc.T("（无齿孔模式）") : Loc.T("与齿孔阈值"));
        }
        catch (Exception ex) { StatusText = Loc.T("自动片基检测失败：") + ex.Message; }
        RestartThumbnails();
    }

    /// <summary>The RAW preview restricted to the current crop (else the whole frame) — auto-detections
    /// analyse only the kept picture so sprockets / film edges / borders don't skew D-max or WB.
    /// This is the MASK domain; for measured values use <see cref="AutoRegionStage1"/>.</summary>
    private ImageBuffer? AutoRegion()
        => _previewLinear is { } prev && _cropRect is { } c ? Geometry.ApplyCrop(prev, c) : _previewLinear;

    /// <summary>The same region in the Stage-1 sampling domain (decoupled under Path A). The
    /// photometric chain runs on the FULL preview before cropping — vignette is radial about the
    /// frame centre, so correcting a crop in isolation would centre the falloff on the wrong point.</summary>
    private ImageBuffer? AutoRegionStage1()
    {
        if (Stage1Source(_previewLinear) is not { } s) return null;
        return _cropRect is { } c ? Geometry.ApplyCrop(s, c) : s;
    }

    /// <summary>Auto-detect D-max = 99.9th density percentile of the T_norm (T / t_base) frame.</summary>
    public void AutoDetectDMax()
    {
        ImageBuffer? src = AutoRegionStage1();
        if (src is null) return;
        // DetectDMax expects the T_norm image (T / t_base), not raw T — normalise first.
        double[] tb = TBaseArr();
        var norm = new float[src.Data.Length];
        for (int p = 0; p < src.PixelCount; p++)
        {
            int b = p * 3;
            norm[b] = (float)(src.Data[b] / tb[0]);
            norm[b + 1] = (float)(src.Data[b + 1] / tb[1]);
            norm[b + 2] = (float)(src.Data[b + 2] / tb[2]);
        }
        DMax = FilmBase.DetectDMax(new ImageBuffer(src.Width, src.Height, norm));
        StatusText = Loc.F($"自动 D-max = {DMax:F3}");
    }

    /// <summary>Sample the scan-exposure bias so a film-base rect falls to pure black.</summary>
    /// <remarks>
    /// The inversion adds <c>scan_ev·log10(2)</c> to the pre-grade density D. A rect
    /// that should read as clear film has residual density D_resid = mean(−log10(T/t_base)).
    /// To cancel it we shift scan_ev by <c>−D_resid / log10(2)</c> (matches Python's
    /// apply_scan_ev_sample, which drives the film-base region to D = 0 → pure black).
    /// </remarks>
    public void SampleScanEv((double X, double Y, double W, double H) rect)
    {
        double[]? t = MeanOfNegative(rect);
        if (t is null) return;
        double[] tb = TBaseArr();
        double dResid = 0.0;
        for (int c = 0; c < 3; c++)
            dResid += -Math.Log10(Math.Max(t[c], 1e-6) / tb[c]);
        dResid /= 3.0;
        ScanEv = Math.Clamp(ScanEv - dResid / 0.3010299956639812, -3.0, 3.0);
        StatusText = Loc.F($"偏移 scan_ev = {ScanEv:F2}");
    }

    /// <summary>
    /// A normalised (x,y,w,h) selection turned into half-open pixel bounds on <paramref name="img"/>.
    ///
    /// One definition for every rect sampler. The clamping is fiddly in a way that is easy to get
    /// subtly different when it is written out three times: the origin is clamped so it stays a
    /// valid index, and each far edge is clamped to at least origin+1 so a selection that rounds
    /// to nothing still yields one pixel instead of an empty (or inverted) loop.
    /// </summary>
    private static (int X0, int Y0, int X1, int Y1) PixelBounds(
        ImageBuffer img, (double X, double Y, double W, double H) rect)
    {
        int w = img.Width, h = img.Height;
        int x0 = Math.Clamp((int)(rect.X * w), 0, w - 1), y0 = Math.Clamp((int)(rect.Y * h), 0, h - 1);
        int x1 = Math.Clamp((int)((rect.X + rect.W) * w), x0 + 1, w);
        int y1 = Math.Clamp((int)((rect.Y + rect.H) * h), y0 + 1, h);
        return (x0, y0, x1, y1);
    }

    /// <summary>Per-channel mean over a normalised rect of <paramref name="img"/>.</summary>
    private static double[] RectMean(ImageBuffer img, (double X, double Y, double W, double H) rect)
    {
        var (x0, y0, x1, y1) = PixelBounds(img, rect);
        int w = img.Width;
        float[] d = img.Data;
        var s = new double[3];
        long n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            { int i = (y * w + x) * 3; s[0] += d[i]; s[1] += d[i + 1]; s[2] += d[i + 2]; n++; }
        return new[] { s[0] / n, s[1] / n, s[2] / n };
    }

    /// <summary>Mean linear transmittance of a rect on the (un-inverted) negative, in the pipeline's
    /// Stage-1 sampling domain (decoupled under Path A) so scan_ev density matches the render.</summary>
    private double[]? MeanOfNegative((double X, double Y, double W, double H) rect)
        => Stage1Source(_previewLinear) is { } neg ? RectMean(neg, rect) : null;

    // ══ Stage-2 white balance (grey-world on the rendered positive) ════════════
    /// <summary>Grey-point WB: neutralise a sampled rect, landing on 色温/色调 sliders.</summary>
    public void SampleGreyPoint((double X, double Y, double W, double H) rect)
    {
        double[]? mean = MeanOfRenderedPositive(rect);
        if (mean is null) return;
        // Grey-point is a pure-COLOUR op: discard the brightness part (EV stays put).
        double[] gains = GreyWorldGains(mean);
        var (temp, tint, _ev) = WbMath.GainsToTempTint(gains);
        Temp = Math.Clamp(temp, -WbMath.WbRange, WbMath.WbRange);
        Tint = Math.Clamp(tint, -WbMath.WbRange, WbMath.WbRange);
        StatusText = Loc.F($"灰点白平衡 → 色温 {Temp:F0} / 色调 {Tint:F0}");
    }

    /// <summary>
    /// Auto highlight WB (Stage 1, NegativeConvert way): find the roll's brightest neutral
    /// scene point and treat it as pure white → per-channel wb_high, landing on the wb_high
    /// sliders. Ports Python's 自动（寻找最亮点并视为纯白）via <see cref="FilmBase.AutoWbHighFromRoll"/>
    /// on the single current frame.
    /// </summary>
    public void AutoWbHigh()
    {
        // Two-buffer contract (same as AutoFilmBaseFromRoll): the luma masks — sprocket/light-board
        // cut and dark valley — key off the RAW region, which is where those thresholds are
        // calibrated and where the pipeline builds its own mask; the sampled VALUES come from the
        // decoupled region. Previously this handed the decoupled buffer in as `images`, so the
        // valley ran in an uncalibrated domain, and it never forwarded SprocketThreshold at all —
        // the light-board cut was dead even on rolls with sprockets.
        ImageBuffer? raw = AutoRegion();
        ImageBuffer? val = AutoRegionStage1();
        if (raw is null || val is null) return;
        try
        {
            double[] wh = FilmBase.AutoWbHighFromRoll(
                new[] { raw }, TBaseArr(), WbOffArr(),
                SprocketEnabled ? SprocketThreshold : null,
                valueImages: ReferenceEquals(raw, val) ? null : new[] { val });
            WbHighR = wh[0]; WbHighG = wh[1]; WbHighB = wh[2];
            StatusText = Loc.F($"自动亮部 WB → wb_high = {wh[0]:F3}, {wh[1]:F3}, {wh[2]:F3}");
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("自动白平衡失败：") + ex.Message;
        }
    }

    // ── Smart WB (Deep-WB net → affine wb_high/wb_offset) ───────────────────────
    //
    // One shared session for the process: loading net_awb.onnx costs real time and the weights
    // are ~17 MB, so a per-click session would be both slow and wasteful.
    //
    // This is OpenRevelare.DeepWb.Onnx's corrector — the SAME one the CLI's --print-awb parity
    // harness drives. The GUI used to have a private copy that resized with plain bilinear and
    // skipped the uint8 quantisation, so the pixels the net judged here were not the pixels the
    // reference was checked against. The model is a loose file beside the app (the backend's
    // Content item), not an embedded resource: onnxruntime maps it directly instead of the GUI
    // inflating it through a MemoryStream into a 17 MB managed array first.
    private OpenRevelare.DeepWb.Onnx.OnnxDeepWbCorrector? _deepWb;

    private OpenRevelare.DeepWb.Onnx.OnnxDeepWbCorrector GetDeepWb()
        => _deepWb ??= new OpenRevelare.DeepWb.Onnx.OnnxDeepWbCorrector();

    /// <summary>Stage-1 render params for the Deep-WB net input: BASIC (colour-restored + sRGB),
    /// current calibration with the trial wb_high + adaptive d_max, Stage-2 reset to defaults so the
    /// net judges an un-graded neutral picture (port of the worker's nn_cal).</summary>
    private FrameParams BuildDeepWbRenderParams(double[] wbHigh, double iterDMax) => new()
    {
        OutputIntent = OutputIntent.Basic,
        TBase = TBaseArr(),
        WbHigh = (double[])wbHigh.Clone(),
        WbOffset = WbOffArr(),
        ScanExposureEv = ScanEv,
        Grade = Grade, Pivot = Pivot, DMax = iterDMax,   // adaptive d_max (highlight just touches 1)
        // ChromaGrade left default (3.05) → full colour restoration, as the worker uses.
        DistortionK1 = DistortionK1, VignetteAmount = VignetteAmount, VignetteFalloff = VignetteFalloff,
        LccFlatField = LccEnabled && LccAvailable ? _lccFlatField : null,
        // Path A decoupling — MUST match BuildParams. The net judges a rendered positive and its
        // gains are folded straight into wb_high, which is then applied to a pipeline that DOES
        // decouple; iterating on an un-decoupled render solves wb_high in the wrong colour basis
        // and lands magenta. d_highlight is measured on the decoupled negative for the same reason,
        // and rawDelta divides the net's log-gains BY that d_highlight — so if these two disagree
        // the mismatch is baked into every iteration.
        DecoupleMatrix = _decoupleMatrix,
        DecoupleMode = DecoupleMode.Linear,
        DecoupleChromaMatrix = _decoupleChromaMatrix,
        SprocketEnabled = SprocketEnabled, SprocketThreshold = SprocketThreshold,
        CropRect = _cropRect, Rotation = Rotation, QuarterTurns = _quarterTurns, FlipH = _flipH, FlipV = _flipV,
        // Stage 2 reset to defaults (the WB decision must not be polluted by artistic edits).
    };

    /// <summary>
    /// Smart white balance (Beta) — faithful port of the source worker (gui/main_window.py
    /// _AutoWBAffineWorker + white_balance.nn_wb_high_step): start from the geometric highlight
    /// baseline, then iterate the Deep-WB net, adding a chroma-only density-slope delta to wb_high
    /// each round (adaptive d_max, BASIC-rendered positive), up to 50 rounds or |log_gains| &lt; 0.01.
    /// </summary>
    public async Task AutoWbAiAsync()
    {
        if (_previewLinear is null) return;
        IsBusy = true;
        StatusText = Loc.T("智能白平衡分析中 …");
        try
        {
            double grade = Grade, pivot = Pivot;
            double[] tBase = TBaseArr(), wbOffset = WbOffArr();
            // raw — the pipeline decouples internally, which only holds because
            // BuildDeepWbRenderParams carries DecoupleMatrix. Do not drop it there.
            ImageBuffer neg = _previewLinear;
            // The highlight anchor is measured the same way 自动亮部 WB measures it: masks off the
            // RAW region (where the sprocket cut and the dark valley are calibrated), values off the
            // decoupled one (where t_base/wb_high live and where the render below lands).
            ImageBuffer? anchorRaw = AutoRegion();
            ImageBuffer? anchorVal = AutoRegionStage1();
            if (anchorRaw is null || anchorVal is null) return;

            var (wbHigh, converged) = await Task.Run(() =>
            {
                OpenRevelare.DeepWb.Onnx.OnnxDeepWbCorrector corr = GetDeepWb();

                // d_highlight: the density of the roll's ONE brightest real picture point.
                // It must be a SAME-SOURCE pick — R, G and B read off the same physical pixel.
                // A private per-channel percentile (what this used to do, following Python) draws
                // the three channels from three different pixels, and on a Path A decouple roll the
                // matrix systematically lifts one channel's density, so that channel's independent
                // extreme is inflated, locks in as the wb_high base, and leaves white clouds yellow.
                // FilmBase.HighlightDensityFromRoll is the shared implementation that gets this
                // right (and masks the light board / opaque edges, which the centre-quarter crop
                // this replaced could only avoid by luck).
                double[] dHigh = FilmBase.HighlightDensityFromRoll(
                    new[] { anchorRaw }, tBase,
                    SprocketEnabled ? SprocketThreshold : null,
                    valueImages: ReferenceEquals(anchorRaw, anchorVal) ? null : new[] { anchorVal });

                // Step 1 — geometric baseline: wb_high so the highlight end inverts to flat white.
                double target = double.NegativeInfinity;
                for (int c = 0; c < 3; c++) target = Math.Max(target, dHigh[c] + wbOffset[c]);
                var wh = new double[3];
                for (int c = 0; c < 3; c++) wh[c] = (target - wbOffset[c]) / Math.Max(dHigh[c], 1e-10);
                // Debug, not Console: this is a WinExe with no console attached, so the writes went
                // nowhere a user could read. Debug.WriteLine reaches the debugger's output window
                // while developing and compiles out of Release entirely.
                Debug.WriteLine($"[AIWB] d_highlight={dHigh[0]:F4},{dHigh[1]:F4},{dHigh[2]:F4} " +
                                $"geo wb_high={wh[0]:F4},{wh[1]:F4},{wh[2]:F4} " +
                                $"grade={grade:F3} pivot={pivot:F3}");

                // Step 2 — NN chroma-only iteration.
                bool conv = false;
                for (int it = 1; it <= 50; it++)
                {
                    double dWbMax = double.NegativeInfinity;
                    for (int c = 0; c < 3; c++) dWbMax = Math.Max(dWbMax, dHigh[c] * wh[c] + wbOffset[c]);
                    double iterDMax = pivot * (1.0 - grade) + dWbMax * grade;

                    ImageBuffer pos = Pipeline.ProcessFrame(neg, BuildDeepWbRenderParams(wh, iterDMax));
                    var (inp, outp) = corr.CorrectOnce(pos);
                    var (li, lo) = MeanLinearHighlight(inp, outp);

                    var logGains = new double[3];
                    for (int c = 0; c < 3; c++)
                        logGains[c] = Math.Log10(Math.Max(Math.Max(lo[c], 1e-8) / Math.Max(li[c], 1e-8), 1e-8));
                    double meanLog = (logGains[0] + logGains[1] + logGains[2]) / 3.0;

                    // chroma-only density-slope delta (strip brightness on the delta itself).
                    var rawDelta = new double[3];
                    for (int c = 0; c < 3; c++) rawDelta[c] = (logGains[c] - meanLog) / (grade * Math.Max(dHigh[c], 1e-6));
                    double meanRaw = (rawDelta[0] + rawDelta[1] + rawDelta[2]) / 3.0;

                    double dev = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        wh[c] = Math.Max(wh[c] + (rawDelta[c] - meanRaw), 0.1);
                        dev = Math.Max(dev, Math.Abs(logGains[c] - meanLog));
                    }

                    Debug.WriteLine($"[AIWB] iter {it}: d_max={iterDMax:F4} log_gains=" +
                                    $"{logGains[0]:F4},{logGains[1]:F4},{logGains[2]:F4} dev={dev:F4} " +
                                    $"wb_high={wh[0]:F4},{wh[1]:F4},{wh[2]:F4}");
                    int round = it;
                    Dispatcher.UIThread.Post(() => StatusText = Loc.F($"智能白平衡 第 {round}/50 轮 · 收敛度 {dev:F4}"));
                    if (dev < 0.01) { conv = true; break; }
                }
                return (wh, conv);
            });

            WbHighR = wbHigh[0]; WbHighG = wbHigh[1]; WbHighB = wbHigh[2];   // AI only modifies wb_high
            StatusText = Loc.F($"智能白平衡{(converged ? "" : Loc.T("（未收敛，仅供参考）"))} → wb_high {wbHigh[0]:F3}, {wbHigh[1]:F3}, {wbHigh[2]:F3}");
        }
        catch (Exception ex) { StatusText = Loc.T("智能白平衡失败：") + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Per-channel mean of the sRGB-decoded LINEAR values of an sRGB positive.</summary>
    private static double[] MeanLinear(ImageBuffer srgb)
    {
        double r = 0, g = 0, b = 0; int n = srgb.PixelCount;
        float[] s = srgb.Data;
        for (int p = 0; p < n; p++) { r += ToLin(s[p * 3]); g += ToLin(s[p * 3 + 1]); b += ToLin(s[p * 3 + 2]); }
        return new[] { r / n, g / n, b / n };
    }

    private static double ToLin(float c) => c <= 0.04045f ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>
    /// The net's correction measured over the HIGHLIGHT BAND of its input, per channel, linear.
    ///
    /// This is the feedback signal the wb_high iteration closes its loop on, and the band matters
    /// more than anything else in the loop. wb_high is a highlight-end control: the delta derived
    /// from these gains is <c>log10(g)/(grade·d_highlight)</c>, i.e. it is sized to land EXACTLY at
    /// the highlight density and lands proportionally short everywhere darker. Measuring the gains
    /// as a whole-image mean (what this did before, following Python) therefore closes the loop on
    /// the wrong statistic: the loop only stops once the net is happy with the picture's OVERALL
    /// cast, and reaching that through a highlight-anchored control means the highlight itself has
    /// been pushed past neutral by roughly d_highlight/d_mean. On a frame with real white in it —
    /// clouds — the geometric baseline already had that white neutral, and the whole-image loop
    /// then walked it off into a cast (the reported yellow). Measuring where the control acts makes
    /// the loop consistent: a frame whose highlight is already white gets log_gains ≈ 0 on round 1,
    /// converges immediately, and keeps the geometric answer, while a frame with no true white
    /// still follows the net.
    ///
    /// Clipped pixels are excluded from BOTH frames: the render uses an adaptive d_max that puts
    /// the highlight right at 1.0, so the very top of the band carries no recoverable chroma and
    /// its ratio would read as a spurious 1.0. Falls back to the whole image when the band is too
    /// thin to be a statistic (a nearly-uniform or fully blown frame).
    /// </summary>
    private static (double[] In, double[] Out) MeanLinearHighlight(ImageBuffer inp, ImageBuffer outp)
    {
        const float Clip = 0.99f;       // treat as blown; no usable colour left
        const double BandPct = 98.0;    // top 2% of unclipped luma = "the highlight"
        const int MinPixels = 64;

        int n = inp.PixelCount;
        float[] a = inp.Data, b = outp.Data;

        var luma = new List<double>(n / 8);
        var eligible = new bool[n];
        for (int p = 0; p < n; p++)
        {
            int i = p * 3;
            if (a[i] >= Clip || a[i + 1] >= Clip || a[i + 2] >= Clip) continue;
            if (b[i] >= Clip || b[i + 1] >= Clip || b[i + 2] >= Clip) continue;
            eligible[p] = true;
            luma.Add(((double)a[i] + a[i + 1] + a[i + 2]) / 3.0);
        }

        if (luma.Count >= MinPixels)
        {
            luma.Sort();
            double thresh = luma[Math.Clamp((int)(BandPct / 100.0 * (luma.Count - 1)), 0, luma.Count - 1)];
            var si = new double[3];
            var so = new double[3];
            int k = 0;
            for (int p = 0; p < n; p++)
            {
                if (!eligible[p]) continue;
                int i = p * 3;
                if (((double)a[i] + a[i + 1] + a[i + 2]) / 3.0 < thresh) continue;
                for (int c = 0; c < 3; c++) { si[c] += ToLin(a[i + c]); so[c] += ToLin(b[i + c]); }
                k++;
            }
            if (k >= MinPixels)
            {
                for (int c = 0; c < 3; c++) { si[c] /= k; so[c] /= k; }
                return (si, so);
            }
        }
        return (MeanLinear(inp), MeanLinear(outp));
    }

    // ── Black / white eyedropper (levels endpoints on the rendered positive) ─────
    /// <summary>Sample the darkest luma in a rect → 黑场 slider.</summary>
    public void SampleBlack((double X, double Y, double W, double H) rect)
    {
        var mm = MinMaxLumaOfRenderedPositive(rect);
        if (mm is null) return;
        Black = WbMath.BlackPointToSlider(Math.Clamp(mm.Value.Min, 0.0, 0.5));
        StatusText = Loc.F($"黑场采样 → {Black:F2}");
    }

    /// <summary>Sample the brightest luma in a rect → 白场 slider.</summary>
    public void SampleWhite((double X, double Y, double W, double H) rect)
    {
        var mm = MinMaxLumaOfRenderedPositive(rect);
        if (mm is null) return;
        White = WbMath.WhitePointToSlider(Math.Clamp(mm.Value.Max, 0.5, 1.0));
        StatusText = Loc.F($"白场采样 → {White:F2}");
    }

    /// <summary>Auto black/white points from the 0.1 / 99.9 percentiles across all channels of the
    /// ungraded positive (port of Python levels.auto_levels) → 黑场 / 白场 sliders.</summary>
    public void AutoLevels()
    {
        if (_previewLinear is null) return;
        FrameParams p = BuildParams();
        p.BlackPoint = 0.0; p.WhitePoint = 1.0;   // measure the positive WITHOUT the current levels
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, p);
        var (black, white) = LevelsPercentiles(pos.Data, 0.001, 0.999);
        if (white - black < 1e-6) white = black + 1e-6;
        Black = WbMath.BlackPointToSlider(Math.Clamp(black, 0.0, 0.5));
        White = WbMath.WhitePointToSlider(Math.Clamp(white, 0.5, 1.0));
        StatusText = Loc.F($"自动色阶 → 黑场 {Black:F2} / 白场 {White:F2}");
    }

    /// <summary>Low/high percentiles over all RGB samples via a 4096-bin histogram on [0,1].</summary>
    private static (double Black, double White) LevelsPercentiles(float[] data, double lowPct, double highPct)
    {
        const int bins = 4096;
        var hist = new int[bins];
        foreach (float v in data)
        {
            int b = (int)(v * bins);
            hist[b < 0 ? 0 : b >= bins ? bins - 1 : b]++;
        }
        long n = data.Length;
        long lowTarget = (long)(n * lowPct), highTarget = (long)(n * highPct);
        double black = 0, white = 1; long acc = 0; bool gotBlack = false;
        for (int b = 0; b < bins; b++)
        {
            acc += hist[b];
            if (!gotBlack && acc >= lowTarget) { black = (b + 0.5) / bins; gotBlack = true; }
            if (acc >= highTarget) { white = (b + 0.5) / bins; break; }
        }
        return (black, white);
    }

    /// <summary>Grey-world gains that neutralise a mean colour to grey (relative to G).</summary>
    private static double[] GreyWorldGains(double[] mean)
    {
        double r = Math.Max(mean[0], 1e-6), g = Math.Max(mean[1], 1e-6), b = Math.Max(mean[2], 1e-6);
        return new[] { g / r, 1.0, g / b };
    }

    private double[]? MeanOfRenderedPositive((double X, double Y, double W, double H) rect)
        => _previewLinear is null ? null : RectMean(Pipeline.ProcessFrame(_previewLinear, BuildParams()), rect);

    private (double Min, double Max)? MinMaxLumaOfRenderedPositive((double X, double Y, double W, double H) rect)
    {
        if (_previewLinear is null) return null;
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, BuildParams());
        var (x0, y0, x1, y1) = PixelBounds(pos, rect);
        int w = pos.Width;
        float[] d = pos.Data;
        double min = double.MaxValue, max = double.MinValue;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 3;
                double luma = 0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2];
                if (luma < min) min = luma;
                if (luma > max) max = luma;
            }
        return (min, max);
    }

    /// <summary>Reset every adjustment to its neutral default and re-render.</summary>
    public void ResetAdjustments()
    {
        _renderCts?.Cancel();
        // Stage 1 — lens / sprocket / intent
        DistortionK1 = 0; VignetteAmount = 0; VignetteFalloff = 2.5;
        SprocketEnabled = false; SprocketThreshold = 0.9; OutputIntentIndex = 0;
        // Stage 1 — film base
        TBaseR = 0.82; TBaseG = 0.51; TBaseB = 0.29;
        DMax = 2.0; ScanEv = 0;
        WbOffR = 0; WbOffG = 0; WbOffB = 0;
        WbHighR = 1.0; WbHighG = 1.0; WbHighB = 1.0;
        GradePresetIndex = 1; IsManualGrade = false; Grade = 1.65; Pivot = 0.9;
        // Stage 2
        Temp = 0; Tint = 0; ExposureEv = 0;
        Black = 0; White = 0; Contrast = 0; Highlights = 0; Shadows = 0; Saturation = 0;
        _curveM = new(); _curveR = new(); _curveG = new(); _curveB = new(); _curvePreserveHue = true;
        // Geometry
        Rotation = 0; _quarterTurns = 0; _flipH = false; _flipV = false; _cropRect = null;
        FilmBaseText = Loc.T("片基：默认（未采样）");
        _filmBaseSampled = false;
        ScheduleRender();
    }

    // ── Import (roll = one or more frames) ──────────────────────────────────────
    public Task LoadAsync(string path) => LoadRollAsync(new[] { path });

    // ══ Catalog: the open roll's index entry + debounced autosave ═══════════════
    //
    // A roll is registered in the catalog the moment it is imported, and its .ncproj is written
    // beside the source images from then on without the user asking. There is no "save project"
    // step to forget: the file on disk is the roll, and the catalog is only an index pointing at
    // it (see Services/Catalog.cs for why that split matters).

    private Catalog.Roll? _roll;
    private readonly RollAutoSave _autoSave;

    // Two things are saved on the same idle pause, and they go stale independently: the project
    // file (data) and the cover contact sheet (cosmetic). Warm-up completing dirties only the
    // sheet — opening a roll to look at it must not rewrite its .ncproj and bump its 修改时间.
    private bool _rollDirty;
    private bool _sheetDirty;

    public MainViewModel()
    {
        _autoSave = new RollAutoSave(AutoSaveAsync);
        // The library edits roll info straight from a card. When that card IS the open roll, it
        // has to go through these live notes — the editor owns the project file meanwhile.
        Library.LiveNotesFor = id => _roll?.Id == id ? Notes : null;
        // Roll notes feed both the .ncproj and the roll list's subtitle, so editing them in the
        // contact-sheet dialog has to dirty the roll like any other change.
        Notes.PropertyChanged += (_, _) => MarkRollDirty();
        Loc.Changed += RetranslateText;
    }

    /// <summary>
    /// Re-resolve this view model's text after a language switch. One of these exists per run, so
    /// the static subscription in the constructor never needs unhooking.
    ///
    /// Only the IDLE text moves. A status line that reports something — 「已导出 12 帧」,
    /// 「自动 D-max = 2.031」, an exception message — describes an event that happened while the
    /// old language was in effect; restating it in the new one would be rewriting history, and
    /// half of these carry a number or a file name that no longer has a source to be rebuilt
    /// from. What must follow the switch is the text that is merely sitting there saying nothing
    /// has happened yet: those three labels are on screen from launch until the user acts, and in
    /// the empty-editor state they are most of what the window says.
    /// </summary>
    private void RetranslateText()
    {
        if (Frames.Count == 0) StatusText = Loc.T("打开一张负片（RAW 或 TIFF）开始。");
        if (!LccAvailable) LccStatus = Loc.T("未载入平场校正");
        if (!_filmBaseSampled) FilmBaseText = Loc.T("片基：默认（未采样）");
        foreach (RollFrame f in Frames) f.RefreshText();
    }

    /// <summary>The catalog entry for the open roll; null until something is imported.</summary>
    public Catalog.Roll? CurrentRoll => _roll;

    /// <summary>The 图库 module's state. Held here so both modules share one DataContext — they
    /// are two views of one session, not two windows.</summary>
    public LibraryViewModel Library { get; } = new();

    /// <summary>
    /// True = 图库 (the roll wall); false = 修片 (the editing view). Starts on the WALL: that is
    /// where a session begins — you pick a roll, or make one from the tile that leads it. Opening
    /// straight into an editing view with nothing loaded shows a set of controls that do nothing.
    /// Set to true up front rather than switched after the window appears, so there is no flash
    /// of the empty editor on the way in.
    /// </summary>
    [ObservableProperty] private bool _isLibraryMode = true;

    /// <summary>Show the roll wall. Flushes first: the wall is drawn from the catalog and the
    /// covers on disk, so a pending edit has to have landed before it is read back.</summary>
    public async Task EnterLibraryAsync()
    {
        await FlushRollAsync();
        await Library.RefreshAsync();
        IsLibraryMode = true;
    }

    /// <summary>Back to editing. Refuses when there is nothing open — an empty editing view with
    /// no roll is a dead end the user cannot get out of except by importing.</summary>
    public void EnterDevelop()
    {
        if (Frames.Count > 0) IsLibraryMode = false;
    }

    /// <summary>Something about the open roll changed — schedule the write. The cover is derived
    /// from the same state, so it goes with it.</summary>
    private void MarkRollDirty()
    {
        if (_roll is null) return;
        _rollDirty = true;
        _sheetDirty = true;
        _autoSave.MarkDirty();
    }

    /// <summary>
    /// The printed look changed (印样窗口 → 浅色/深色). Redraws the OPEN roll's cover; the other
    /// rolls' covers keep the look they were saved with until those rolls are next opened —
    /// re-covering the whole catalog would mean decoding every roll in it.
    /// </summary>
    public void OnSheetStyleChanged() => MarkSheetDirty();

    /// <summary>Only the cover needs redrawing (more frames finished decoding).</summary>
    private void MarkSheetDirty()
    {
        if (_roll is null) return;
        _sheetDirty = true;
        _autoSave.MarkDirty();
    }

    /// <summary>Register a freshly loaded roll in the catalog and give it a project file. Titled
    /// after its source folder — the roll number is deliberately NOT used, because notes carry
    /// over between imports and a new roll would inherit the previous roll's number.</summary>
    private void RegisterRoll(IReadOnlyList<string> paths)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? "";
        string title = dir.Length > 0 ? new DirectoryInfo(dir).Name : "";
        if (string.IsNullOrWhiteSpace(title)) title = Loc.T("未命名卷");

        _roll = new Catalog.Roll { Title = title, ProjectPath = Catalog.NewProjectPath(dir, title) };
        SyncRollEntry();
        _roll.ImportedAt = _roll.LastOpenedAt = DateTime.Now;
        Catalog.Upsert(_roll);
        _autoSave.MarkDirty();   // the first .ncproj lands on the next idle pause
    }

    /// <summary>Refresh the index entry's cached copy of what the roll list displays.</summary>
    private void SyncRollEntry()
    {
        if (_roll is null) return;
        _roll.RollNumber = Notes.RollNumber;
        _roll.FilmStock = Notes.FilmStock;
        _roll.CameraBody = Notes.CameraBody;
        _roll.DevDate = Notes.DevDate;
        _roll.FilmIso = Notes.FilmIso;
        _roll.DevLab = Notes.DevLab;
        _roll.DevProcess = Notes.DevProcess;
        _roll.Location = Notes.Location;
        _roll.Format = Notes.Format;
        _roll.FrameCount = Frames.Count;
        _roll.ModifiedAt = DateTime.Now;
    }

    /// <summary>
    /// Snapshot the whole roll as a serialisable project. Runs on the UI thread — it folds the
    /// live control values into the current frame — and CLONES every frame's params, so the
    /// caller can hand the result to a background writer while editing continues.
    /// </summary>
    private Project.Data BuildProjectData()
    {
        if (CurrentFrame is not null) CurrentFrame.Params = BuildParams();
        var data = new Project.Data
        {
            Meta = new Project.RollMeta
            {
                InputType = RollIsRaw ? "raw" : "tiff",
                SourcePath = _decoupleMatrix is not null ? "A" : "B",
                CalSourcePath = _calSourceDir,
                CalRgbPaths = _calRgbPaths is { Length: 3 } r
                    ? new Dictionary<string, string> { ["R"] = r[0], ["G"] = r[1], ["B"] = r[2] }
                    : null,
                LccPath = _lccSourcePath,
                CameraBody = Notes.CameraBody, FilmStock = Notes.FilmStock, FilmIso = Notes.FilmIso,
                RollNumber = Notes.RollNumber, DevLab = Notes.DevLab, DevProcess = Notes.DevProcess,
                DevDate = Notes.DevDate, Location = Notes.Location, RollNote = Notes.RollNote,
                Format = Notes.Format,
            },
        };
        foreach (RollFrame f in Frames)
            data.Frames.Add(new Project.Frame
            {
                SourcePath = f.Path, IsVirtual = f.IsVirtual, Params = f.Params.Clone(),
            });
        return data;
    }

    /// <summary>Persist whatever went stale: the project file, the cover sheet, or both.</summary>
    private async Task AutoSaveAsync()
    {
        if (_roll is null || Frames.Count == 0) return;

        if (_rollDirty)
        {
            Project.Data data = BuildProjectData();
            string path = _roll.ProjectPath;
            _rollDirty = false;
            try
            {
                await Task.Run(() => Project.Save(path, data));
            }
            catch (Exception ex)
            {
                _rollDirty = true;
                StatusText = Loc.T("自动保存失败：") + ex.Message;
                throw;   // RollAutoSave re-dirties and retries on the next idle pause
            }
            SyncRollEntry();
            Catalog.Upsert(_roll);
        }

        if (_sheetDirty)
        {
            _sheetDirty = false;
            // A cover is never worth failing the save over — but a silent failure means the roll
            // list quietly shows nothing, so say what happened.
            try { if (!await UpdateRollSheetAsync()) _sheetDirty = true; }   // refused → retry when warm
            catch (Exception ex)
            {
                _sheetDirty = true;
                StatusText = Loc.T("印样封面更新失败：") + ex.Message;
                Console.Error.WriteLine("[sheet] " + ex);
            }
        }
    }

    /// <summary>
    /// Redraw the roll's cover contact sheet from the resident tiles and store it.
    ///
    /// Every frame is re-rendered, not just the edited one: a tile is ~320 px, so the whole roll
    /// costs a few milliseconds per frame on a worker, and tracking which cell went stale would
    /// buy nothing but a way to get it wrong. What must NOT happen here is a decode — hence tiles
    /// rather than <see cref="BuildContactThumbsAsync"/>, which walks the roll through
    /// <see cref="PreviewAsync"/> and re-decodes anything the preview cache has since evicted.
    ///
    /// Frames still waiting on their first decode get a flat placeholder cell; the warm-up marks
    /// the sheet dirty again as they land, so the cover completes itself.
    ///
    /// Returns false when the redraw was refused as a downgrade (see <see cref="MayWriteCover"/>) —
    /// the caller has to leave the sheet dirty so it is tried again once the roll is warm.
    /// </summary>
    private async Task<bool> UpdateRollSheetAsync()
    {
        if (BuildSheetCells() is not { } cells) return false;
        string rollId = _roll!.Id;
        if (!MayWriteCover(rollId)) return false;
        var opt = new SheetComposer.Options { Style = Settings.Current.SheetStyle };

        List<ImageBuffer> thumbs = await Task.Run(() => RenderSheetCells(cells));
        SheetComposer.Grid grid = await Task.Run(() => SheetComposer.BuildGrid(thumbs, SheetLong(thumbs.Count), opt));
        using RenderTargetBitmap composed = SheetComposer.Compose(grid, Notes, opt);   // UI thread
        ImageBuffer sheet = SheetComposer.ToBuffer(composed);

        await Task.Run(() => SheetStore.Save(rollId, sheet));
        return true;
    }

    /// <summary>The same redraw, start to finish on the calling (UI) thread — for window close,
    /// where there is no time left to hop threads. ~150 ms for a 36-frame roll, which is the cost
    /// of not leaving yesterday's cover on the wall after an edit.</summary>
    private bool UpdateRollSheetNow()
    {
        if (BuildSheetCells() is not { } cells) return false;
        if (!MayWriteCover(_roll!.Id)) return false;
        var opt = new SheetComposer.Options { Style = Settings.Current.SheetStyle };
        List<ImageBuffer> thumbs = RenderSheetCells(cells);
        SheetComposer.Grid grid = SheetComposer.BuildGrid(thumbs, SheetLong(thumbs.Count), opt);
        using RenderTargetBitmap composed = SheetComposer.Compose(grid, Notes, opt);
        SheetStore.Save(_roll!.Id, SheetComposer.ToBuffer(composed));
        return true;
    }

    /// <summary>
    /// A cover drawn while the roll is still decoding is full of placeholder cells. That is fine
    /// for a roll that has no cover yet — a fresh import fills its card in progressively — but it
    /// must never REPLACE a finished cover: reopening an untouched old project re-decodes the whole
    /// roll (tiles are RAM-only), and stepping back into 图库 during those seconds used to flush a
    /// half-empty sheet over the good one, blanking the bottom of the card.
    ///
    /// Refusing here costs nothing: the warm-up marks the sheet dirty when it finishes, so an edit
    /// made mid-decode still reaches the cover — just at the end of the decode instead of during it.
    /// </summary>
    private bool MayWriteCover(string rollId) => _rollWarm || !SheetStore.Exists(rollId);

    /// <summary>Snapshot every cell's tile and params on the UI thread — both move under a
    /// background pass. Null when there is nothing worth drawing yet.</summary>
    private List<(ImageBuffer? Tile, FrameParams Params)>? BuildSheetCells()
    {
        if (_roll is null || Frames.Count == 0) return null;
        var cells = new List<(ImageBuffer? Tile, FrameParams Params)>(Frames.Count);
        foreach (RollFrame f in Frames)
            cells.Add((TileFor(f.Path), f.Params.Clone()));
        return cells.Any(c => c.Tile is not null) ? cells : null;   // nothing decoded yet
    }

    private static List<ImageBuffer> RenderSheetCells(List<(ImageBuffer? Tile, FrameParams Params)> cells)
    {
        // Placeholder cells borrow a real tile's dimensions so the grid geometry (median aspect)
        // is the one the finished sheet will have.
        ImageBuffer sample = cells.First(c => c.Tile is not null).Tile!;
        var list = new List<ImageBuffer>(cells.Count);
        foreach (var (tile, p) in cells)
            list.Add(tile is null ? Placeholder(sample.Width, sample.Height)
                                  : Pipeline.ProcessFrame(tile, p));
        return list;
    }

    /// <summary>
    /// Never upscale a tile. The grid is ceil(sqrt(n)) columns wide, so a full 36-frame roll lands
    /// at roughly tile resolution at 2048 — but a SHORT roll would stretch 320 px cells to a
    /// thousand and print a blurry cover. Capping at cols × tile width gives a smaller cover at
    /// native sharpness, which is the right trade for a card.
    /// </summary>
    private static int SheetLong(int frameCount)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(frameCount));
        return Math.Min(SheetStore.MaxLong, cols * TileMaxEdge);
    }

    /// <summary>A cell for a frame that has not been decoded yet — flat, in the sheet's own
    /// gap colour, so it reads as an empty slot rather than a black frame.</summary>
    private static ImageBuffer Placeholder(int w, int h)
    {
        float[] gap = SheetTheme.For(Settings.Current.SheetStyle).GapRgb;
        var buf = new ImageBuffer(w, h, new float[w * h * 3]);
        for (int i = 0; i < buf.Data.Length; i += 3)
        {
            buf.Data[i] = gap[0]; buf.Data[i + 1] = gap[1]; buf.Data[i + 2] = gap[2];
        }
        return buf;
    }

    /// <summary>Write a pending edit out right now (roll switch, export, shutdown).</summary>
    public Task FlushRollAsync() => _autoSave.FlushAsync();

    /// <summary>
    /// Synchronous flush for window close, where there is no time left to await anything: the app
    /// may be gone before a continuation would run. A 36-frame project is ~50 KB, so this costs
    /// single-digit milliseconds on the UI thread.
    /// </summary>
    public void FlushRollNow()
    {
        if (_roll is null || Frames.Count == 0) return;
        if (_rollDirty)
        {
            try
            {
                Project.Save(_roll.ProjectPath, BuildProjectData());
                _rollDirty = false;
                SyncRollEntry();
                Catalog.Upsert(_roll);
            }
            catch { /* closing down; nothing useful left to report */ }
        }
        // The cover too — OnClosing runs before the window is torn down, so the rasteriser is
        // still there. Skipping it used to mean an edit-then-close session left the wall showing
        // the previous cover until that roll was opened again.
        if (_sheetDirty)
        {
            _sheetDirty = false;
            try { UpdateRollSheetNow(); } catch { /* cosmetic; never hold up the close */ }
            // No retry to schedule here — the app is going away. A refused redraw simply leaves
            // the previous cover in place, which is the point of refusing.
        }
        _autoSave.Discard();
    }

    /// <summary>
    /// Asked when a project's negatives are not where it left them: gets the number of missing
    /// frames and the first missing file's name, returns a folder to look in, or null to open the
    /// roll as-is. Supplied by the window — this is a file picker, which a view model has no
    /// business owning.
    /// </summary>
    public Func<int, string, Task<string?>>? AskRelinkFolder;

    /// <summary>Ask where the negatives went, and re-point the project at them. The matching
    /// itself is <see cref="Project.Relink"/> — data surgery, testable without a file picker.
    /// Returns true when paths changed: that is the ONE thing a load may write back.</summary>
    private async Task<bool> RelinkIfMissingAsync(Project.Data data)
    {
        IReadOnlyList<string> missing = Project.MissingSources(data);
        if (missing.Count == 0 || AskRelinkFolder is null) return false;

        string? folder = await AskRelinkFolder(missing.Count, Path.GetFileName(missing[0]));
        if (string.IsNullOrEmpty(folder)) return false;

        int found = Project.Relink(data, folder);
        if (found == 0) { StatusText = Loc.T("所选文件夹里没有找到同名的底片"); return false; }

        StatusText = Loc.F($"已重新定位 {found}/{missing.Count} 个源文件");
        return true;   // the new paths have to be written back, or the fix is lost on exit
    }

    /// <summary>Reopen a roll from the catalog.</summary>
    public async Task OpenRollAsync(Catalog.Roll roll)
    {
        if (roll.Missing)
        {
            StatusText = Loc.F($"工程文件不存在：{roll.ProjectPath}");
            return;
        }
        await OpenProjectAsync(roll.ProjectPath);
    }

    /// <summary>Point the autosave at <paramref name="path"/>, reusing that project's existing
    /// catalog entry if it has one and registering it if it does not — opening a .ncproj from
    /// anywhere is how a roll gets adopted into the catalog.</summary>
    private void AdoptProject(string path)
    {
        _roll = Catalog.ByProjectPath(path) ?? new Catalog.Roll
        {
            Title = Path.GetFileNameWithoutExtension(path),
            ProjectPath = Path.GetFullPath(path),
            ImportedAt = DateTime.Now,
        };
        SyncRollEntry();
        _roll.LastOpenedAt = DateTime.Now;
        Catalog.Upsert(_roll);
    }

    // ── Project save / load (.ncproj, schema-compatible with Python) ────────────
    /// <summary>Write a COPY of the roll to an arbitrary path (「另存工程副本」). The open roll
    /// keeps autosaving to its own project file — this is for handing a roll to someone else or
    /// parking a variant, not for saving your work.</summary>
    public async Task SaveProjectAsync(string path)
    {
        if (Frames.Count == 0) return;
        Project.Data data = BuildProjectData();
        try
        {
            await Task.Run(() => Project.Save(path, data));
            StatusText = Loc.T("工程副本已保存：") + Path.GetFileName(path);
        }
        catch (Exception ex) { StatusText = Loc.T("工程保存失败：") + ex.Message; }
    }

    /// <summary>Open a .ncproj: recompute roll-level ops from the stored calibration source paths,
    /// rebuild every frame (real + virtual copies) with its saved params, and show the first.</summary>
    public async Task OpenProjectAsync(string path)
    {
        await FlushRollAsync();   // the outgoing roll's pending edit, before anything is replaced
        IsBusy = true;
        StatusText = Loc.T("正在打开工程 …");
        Project.Data data;
        try { data = await Task.Run(() => Project.Load(path)); }
        catch (Exception ex) { StatusText = Loc.T("打开工程失败：") + ex.Message; IsBusy = false; return; }
        if (data.Frames.Count == 0) { StatusText = Loc.T("工程为空"); IsBusy = false; return; }

        // Before anything reads pixels: the negatives may have moved since this was saved.
        bool relinked = await RelinkIfMissingAsync(data);

        _calSourceDir = data.Meta.CalSourcePath;
        _calRgbPaths = data.Meta.CalRgbPaths is { } r && r.ContainsKey("R")
            ? new[] { r["R"], r.GetValueOrDefault("G", ""), r.GetValueOrDefault("B", "") } : null;
        _lccSourcePath = data.Meta.LccPath;

        // Drop the previous roll's pixels HERE, not further down: the calibration below caches the
        // previews of every frame it decodes, and a later Clear() would throw that work away.
        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _previews.Clear(); ClearTiles(); _fullSlot = null; _regionSlot = null;
        lock (_decoding) _decoding.Clear();

        // Recompute the roll-level ops (never stored in the file) from their source paths.
        double[,]? dm = null, cm = null; ImageBuffer? lcc = null;
        try
        {
            var contentPaths = data.Frames.Where(f => !f.IsVirtual).Select(f => f.SourcePath).ToList();
            await Task.Run(() =>
            {
                string[]? rgb = _calRgbPaths is { Length: 3 } p && p.All(File.Exists) ? _calRgbPaths : null;
                if (rgb is null && !string.IsNullOrEmpty(_calSourceDir) && Directory.Exists(_calSourceDir))
                {
                    var (rp, gp, bp) = DecoupleCalibration.FindRgbCalFiles(_calSourceDir);
                    rgb = new[] { rp, gp, bp };
                    _calRgbPaths = rgb;
                }
                if (rgb is not null)
                    (dm, cm) = CalibratePathA(rgb, contentPaths);
                if (!string.IsNullOrEmpty(_lccSourcePath) && File.Exists(_lccSourcePath))
                {
                    ReportBackground(Loc.T("载入平场校正 …"));
                    lcc = Lcc.LoadFlatField(_lccSourcePath, tiffIsLinear: true);
                }
                ReportBackground("");
            });
        }
        catch (Exception ex) { StatusText = Loc.T("工程标定重算失败（按无解耦打开）：") + ex.Message; }

        _decoupleMatrix = dm; _decoupleChromaMatrix = cm;
        if (lcc is not null) { _lccFlatField = lcc; LccAvailable = true; LccStatus = Loc.T("已载入平场（工程）"); }
        else { _lccFlatField = null; LccAvailable = false; LccStatus = Loc.T("未载入平场校正"); }

        // Detach from the outgoing roll BEFORE its state is replaced — same reason as in
        // LoadRollAsync. Assigning the notes below fires Notes.PropertyChanged → MarkRollDirty,
        // which would dirty the roll being LEFT and then carry that flag into the one being
        // opened, so that merely looking at an old roll rewrote its .ncproj and bumped its
        // 修改时间. Opening is not an edit; the relink above is the only change a load can make.
        _roll = null;
        _rollDirty = false;
        _sheetDirty = false;

        // Roll notes.
        Notes.CameraBody = data.Meta.CameraBody; Notes.FilmStock = data.Meta.FilmStock;
        Notes.FilmIso = data.Meta.FilmIso; Notes.RollNumber = data.Meta.RollNumber;
        Notes.DevLab = data.Meta.DevLab; Notes.DevProcess = data.Meta.DevProcess;
        Notes.DevDate = data.Meta.DevDate; Notes.Location = data.Meta.Location;
        Notes.RollNote = data.Meta.RollNote; Notes.Format = data.Meta.Format;

        // Rebuild the roll (caches already cleared above, before calibration warmed them).
        _prevFrame = null;
        _pendingSprocketPrompt = false;
        _undo.Clear(); _redo.Clear(); _committed = null; UpdateUndoState();
        foreach (RollFrame f in Frames) Retire(f.Thumbnail);   // the outgoing roll's strip
        Frames.Clear();
        foreach (Project.Frame pf in data.Frames)
        {
            FrameParams fp = pf.Params;
            fp.DecoupleMatrix = dm; fp.DecoupleMode = DecoupleMode.Linear; fp.DecoupleChromaMatrix = cm;
            fp.LccFlatField = lcc;   // roll-uniform (matches import); global toggle gates it
            Frames.Add(new RollFrame(pf.SourcePath, pf.IsVirtual) { Params = fp });
        }
        LccEnabled = lcc is not null;
        IsBusy = false;
        CurrentFrame = Frames[0];   // triggers SwitchFrameAsync → decode + LoadParams + render

        // Autosave now tracks THIS project file. Adopted after the frames are in place so the
        // entry's frame count is the real one.
        _autoSave.Discard();
        AdoptProject(path);
        if (relinked) MarkRollDirty();   // the repaired paths, written back on the next idle pause

        StatusText = Loc.F($"工程已打开：{Path.GetFileName(path)}（{Frames.Count} 帧）");
        StartRollWarmUp();
        ReleaseBulkBuffers();   // the calibration/import full-res decodes are dead; uncommit them
        await Task.CompletedTask;
    }

    /// <summary>Open a roll from the import dialog: compute Path-A decouple + LCC (roll-level), then load.</summary>
    public async Task LoadRollWithConfigAsync(ImportConfig cfg)
    {
        if (cfg.Paths.Count == 0) return;
        IsBusy = true;
        StatusText = Loc.T("正在准备导入 …");
        // The roll changes here, not in LoadRollAsync — the prep below already caches previews for
        // the frames it decodes, and a later Clear() would throw that work away.
        _previews.Clear(); ClearTiles(); _fullSlot = null; _regionSlot = null;
        lock (_decoding) _decoding.Clear();

        double[,]? dm = null, cm = null;
        ImageBuffer? lccField = null; string lccName = "";
        string[]? calRgb = null;
        try
        {
            await Task.Run(() =>
            {
                if (cfg.PathA && !string.IsNullOrWhiteSpace(cfg.CalDir))
                {
                    ReportBackground(Loc.T("识别 R/G/B 校正图 …"));
                    var (rp, gp, bp) = DecoupleCalibration.FindRgbCalFiles(cfg.CalDir);
                    calRgb = new[] { rp, gp, bp };

                    (dm, cm) = CalibratePathA(calRgb, cfg.Paths);
                }
                if (cfg.LccEnabled && !string.IsNullOrWhiteSpace(cfg.LccPath))
                {
                    ReportBackground(Loc.T("载入平场校正 …"));
                    lccField = Lcc.LoadFlatField(cfg.LccPath, tiffIsLinear: true);
                    lccName = Path.GetFileName(cfg.LccPath);
                }
                ReportBackground("");
            });
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("导入准备失败：") + ex.Message;
            ReportBackground(""); IsBusy = false; return;
        }

        // Retain calibration SOURCE paths so a saved .ncproj can recompute matrices on load.
        _calSourceDir = cfg.PathA ? cfg.CalDir : null;
        _calRgbPaths = calRgb;
        _lccSourcePath = (cfg.LccEnabled && !string.IsNullOrWhiteSpace(cfg.LccPath)) ? cfg.LccPath : null;

        // Set roll-level ops BEFORE loading so the sprocket dialog + auto film-base (which run
        // during LoadRollAsync) sample t_base in the DECOUPLED domain and the first render decouples.
        _decoupleMatrix = dm; _decoupleChromaMatrix = cm;
        if (lccField is not null) { _lccFlatField = lccField; LccAvailable = true; LccStatus = Loc.T("已载入平场：") + lccName; }
        IsBusy = false;

        _configLoad = true;
        try { await LoadRollAsync(cfg.Paths); }
        finally { _configLoad = false; }

        // After the load, not before: a new roll resets its notes, which would wipe whatever the
        // import dialog just collected. Blank fields are left alone rather than written through,
        // so an untouched dialog cannot clear anything the roll already had.
        if (cfg.Notes.CameraBody is { Length: > 0 } camera) Notes.CameraBody = camera;
        if (cfg.Notes.FilmStock is { Length: > 0 } film) Notes.FilmStock = film;
        if (cfg.Notes.FilmIso is { Length: > 0 } iso) Notes.FilmIso = iso;
        if (cfg.Notes.RollNumber is { Length: > 0 } rollNumber) Notes.RollNumber = rollNumber;
        if (cfg.Notes.DevLab is { Length: > 0 } lab) Notes.DevLab = lab;
        if (cfg.Notes.DevProcess is { Length: > 0 } process) Notes.DevProcess = process;
        if (cfg.Notes.DevDate is { Length: > 0 } date) Notes.DevDate = date;
        if (cfg.Notes.Location is { Length: > 0 } location) Notes.Location = location;
        if (cfg.Notes.RollNote is { Length: > 0 } note) Notes.RollNote = note;
        if (cfg.Notes.Format is { Length: > 0 } format) Notes.Format = format;

        // Bake into every frame's stored params so export / thumbnails carry them.
        foreach (RollFrame f in Frames)
        {
            f.Params.DecoupleMatrix = dm;
            f.Params.DecoupleMode = DecoupleMode.Linear;
            f.Params.DecoupleChromaMatrix = cm;
            if (lccField is not null) f.Params.LccFlatField = lccField;
        }
        if (lccField is not null) LccEnabled = true;   // triggers a render
        ScheduleRender();
        RestartThumbnails();
        StatusText = Loc.F($"导入完成（{Frames.Count} 帧") +
                     (cfg.PathA ? Loc.T("，Path A 分光解耦") : "") + (lccField is not null ? Loc.T("，LCC 平场") : "") + "）";
    }

    /// <summary>
    /// Path A calibration: the decouple matrix plus the axis-accurate chroma compensation matrix
    /// (port of Python's compute_matrix_from_paths + _measure_chroma_amp).
    ///
    /// This is the longest wait before the first frame can appear, so the two things it needs —
    /// the 3 calibration frames and the first few content frames — are decoded in ONE parallel
    /// pass rather than as two sequential stages. Only the chroma MEASUREMENT depends on the
    /// matrix; the decodes it feeds on do not.
    ///
    /// Each decode releases its full buffer immediately: the calibration frames collapse to a
    /// centre-ROI mean, and the content frames to a 720 px sample buffer plus a cached preview.
    /// Keeping nine ~288 MB buffers alive at once is what a naive "decode everything first" would
    /// cost; this way the peak is only what is in flight.
    /// </summary>
    private (double[,] Dm, double[,] Cm) CalibratePathA(string[] calRgb, IReadOnlyList<string> paths)
    {
        int nF = Math.Min(6, paths.Count);
        var roi = new double[3][];                 // calibration ROI means
        var negs = new ImageBuffer[nF];            // content frames at 720, pre-decouple
        int done = 0, total = 3 + nF;

        ReportBackground(Loc.F($"解码校正图与内容帧 0/{total} …"));
        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 3, 1, 3),
        };
        Parallel.For(0, total, opts, i =>
        {
            if (i < 3)
            {
                // Full-quality decode: ComputeDecoupleMatrix only wants the centre-ROI mean, but
                // that mean is what the entire Path A colour basis rests on — its precision is not
                // negotiable. Streamed off the decoder, so the precision costs nothing in memory.
                roi[i] = ImageIo.RoiMeanFull(calRgb[i]);
            }
            else
            {
                int fi = i - 3;
                // Both sizes off ONE decode, neither of them via a full-resolution frame. These are
                // the roll's first frames — exactly what the warm-up would decode next — so their
                // previews go into the cache and that work is not paid for twice.
                var (outs, srcW, srcH) = ImageIo.LoadPreviews(paths[fi], PreviewMaxEdge, 720);
                _previews.Put(paths[fi], outs[0], srcW, srcH);
                negs[fi] = outs[1];
            }
            ReportBackground(Loc.F($"解码校正图与内容帧 {Interlocked.Increment(ref done)}/{total} …"));
        });

        ReportBackground(Loc.T("计算解耦矩阵与色度补偿 …"));
        double[,] dm = DecoupleCalibration.DecoupleMatrixFromRoiMeans(roi[0], roi[1], roi[2]);

        // Samples are concatenated in FRAME ORDER: ChromaAxisCompensationMatrix reduces these
        // arrays in float32, so the order they are appended in changes the resulting matrix.
        var preAll = new List<float>(); var postAll = new List<float>();
        for (int fi = 0; fi < nF; fi++)
        {
            ImageBuffer neg = negs[fi];
            if (neg is null) continue;
            var dec = new ImageBuffer(neg.Width, neg.Height, (float[])neg.Data.Clone());
            Decouple.Apply(dec.Data, dm, DecoupleMode.Linear);   // SAME (gamut-mapped) decouple the pipeline uses
            for (int p = 0; p < neg.PixelCount; p += 4)          // stride for speed
            {
                int i = p * 3;
                preAll.Add(neg.Data[i]); preAll.Add(neg.Data[i + 1]); preAll.Add(neg.Data[i + 2]);
                postAll.Add(dec.Data[i]); postAll.Add(dec.Data[i + 1]); postAll.Add(dec.Data[i + 2]);
            }
        }
        var preImg = new ImageBuffer(preAll.Count / 3, 1, preAll.ToArray());
        var postImg = new ImageBuffer(postAll.Count / 3, 1, postAll.ToArray());
        return (dm, DecoupleCalibration.ChromaAxisCompensationMatrix(preImg, postImg));
    }

    /// <summary>Open a roll: build a frame per file, show the first, decode thumbnails in the background.</summary>
    public async Task LoadRollAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        await FlushRollAsync();   // the outgoing roll's pending edit, before its frames are dropped
        _autoSave.Discard();
        // Detach from the outgoing roll BEFORE its frames are replaced: anything that dirties the
        // roll between here and RegisterRoll would otherwise be pointed at the old entry.
        _roll = null;
        _rollDirty = false;
        _sheetDirty = false;
        Notes.Reset();            // notes are per-roll; a new roll starts blank
        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _prevFrame = null;
        _pendingSprocketPrompt = true;
        if (!_configLoad)   // config path pre-sets roll-level ops before this call; don't wipe them
        {
            _decoupleMatrix = null; _decoupleChromaMatrix = null;
            _lccFlatField = null; LccAvailable = false; LccEnabled = false;
        }
        _undo.Clear(); _redo.Clear(); _committed = null; UpdateUndoState();
        if (!_configLoad)   // the config path already cleared, and has since cached real work
        {
            _previews.Clear(); ClearTiles(); _fullSlot = null; _regionSlot = null;   // never serve the previous roll's pixels
            lock (_decoding) _decoding.Clear();
        }
        foreach (RollFrame f in Frames) Retire(f.Thumbnail);   // the outgoing roll's strip
        Frames.Clear();
        foreach (string p in paths) Frames.Add(new RollFrame(p));
        CurrentFrame = Frames[0];   // triggers SwitchFrameAsync (decode + render)
        RegisterRoll(paths);        // new roll → new catalog entry + project file

        // Fire and forget: the import must return as soon as frame 1 is on screen. Awaiting the
        // roll here is what made importing feel like it hung — it did not come back until every
        // frame in the roll had been decoded.
        StartRollWarmUp();
        ReleaseBulkBuffers();   // the calibration/import full-res decodes are dead; uncommit them
        await Task.CompletedTask;
    }

    /// <summary>Decode the selected frame, load its params into the UI, render.</summary>
    private async Task SwitchFrameAsync(RollFrame frame)
    {
        IsBusy = true;
        StatusText = Loc.F($"正在解码 {frame.FileName} …");
        int tok = ++_switchToken;
        try
        {
            // Cache hit → no decode at all; otherwise join whoever is already decoding this file.
            // Re-selecting a frame (or a virtual copy, which shares its parent's path) must never
            // pay for LibRaw again.
            PreviewCache.Entry entry = await PreviewAsync(frame.Path);
            if (tok != _switchToken) return;   // superseded by a newer switch
            _previewLinear = entry.Preview;
            // Release the export buffer when we leave its frame: it is ~288 MB at 24 MP and close
            // to a gigabyte at 80 MP, and nothing but an export of THAT frame will ever read it.
            // Staying on one frame still exports → tweak → re-exports on a single decode.
            if (_fullSlot is { } slot &&
                !string.Equals(slot.Path, frame.Path, StringComparison.OrdinalIgnoreCase))
                _fullSlot = null;
            if (_regionSlot is { } rs &&
                !string.Equals(rs.Path, frame.Path, StringComparison.OrdinalIgnoreCase))
                _regionSlot = null;
            FileName = frame.FileName;
            HasImage = true;
            LoadParams(frame.Params);          // sets UI (suppressed) + renders
            int idx = Frames.IndexOf(frame);
            StatusText = $"{FileName} — {entry.SourceWidth}×{entry.SourceHeight}（{idx + 1}/{Frames.Count}）";
            if (!_restoring) SetUndoBaseline();   // new frame's state is the fresh undo baseline
            UpdateSprocketOverlay();              // refresh the mask overlay for the new frame
            if (_pendingSprocketPrompt) { _pendingSprocketPrompt = false; RollImported?.Invoke(); }
        }
        catch (Exception ex)
        {
            if (tok == _switchToken) { StatusText = Loc.T("打开失败：") + ex.Message; HasImage = false; }
        }
        finally { if (tok == _switchToken) IsBusy = false; }
    }

    /// <summary>Push a frame's stored FrameParams into all the UI controls (suppressing renders).</summary>
    private void LoadParams(FrameParams p)
    {
        _suppressRender = true;
        // Stage 1 — lens / sprocket / intent
        DistortionK1 = p.DistortionK1; VignetteAmount = p.VignetteAmount; VignetteFalloff = p.VignetteFalloff;
        LccEnabled = p.LccFlatField != null;
        SprocketEnabled = p.SprocketEnabled; SprocketThreshold = p.SprocketThreshold ?? 0.9;
        OutputIntentIndex = p.OutputIntent == OutputIntent.Basic ? 0 : 1;
        // Stage 1 — film base
        TBaseR = p.TBase[0]; TBaseG = p.TBase[1]; TBaseB = p.TBase[2];
        DMax = p.DMax; ScanEv = p.ScanExposureEv;
        WbOffR = p.WbOffset[0]; WbOffG = p.WbOffset[1]; WbOffB = p.WbOffset[2];
        WbHighR = p.WbHigh[0]; WbHighG = p.WbHigh[1]; WbHighB = p.WbHigh[2];
        // Grade preset first (its handler may set grade/pivot), then override with the stored values.
        GradePresetIndex = WbMath.GradeToPresetIndex(p.Grade);
        IsManualGrade = GradePresetIndex == WbMath.GradePresets.Length - 1;
        Grade = p.Grade; Pivot = p.Pivot;
        // Stage 2
        var (temp, tint, _) = WbMath.GainsToTempTint(p.WbGains);
        Temp = Math.Clamp(temp, -WbMath.WbRange, WbMath.WbRange);
        Tint = Math.Clamp(tint, -WbMath.WbRange, WbMath.WbRange);
        ExposureEv = p.ExposureEv;
        Black = WbMath.BlackPointToSlider(p.BlackPoint);
        White = WbMath.WhitePointToSlider(p.WhitePoint);
        Contrast = p.Contrast; Highlights = p.Highlights; Shadows = p.Shadows; Saturation = p.Saturation;
        _curveM = new List<(double, double)>(p.CurvePointsM);
        _curveR = new List<(double, double)>(p.CurvePointsR);
        _curveG = new List<(double, double)>(p.CurvePointsG);
        _curveB = new List<(double, double)>(p.CurvePointsB);
        _curvePreserveHue = p.CurvePreserveHue;
        // Geometry
        Rotation = p.Rotation; _quarterTurns = p.QuarterTurns; _flipH = p.FlipH; _flipV = p.FlipV;
        _cropRect = p.CropRect;
        FilmBaseText = Loc.F($"片基 t_base = {p.TBase[0]:F3}, {p.TBase[1]:F3}, {p.TBase[2]:F3}");
        _filmBaseSampled = true;
        _suppressRender = false;

        FrameParamsLoaded?.Invoke(p);   // view syncs the curve editor
        ScheduleRender();
    }

    // ── Film-strip thumbnails ───────────────────────────────────────────────────
    private const int ThumbMaxEdge = 256;

    /// <summary>
    /// Regenerate missing thumbnails. Decodes ONLY for frames that have neither a resident sheet
    /// tile nor a cached preview — after the roll warm-up that is none of them, so a parameter
    /// broadcast re-renders the strip instead of re-decoding the whole roll (it used to pay a RAW
    /// decode per frame, every single time anything was applied to the roll).
    ///
    /// The tile is tried FIRST because it is the only one of the two that cannot be evicted: on a
    /// long roll the preview cache drops earlier frames, and 「应用标定到整卷」 would then decode
    /// them again just to redraw a 256 px thumbnail.
    /// </summary>
    private async Task DecodeThumbnailsAsync(CancellationToken ct)
    {
        foreach (RollFrame f in Frames.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (f.Thumbnail is not null) continue;
            try
            {
                // Join the SHARED decode rather than starting a private one. This loop runs
                // during import, at the same time as the roll warm-up, on the same files, and a
                // private decode would not go through PreviewAsync's in-flight table — the roll
                // used to be decoded twice over, with both halves competing for the same decode
                // slots. Awaiting PreviewAsync joins whatever is already running (returning
                // immediately on a cache hit) and caches the result for everyone else.
                ImageBuffer source = TileFor(f.Path)
                                     ?? _previews.Get(f.Path)?.Preview
                                     ?? (await PreviewAsync(f.Path).WaitAsync(ct)).Preview;
                await RenderThumbnailAsync(f, source, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* skip undecodable frame */ }
        }
    }

    /// <summary>Render one frame's thumbnail off an already-decoded preview. Never decodes: every
    /// caller resolves the preview through <see cref="PreviewAsync"/> first, so the strip and the
    /// main view are guaranteed to be looking at the same pixels.</summary>
    private async Task RenderThumbnailAsync(RollFrame f, ImageBuffer preview, CancellationToken ct)
    {
        FrameParams p = f.Params;
        Bitmap bmp = await Task.Run(() =>
        {
            ImageBuffer small = Resample.Box(preview, ThumbMaxEdge);
            return (Bitmap)BitmapConvert.ToBitmap(Pipeline.ProcessFrame(small, p));
        }, ct);
        if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
        await Dispatcher.UIThread.InvokeAsync(() => SetThumbnail(f, bmp));
    }

    /// <summary>Regenerate a frame's thumbnail from the in-memory preview (no re-decode).</summary>
    private void RefreshThumbnail(RollFrame frame)
    {
        if (_previewLinear is null) return;
        ImageBuffer prev = Resample.Box(_previewLinear, ThumbMaxEdge);
        SetThumbnail(frame, BitmapConvert.ToBitmap(Pipeline.ProcessFrame(prev, frame.Params)));
    }

    private void RestartThumbnails()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        _ = DecodeThumbnailsAsync(_thumbCts.Token);
    }

    /// <summary>
    /// ONE background pass per roll: decode each unique source once, cache its preview, and build
    /// the thumbnails from that same preview. Mirrors the Python GUI's _UpgradeWorker.
    ///
    /// It is one pass on purpose. Decoding the roll twice — half-size for the strip, full for the
    /// preview cache — is what made import crawl, and the thumbnails come out better this way
    /// (1600 px box-downsampled to 256 beats a half-size decode downsampled to 256).
    ///
    /// Uses the same full <see cref="ImageIo.LoadLinear"/> as <see cref="SwitchFrameAsync"/>, NOT
    /// the half-size thumbnail decode: whether the warm-up or the switch got there first must not
    /// change which pixels you see, or the preview (and everything sampled off it) becomes a race.
    /// </summary>
    private async Task WarmRollAsync(CancellationToken ct)
    {
        // Start at the current frame and walk outward: the neighbours get visited next.
        List<RollFrame> frames = Frames.ToList();
        if (frames.Count == 0) return;
        int start = Math.Max(0, CurrentFrame is { } cur ? frames.IndexOf(cur) : 0);

        // Virtual copies share their parent's path — decode it once for all of them.
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < frames.Count; i++)
        {
            string p = frames[(start + i) % frames.Count].Path;
            if (seen.Add(p)) order.Add(p);
        }

        // A few workers, not one per core: each in-flight decode holds a few hundred MB
        // transiently, and the UI still needs a core to stay responsive while this runs.
        var opts = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 3, 1, 3),
        };

        try
        {
            int done = 0, total = order.Count;
            ReportBackground(Loc.F($"后台解码 0/{total} …"));
            await Parallel.ForEachAsync(order, opts, async (path, token) =>
            {
                PreviewCache.Entry entry;
                try { entry = await PreviewAsync(path).WaitAsync(token); }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref done); return; }   // undecodable → skip, keep going

                // Publish each frame's thumbnail the moment its decode lands, rather than at the
                // end of the roll — the strip fills in progressively instead of all at once.
                List<RollFrame> targets = await Dispatcher.UIThread.InvokeAsync(() =>
                    Frames.Where(f => f.Thumbnail is null &&
                                      string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                          .ToList());
                foreach (RollFrame f in targets)
                {
                    if (token.IsCancellationRequested) return;
                    try { await RenderThumbnailAsync(f, entry.Preview, token); }
                    catch (OperationCanceledException) { return; }
                    catch { /* one bad thumbnail must not stop the roll */ }
                }

                int n = Interlocked.Increment(ref done);
                ReportBackground(n >= total ? "" : Loc.F($"后台解码 {n}/{total} …"));
                // Each landed decode leaves one more tile behind, so the cover can fill in.
                await Dispatcher.UIThread.InvokeAsync(MarkSheetDirty);
            });
            ReportBackground("");
            // The roll is as decoded as it will get (frames that failed above stay tile-less, and
            // waiting for them would mean never). Mark once more: a cover redraw that was refused
            // while the roll was half-decoded is now allowed, and this is what asks for it again.
            await Dispatcher.UIThread.InvokeAsync(() => { _rollWarm = true; MarkSheetDirty(); });
        }
        catch (OperationCanceledException) { /* roll changed under us */ }
        finally { if (!ct.IsCancellationRequested) ReportBackground(""); }
    }

    /// <summary>Set <see cref="BackgroundStatus"/> from any thread. Separate from
    /// <see cref="StatusText"/> on purpose: progress must not overwrite the frame name, and the
    /// frame name must not overwrite progress.</summary>
    private void ReportBackground(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) BackgroundStatus = text;
        else Dispatcher.UIThread.Post(() => BackgroundStatus = text);
    }

    /// <summary>Kick off (or restart) the roll warm-up. Its own CTS: <see cref="RestartThumbnails"/>
    /// fires on every roll-wide parameter change, and sharing a token with it used to cancel the
    /// warm-up mid-import — the sprocket dialog alone was enough to kill it permanently.</summary>
    private void StartRollWarmUp()
    {
        _rollWarm = false;
        _warmCts?.Cancel();
        _warmCts = new CancellationTokenSource();
        _ = WarmRollAsync(_warmCts.Token);
    }

    // ── Broadcast / copy-paste (fields gated by SyncOptions) ────────────────────
    public SyncOptions Sync { get; } = new();
    private FrameParams? _calClipboard, _sceneClipboard;
    [ObservableProperty] private bool _hasCalClipboard;
    [ObservableProperty] private bool _hasSceneClipboard;

    /// <summary>Copy Stage-1 calibration (per SyncOptions) from the current frame to every other frame.</summary>
    public void ApplyCalibrationToRoll() => Broadcast(cal: true, scene: false, onlySelected: false, Loc.T("标定"));

    /// <summary>Copy Stage-2 scene adjustments (per SyncOptions) from the current frame to every other frame.</summary>
    public void ApplySceneToRoll() => Broadcast(cal: false, scene: true, onlySelected: false, Loc.T("场景"));

    public void CopyCalibration() { _calClipboard = BuildParams(); HasCalClipboard = true; StatusText = Loc.T("已复制标定"); }
    public void CopyScene() { _sceneClipboard = BuildParams(); HasSceneClipboard = true; StatusText = Loc.T("已复制场景"); }

    /// <summary>Paste the copied calibration onto the ticked frames.</summary>
    public void PasteCalibrationToSelected() => Paste(_calClipboard, cal: true, scene: false, Loc.T("标定"));
    public void PasteSceneToSelected() => Paste(_sceneClipboard, cal: false, scene: true, Loc.T("场景"));

    /// <summary>
    /// Paste onto the CURRENT frame only — the one-to-one case the roll-wide broadcasts could not
    /// express. Matching one frame to another was otherwise a matter of ticking it in the strip,
    /// pasting to "selected", and then unticking it.
    /// </summary>
    public void PasteCalibrationToCurrent() => PasteToCurrent(_calClipboard, cal: true, scene: false, Loc.T("标定"));
    public void PasteSceneToCurrent() => PasteToCurrent(_sceneClipboard, cal: false, scene: true, Loc.T("场景"));

    private void PasteToCurrent(FrameParams? clip, bool cal, bool scene, string what)
    {
        if (CurrentFrame is null) return;
        if (clip is null) { StatusText = Loc.F($"尚未复制{what}"); return; }
        CommitUndo();   // close the previous edit as its own undo step

        // Through BuildParams, not the stored params: the live control values are the truth for
        // the frame on screen, and pasting must not silently discard an uncommitted tweak to a
        // group the paste does not cover.
        FrameParams target = BuildParams();
        CopyGroups(clip, target, cal, scene);
        CurrentFrame.Params = target;
        LoadParams(target);          // push the result back into the controls
        SetThumbnail(CurrentFrame, null);
        StatusText = Loc.F($"已粘贴{what}到当前帧");
        MarkEdit();
        ScheduleRender();
        RestartThumbnails();
    }

    private void Broadcast(bool cal, bool scene, bool onlySelected, string what)
    {
        if (CurrentFrame is null) return;
        CommitUndo();   // close the previous edit as its own undo step
        FrameParams src = BuildParams();
        CurrentFrame.Params = src;
        int n = 0;
        foreach (RollFrame f in Frames)
        {
            if (ReferenceEquals(f, CurrentFrame)) continue;
            if (onlySelected && !f.IsSelected) continue;
            CopyGroups(src, f.Params, cal, scene);
            SetThumbnail(f, null); n++;
        }
        StatusText = onlySelected ? Loc.F($"已把{what}应用到选中 {n} 帧") : Loc.F($"已应用{what}到整卷（{n} 帧）");
        MarkEdit();
        RestartThumbnails();
    }

    private void Paste(FrameParams? clip, bool cal, bool scene, string what)
    {
        if (clip is null) { StatusText = Loc.F($"尚未复制{what}"); return; }
        CommitUndo();
        int n = 0;
        foreach (RollFrame f in Frames)
        {
            if (!f.IsSelected || ReferenceEquals(f, CurrentFrame)) continue;
            CopyGroups(clip, f.Params, cal, scene);
            SetThumbnail(f, null); n++;
        }
        StatusText = n == 0 ? Loc.F($"没有选中的目标帧（在胶片条勾选帧）") : Loc.F($"已把{what}粘贴到 {n} 帧");
        MarkEdit();
        RestartThumbnails();
    }

    /// <summary>Copy the SyncOptions-enabled field groups from s into d.</summary>
    private void CopyGroups(FrameParams s, FrameParams d, bool cal, bool scene)
    {
        if (cal)
        {
            if (Sync.CalFilmBase)
            {
                d.TBase = (double[])s.TBase.Clone(); d.DMax = s.DMax; d.ScanExposureEv = s.ScanExposureEv;
                // Same rule as OnDMaxChanged, at the one place d_max moves WITHOUT going through it:
                // broadcasting 片基 but not 反差 would hand the target a new d_max on top of a pivot
                // derived from its old one, stranding the mid-tone anchor. Only for a target sitting
                // on a preset grade — a grade that matches no preset is the same signal LoadParams
                // reads as 手动, and manual pivot is the user's to own.
                if (!Sync.CalGrade
                    && WbMath.GradeToPresetIndex(d.Grade) != WbMath.GradePresets.Length - 1)
                    d.Pivot = WbMath.LinkedPivot(d.DMax);
            }
            if (Sync.CalWb) { d.WbHigh = (double[])s.WbHigh.Clone(); d.WbOffset = (double[])s.WbOffset.Clone(); }
            if (Sync.CalGrade) { d.Grade = s.Grade; d.Pivot = s.Pivot; d.ChromaGrade = s.ChromaGrade; d.ChromaChannelScale = (double[])s.ChromaChannelScale.Clone(); }
            if (Sync.CalLens) { d.DistortionK1 = s.DistortionK1; d.VignetteAmount = s.VignetteAmount; d.VignetteFalloff = s.VignetteFalloff; d.LccFlatField = s.LccFlatField; }
            if (Sync.CalSprocket) { d.SprocketEnabled = s.SprocketEnabled; d.SprocketThreshold = s.SprocketThreshold; }
            d.OutputIntent = s.OutputIntent;   // intent is roll-uniform
        }
        if (scene)
        {
            if (Sync.SceneWb) d.WbGains = (double[])s.WbGains.Clone();
            if (Sync.SceneExposure) d.ExposureEv = s.ExposureEv;
            if (Sync.SceneTone)
            {
                d.BlackPoint = s.BlackPoint; d.WhitePoint = s.WhitePoint; d.Contrast = s.Contrast;
                d.Highlights = s.Highlights; d.Shadows = s.Shadows; d.Saturation = s.Saturation;
            }
            if (Sync.SceneCurves)
            {
                d.CurvePointsM = new List<(double, double)>(s.CurvePointsM);
                d.CurvePointsR = new List<(double, double)>(s.CurvePointsR);
                d.CurvePointsG = new List<(double, double)>(s.CurvePointsG);
                d.CurvePointsB = new List<(double, double)>(s.CurvePointsB);
                d.CurvePreserveHue = s.CurvePreserveHue;
            }
        }
        if (Sync.GeomOrientation) { d.QuarterTurns = s.QuarterTurns; d.FlipH = s.FlipH; d.FlipV = s.FlipV; }
        if (Sync.GeomStraighten) d.Rotation = s.Rotation;
        if (Sync.GeomCrop) d.CropRect = s.CropRect;
    }

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
        if (CurrentFrame is not null) CurrentFrame.Params = BuildParams();
        FrameParams template = (CurrentFrame?.Params ?? new FrameParams()).Clone();
        RollFrame.ResetScene(template);
        template.CropRect = null; template.Rotation = 0;   // geometry is per-scan; don't inherit crop/straighten

        foreach (string p in toAdd)
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
        parent.Params = BuildParams();   // capture live edits into the parent first
        RollFrame copy = RollFrame.MakeVirtualCopy(parent);
        int pos = Frames.IndexOf(parent) + 1;
        Frames.Insert(pos, copy);
        ResetUndoAfterStructural();
        CurrentFrame = copy;             // switch to the copy so it can be adjusted immediately
        StatusText = Loc.T("已创建虚拟副本（继承标定、场景已重置）");
        RestartThumbnails();
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
    private static void WriteExport(ImageBuffer img, string path, FrameParams p, ExportOptions opt)
    {
        // Downsample AFTER the render, not before: averaging finished pixels supersamples them,
        // whereas shrinking the negative first would throw away detail the render still needed
        // — and would move every Stage-1 measurement with it.
        ImageBuffer outImg = opt.Downsample ? Resample.Box(img, opt.MaxLongEdge) : img;
        // NONE intent writes linear data. No profile offered here describes that, so an "embed"
        // request is honoured only where it would be true.
        ColorSpace? icc = opt.EmbedIcc && p.OutputIntent == OutputIntent.Basic ? ColorSpace.Srgb : null;
        if (opt.Format == ExportFormat.Jpeg) JpegIO.ExportJpeg(outImg, path, opt.JpegQuality, icc: icc);
        else TiffIO.ExportTiff16(outImg, path, opt.TiffCompression, icc);
    }

    /// <summary>Export every frame at full resolution into a folder, each with its own params.</summary>
    public async Task ExportRollAsync(string folder, ExportOptions opt)
    {
        if (Frames.Count == 0) return;
        if (CurrentFrame is not null) CurrentFrame.Params = BuildParams();   // capture live edits
        IsBusy = true;
        try
        {
            var frames = Frames.ToList();
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
                await Task.Run(() => WriteExport(Pipeline.ProcessFrame(ImageIo.LoadLinear(f.Path), p),
                                                 outPath, p, opt));
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
        if (CurrentFrame is not null) CurrentFrame.Params = BuildParams();
        IsBusy = true;
        StatusText = Loc.T("正在生成印样 …");
        try
        {
            var frames = Frames.ToList();
            int done = 0, total = frames.Count;
            var sources = new ImageBuffer[total];
            // Warm previews first, on the shared decode path — this used to re-decode the entire
            // roll at full resolution just to shrink each frame to 900 px.
            for (int i = 0; i < total; i++)
            {
                sources[i] = (await PreviewAsync(frames[i].Path)).Preview;
                ReportBackground(Loc.F($"印样 {++done}/{total} …"));
            }

            List<ImageBuffer> thumbs = await Task.Run(() =>
            {
                var t = new List<ImageBuffer>(total);
                for (int i = 0; i < total; i++)
                    t.Add(Pipeline.ProcessFrame(Resample.Box(sources[i], 900), frames[i].Params));
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
                                              string path)
    {
        IsBusy = true;
        StatusText = Loc.T("正在导出印样 …");
        try
        {
            var opt = new SheetComposer.Options { Style = style };
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
                if (ext is ".jpg" or ".jpeg")
                    JpegIO.ExportJpeg(outImg, path, quality: 92);
                else
                    TiffIO.ExportTiff16(outImg, path, TiffIO.CompressionMode.Lzw, ColorSpace.Srgb);
            });
            StatusText = Loc.F($"印样已导出：{Path.GetFileName(path)}（{outImg.Width}×{outImg.Height}）");
        }
        catch (Exception ex) { StatusText = Loc.T("印样导出失败：") + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>The current frame at full resolution, decoded on demand. Nothing else in the GUI
    /// needs full-res, so this is the only place it is paid for; the single slot means an
    /// export → tweak → re-export loop on the same frame decodes once, and switching frames
    /// releases the buffer instead of accumulating hundreds of MB per visited frame.</summary>
    private ImageBuffer LoadFullLinear(string sourcePath)
    {
        lock (_fullSlotGate)
        {
            if (_fullSlot is { } slot && string.Equals(slot.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
                return slot.Buf;
            ImageBuffer full = ImageIo.LoadLinear(sourcePath);
            _fullSlot = new FullSlot(sourcePath, full);
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

    // ── Export (full resolution) ────────────────────────────────────────────────
    public async Task ExportAsync(string path, ExportOptions opt)
    {
        if (CurrentFrame is not { } frame || !HasImage) return;
        IsBusy = true;
        StatusText = Loc.T("正在导出 …");
        try
        {
            FrameParams p = BuildParams();
            string srcPath = frame.Path;
            // Overwrite stays the rule for a single export: the save dialog already asked, and the
            // format came from the options dialog rather than being guessed from the extension.
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } outDir) ExportFile.CleanupStale(outDir);
            await Task.Run(() => WriteExport(Pipeline.ProcessFrame(LoadFullLinear(srcPath), p), path, p, opt));
            StatusText = Loc.F($"已导出：{Path.GetFileName(path)} · {opt.Summary()}");
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("导出失败：") + ex.Message;
        }
        finally { IsBusy = false; ReleaseBulkBuffers(); }
    }

    // ══ Sharp patch (local full-resolution zoom) ════════════════════════════════
    //
    // The cached preview is box-downsampled 6× on a 60 MP frame, so zooming magnifies preview
    // pixels and invents nothing — focus, grain and sharpness are simply not visible in the GUI.
    // Past a zoom threshold the view asks for the visible slice to be re-rendered from the
    // ORIGINAL pixels (RegionRender), and blits it over the corresponding part of the preview.
    //
    // Ported from the Python GUI's _HiresWorker, including the two things that make it feel
    // right rather than merely correct: the request carries a pan margin so small movements
    // reuse the patch, and the RESULT carries the rectangle it actually covers — the request is
    // rounded to whole displayed pixels, and blitting against the asked-for rect instead of the
    // realised one lands the patch fractionally off, where it shimmers against the preview.

    /// <summary>A rendered patch and the normalised rect of the displayed frame it covers.</summary>
    public sealed record SharpPatch(Bitmap Image, double X, double Y, double W, double H);

    // Property is `Patch`, not `SharpPatch` — a generated property may not share its name
    // with the type it holds.
    [ObservableProperty] private SharpPatch? _patch;

    partial void OnPatchChanging(SharpPatch? oldValue, SharpPatch? newValue)
    {
        if (!ReferenceEquals(oldValue?.Image, newValue?.Image)) Retire(oldValue?.Image);
    }

    private CancellationTokenSource? _patchCts;
    private int _patchToken;

    // Single-flight, with a trailing re-fire — the same shape as the source's _hires_pending.
    //
    // A wheel gesture is a dozen notches in a second and each one asks for a patch. Cancelling
    // the previous request is not enough: cancellation is only observed BETWEEN steps, so
    // several requests sail into the full-resolution decode together, and before the lock in
    // LoadFullLinear they each decoded their own ~690 MB copy. Measured: the process reached
    // 3.8 GB after one wheel gesture.
    //
    // So at most one render runs. Anything asked for while it does is remembered as the LATEST
    // wanted region and fired once, when the running one finishes — the intermediate zoom levels
    // the wheel passed through are never worth rendering anyway.
    private bool _patchRunning;
    private RegionRender.Roi? _patchQueued;

    /// <summary>The decoded source rectangle the last patches were built from, kept so a small
    /// pan does not re-decode. Frame-space origin + the pixels.</summary>
    private sealed record RegionSlot(string Path, ImageBuffer Buf, int X0, int Y0, int X1, int Y1);
    private RegionSlot? _regionSlot;

    /// <summary>
    /// How much bigger than the strictly-needed rectangle to decode, as a fraction of its size
    /// on each side. Pure pan buffer: the decode's unpack stage is whole-file and irreducible
    /// (~1.07 s of a 1.14 s region decode on a 60 MP ARW), so re-decoding for every nudge is
    /// what would make panning unusable — whereas the extra pixels cost almost nothing.
    /// </summary>
    private const double RegionPanMargin = 0.35;

    /// <summary>
    /// The decoded slice covering <paramref name="need"/>, from the cache when it already does.
    /// Null means this source cannot be region-decoded and the caller should fall back.
    /// Runs on the patch worker thread; the slot is only touched from there and from the
    /// UI-thread invalidation points, which never overlap because patches are single-flight.
    /// </summary>
    private ImageBuffer? RegionSliceFor(string path, (int X0, int Y0, int X1, int Y1) need,
                                        int frameW, int frameH)
    {
        if (_regionSlot is { } s && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
            && s.X0 <= need.X0 && s.Y0 <= need.Y0 && s.X1 >= need.X1 && s.Y1 >= need.Y1)
            return s.Buf;

        int mw = (int)((need.X1 - need.X0) * RegionPanMargin);
        int mh = (int)((need.Y1 - need.Y0) * RegionPanMargin);
        int x0 = Math.Max(0, need.X0 - mw), y0 = Math.Max(0, need.Y0 - mh);
        int x1 = Math.Min(frameW, need.X1 + mw), y1 = Math.Min(frameH, need.Y1 + mh);

        var dec = ImageIo.LoadRegion(path, x0, y0, x1 - x0, y1 - y0, frameW, frameH);
        if (dec is not ({ } buf, int gx, int gy)) return null;
        _regionSlot = new RegionSlot(path, buf, gx, gy, gx + buf.Width, gy + buf.Height);
        return buf;
    }

    // True once a patch has actually been rendered, i.e. there is something to clean up. Editing
    // without ever zooming in must not trigger a compacting collection on every pause.
    private bool _patchDirty;
    private CancellationTokenSource? _patchIdleCts;

    /// <summary>
    /// Debounced tidy-up after the user stops pixel-peeping.
    ///
    /// Runs from EVERY exit of the patch path — a completed render, a request declined by the
    /// budget, and the patch being cleared — because they are not interchangeable. Compacting
    /// only at the end of a successful render missed the most common case entirely: zooming back
    /// OUT ends in requests the budget refuses, which return before the try/finally ever runs, so
    /// the garbage from the way in was never handed back (measured: 1.1 GB → 1.86 GB and stuck).
    ///
    /// Also drops the full-resolution frame itself once no patch is on screen. That is 690 MB for
    /// a 60 MP source, held purely so a NEXT patch or export need not decode again — worth
    /// keeping while zoomed in, not worth keeping while composing at fit. An export that follows
    /// simply decodes once more.
    /// </summary>
    private async void SchedulePatchCleanup()
    {
        if (!_patchDirty) return;
        _patchIdleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _patchIdleCts = cts;
        try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { return; }
        if (_patchRunning || _patchQueued is not null || !_patchDirty) return;

        _patchDirty = false;
        if (Patch is null)
        {
            _regionSlot = null;
            // Safe to drop even mid-export: ExportAsync holds its own reference to the buffer,
            // so clearing the slot only means the NEXT caller decodes again.
            lock (_fullSlotGate) _fullSlot = null;
        }
        ReleaseBulkBuffers();
    }

    /// <summary>Drop the patch — the preview underneath is authoritative again. Called whenever
    /// the render changes (any edit) or the user zooms back out.</summary>
    public void ClearSharpPatch()
    {
        _patchCts?.Cancel();
        _patchToken++;
        _patchQueued = null;   // whatever was waiting is for a view/render that no longer exists
        if (Patch is not null) Patch = null;
        SchedulePatchCleanup();
    }

    /// <summary>
    /// Request a sharp patch covering <paramref name="roi"/> (normalised, in displayed-frame
    /// coordinates). Cheap to call repeatedly: a superseded request is cancelled, and one that
    /// would cost more than <see cref="RegionRender.MaxSourcePixels"/> is declined outright so
    /// the preview keeps standing in rather than the app stalling on a near-full-frame render.
    /// </summary>
    public async Task RequestSharpPatchAsync(RegionRender.Roi roi)
    {
        if (CurrentFrame is not { } frame || !HasImage) return;

        FrameParams p = BuildParams();
        string srcPath = frame.Path;

        // Budget FIRST, and off the source DIMENSIONS, which the preview cache already knows.
        // Deciding after the decode would mean paying ~2 s of LibRaw for a patch we then refuse —
        // and at shallow zoom, where most of the frame is visible, refusing is the common case.
        if (_previews.Get(srcPath) is not { } entry) return;
        if (RegionRender.SourcePixelsFor(entry.SourceWidth, entry.SourceHeight, p, roi)
            > RegionRender.MaxSourcePixels)
        {
            SchedulePatchCleanup();   // zooming back out lands here, over and over
            return;
        }

        // One at a time; the newest request wins the queue slot.
        if (_patchRunning) { _patchQueued = roi; return; }
        _patchRunning = true;

        _patchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _patchCts = cts;
        int tok = ++_patchToken;

        bool needsDecode = _fullSlot is null
                           || !string.Equals(_fullSlot.Path, srcPath, StringComparison.OrdinalIgnoreCase);
        if (needsDecode) ReportBackground(Loc.T("载入全分辨率 …"));
        try
        {
            int frameW = entry.SourceWidth, frameH = entry.SourceHeight;
            var result = await Task.Run(() =>
            {
                ImageBuffer img; RegionRender.Roi realised;
                var need = RegionRender.RequiredSourceBounds(frameW, frameH, p, roi);
                ImageBuffer? slice = RegionSliceFor(srcPath, need, frameW, frameH);
                cts.Token.ThrowIfCancellationRequested();
                if (slice is not null)
                {
                    RegionSlot s = _regionSlot!;
                    (img, realised) = RegionRender.RenderFromSlice(slice, s.X0, s.Y0, frameW, frameH, p, roi);
                }
                else
                {
                    // TIFF, or the DNG-Converter backend — neither can region-decode. Fall back
                    // to the whole frame, which is what this path always used to do.
                    ImageBuffer full = LoadFullLinear(srcPath);
                    cts.Token.ThrowIfCancellationRequested();
                    (img, realised) = RegionRender.Render(full, p, roi);
                }
                cts.Token.ThrowIfCancellationRequested();
                return new SharpPatch((Bitmap)BitmapConvert.ToBitmap(img),
                                      realised.X, realised.Y, realised.W, realised.H);
            }, cts.Token);

            if (tok != _patchToken || cts.IsCancellationRequested) { result?.Image.Dispose(); return; }
            if (result is not null) { Patch = result; _patchDirty = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = Loc.T("局部全分辨率渲染失败：") + ex.Message; }
        finally
        {
            _patchRunning = false;
            if (needsDecode) ReportBackground("");

            if (_patchQueued is { } next)
            {
                // Fire the newest region that arrived while this one was busy. AFTER
                // _patchRunning is cleared, so the re-entry takes the normal path. No compaction
                // mid-burst — the next render would only re-dirty the heap.
                _patchQueued = null;
                _ = RequestSharpPatchAsync(next);
            }
            else
            {
                // Burst over — tidy up once the user has actually stopped moving. A patch
                // allowed the full MaxSourcePixels budget allocates a few hundred megabytes of
                // intermediates (source slice, distortion copy, inverted frame, output), all
                // dead once the bitmap exists and all on a heap the runtime never compacts by
                // itself: three zoom cycles walked the process from 1.3 GB to 2.1 GB.
                SchedulePatchCleanup();
            }
        }
    }

    // ── Preview rendering: debounced when idle, low-latency while dragging ──────
    //
    // The debounce is a "wait for quiet" rule, and a slider drag is never quiet: every move
    // cancelled the pending render and started the 50 ms wait again, so the picture did not move
    // at all until the user stopped — the control was live but the image was not following it.
    //
    // A drag therefore switches strategy entirely, the way the source does
    // (main_window.py::_on_interaction_started):
    //
    //   • No debounce. Every move renders.
    //   • Half the preview's long edge, i.e. a quarter of the pixels. The heavy density inversion
    //     re-runs on every move because FilmBase/WB edits invalidate everything upstream of it.
    //   • The downsampled source is computed ONCE per drag and reused. It cannot change mid-drag
    //     (the frame, its decode and the photometric chain are all fixed), and re-deriving it per
    //     move costs more than the render it feeds.
    //   • SYNCHRONOUSLY, on the UI thread. Handing each move to a worker means the moves that
    //     arrive while one is running are dropped, which is what makes a preview lurch instead of
    //     track; running inline lets the pointer input coalesce naturally against the work, so
    //     every frame that is computed is a frame that is shown.
    //
    // Release restores full resolution with one immediate, un-debounced pass.
    private bool _interacting;
    private ImageBuffer? _dragSmall;
    private const int DragMaxEdge = PreviewMaxEdge / 2;

    /// <summary>A slider thumb or curve point was grabbed — enter low-latency drag mode.</summary>
    public void BeginInteractive()
    {
        CommitUndo();          // the grab closes the previous edit; the drag itself is one step
        _renderCts?.Cancel();  // whatever the last change queued is about to be superseded
        _interacting = true;
        _dragSmall = null;     // force a fresh downsample on the first move
    }

    /// <summary>Released — back to full resolution, immediately.</summary>
    public void EndInteractive()
    {
        if (!_interacting) return;
        _interacting = false;
        _dragSmall = null;
        RenderNow();
    }

    // Backstop for the whole render path, and the reason all three entry points below are
    // wrapped rather than just the async ones.
    //
    // ScheduleRender and RenderNow are `async void`: they are driven straight off property
    // setters, so there is no Task for anyone to observe and an escaping exception unwinds
    // past the message loop and TERMINATES the process, taking every unsaved edit in the roll
    // with it. RenderInteractive is worse in one way — it runs INLINE on the UI thread inside
    // the same async void, so it does not even need an await to get there.
    //
    // The sampling path already learned this (MainWindow's pointer-released backstop and
    // TrySample); a render is exactly as user-triggered and exactly as fatal. Cancellation
    // stays silent — it is the normal outcome of superseding a queued render.
    private void ReportRenderFailure(Exception ex) => StatusText = Loc.T("渲染失败：") + ex.Message;

    private async void ScheduleRender()
    {
        if (_suppressRender || _previewLinear is null) return;
        // Any edit invalidates the sharp patch — it was rendered under the OLD parameters, and
        // leaving it up would show a stale rectangle pasted over a freshly rendered preview.
        // The view re-requests once the new render lands, if still zoomed in far enough.
        ClearSharpPatch();
        if (!_restoring) MarkEdit();   // a real, user-driven param change → undo-committable
        if (_interacting) { RenderInteractive(); return; }
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        try
        {
            await Task.Delay(50, cts.Token);
            await RenderPreviewAsync(BuildParams(), cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    /// <summary>Full-resolution render with no debounce — the settling pass after a drag.</summary>
    private async void RenderNow()
    {
        if (_suppressRender || _previewLinear is null) return;
        ClearSharpPatch();
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        try { await RenderPreviewAsync(BuildParams(), cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    /// <summary>One drag frame, inline on the UI thread at drag resolution.</summary>
    private void RenderInteractive()
    {
        if (_previewLinear is null) return;
        try
        {
            _dragSmall ??= Resample.Box(_previewLinear, DragMaxEdge);
            ImageBuffer outImg = Pipeline.ProcessFrame(_dragSmall, BuildParams());
            // Histograms stay live: at a quarter of the pixels the pass is noise next to the
            // render, and a histogram that freezes mid-drag is exactly when it is being read.
            Histogram = HistogramData.FromBuffer(outImg.Data);
            PreviewImage = BitmapConvert.ToBitmap(outImg);
        }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    private async Task RenderPreviewAsync(FrameParams p, CancellationToken ct)
    {
        ImageBuffer src = _previewLinear!;
        // Captured now, not read at apply time: if the user switches frames while this render is
        // in flight, the thumbnail it produces still belongs to the frame it was rendered from.
        RollFrame? frame = CurrentFrame;

        (Bitmap bmp, HistogramData hist, Bitmap thumb) = await Task.Run(() =>
        {
            ImageBuffer outImg = Pipeline.ProcessFrame(src, p);
            ct.ThrowIfCancellationRequested();
            // Histogram on the same buffer that feeds the display (Basic = already sRGB-encoded).
            HistogramData h = HistogramData.FromBuffer(outImg.Data);
            // The film strip gets a SCALED COPY of this same finished positive — it does not run
            // its own pipeline pass. Until now the current frame's thumbnail was only rebuilt when
            // you LEFT the frame, so the strip showed a stale version of whatever you were
            // actively adjusting. Reusing the render costs one box pass over an image that is
            // already in cache, which is why the source does it here too
            // (main_window.py::_on_process_done → _film_strip.update_thumbnail(result)) rather
            // than paying for a second inversion. outImg is already cropped and oriented, so the
            // thumbnail matches the frame as composed.
            var t = (Bitmap)BitmapConvert.ToBitmap(Resample.Box(outImg, ThumbMaxEdge));
            return ((Bitmap)BitmapConvert.ToBitmap(outImg), h, t);
        }, ct);

        if (ct.IsCancellationRequested) { bmp.Dispose(); thumb.Dispose(); return; }
        void Apply()
        {
            PreviewImage = bmp;
            Histogram = hist;
            if (frame is not null) SetThumbnail(frame, thumb); else thumb.Dispose();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else await Dispatcher.UIThread.InvokeAsync(Apply);
    }
}
