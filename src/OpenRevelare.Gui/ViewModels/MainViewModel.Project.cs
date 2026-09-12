using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> —— 工程存取（.ncproj）。
///
/// 拆分自 MainViewModel.cs，见该文件顶部关于拆分理由的说明。此处只有工程文件的读写：
/// schema 与 Python 版兼容，所以这一块的任何改动都要考虑「旧工程还读不读得回来」。
/// </summary>
public partial class MainViewModel
{
    public bool UsesLegacyColorPipeline =>
        _colorPipelineVersion == ColorPipelineVersion.LegacyV1;

    private bool _legacyNoticeDismissed;

    /// <summary>
    /// The banner is informational, so it must be closable — it was the ONLY notice in the window
    /// that was not. A user who deliberately keeps old rolls on v1 (which D-013 exists to let them
    /// do) had this bar occupying the top of the preview for the life of the project, with no way
    /// to acknowledge it. Dismissal is per roll and per session, exactly like
    /// <see cref="ShowTiffInputNotice"/>: reopening the roll offers the migration again, so
    /// closing it forfeits nothing.
    /// </summary>
    public bool ShowLegacyColorPipelineNotice =>
        UsesLegacyColorPipeline && !_legacyNoticeDismissed;

    public void DismissLegacyColorPipelineNotice()
    {
        if (_legacyNoticeDismissed) return;
        _legacyNoticeDismissed = true;
        OnPropertyChanged(nameof(ShowLegacyColorPipelineNotice));
    }

    /// <summary>
    /// What migrating THIS project will actually do to the picture, worked out from the roll
    /// rather than left for the user to guess.
    ///
    /// <para>
    /// The dialog used to say only "画面和之后的导出可能变化", which is true of every project and
    /// therefore useful for none: someone deciding whether to touch a finished roll cannot act on
    /// "可能". The three conditions below are the complete set of things that differ between the
    /// pipelines, measured in LegacyVersusManagedRenderTests — outside them the two renders are
    /// bit-identical, so the honest answer is that nothing will change.
    /// </para>
    /// </summary>
    private string DescribeMigrationEffect()
    {
        bool anyLut = false, anyCurve = false, anyLegacyStage2 = false;
        foreach (RollFrame frame in Frames)
        {
            FrameParams p = frame.Params;
            anyLut |= !string.IsNullOrWhiteSpace(p.PrintLut);
            anyCurve |= p.CurvePointsM.Count > 0 || p.CurvePointsR.Count > 0
                        || p.CurvePointsG.Count > 0 || p.CurvePointsB.Count > 0;
            anyLegacyStage2 |= !p.DisplayReferredStage2;
        }

        if (!anyLut && !anyCurve && !anyLegacyStage2)
            return Loc.T("这一卷没有用胶片 LUT、没有曲线，迁移后画面不会有任何变化——只有导出会带上准确的 ICC。");

        var reasons = new List<string>();
        if (anyLegacyStage2) reasons.Add(Loc.T("旧的第 2 步渲染方式（变化最大）"));
        if (anyCurve) reasons.Add(Loc.T("曲线"));
        if (anyLut) reasons.Add(Loc.T("胶片 LUT"));
        // The closing sentence must be true of ALL three, and "旧版贴错了 profile" is not: that
        // describes the first two, whose PixelProfileMismatch really is set, but NOT the curve
        // case — there v1 reports no mismatch at all and the difference is the private gamma
        // round-trip Stage 2's comment claimed to have removed. LegacyVersusManagedRenderTests
        // pins exactly that distinction, so the wording has to stay above it.
        return Loc.F($"这一卷用到了：{string.Join(Loc.T("、"), reasons)}，迁移后画面会变化。")
               + Loc.T("变的方向是修正——旧版在这些环节上做的事和它自己声称的不一致——但这是你已经调好的观感。");
    }

    /// <summary>The migration dialog's body: what it does, then what it does to THIS roll.</summary>
    public string MigrationDialogText =>
        Loc.T("迁移会把此工程从旧版兼容渲染切换到 v2：嵌入 ICC 的 TIFF 会完整转换，print LUT 会转换到所选 exact output profile，曲线在目标编码中运行。工程保存后不会自动退回 v1。")
        + "\n\n" + DescribeMigrationEffect();

    public string LegacyColorPipelineNotice => Loc.T(
        "此工程仍使用旧版色彩管线。画面保持原样，但输出会嵌入准确的兼容 ICC；迁移到色彩管理版会重新渲染并可能改变外观。");

    public string ColorPipelineDiagnostic =>
        (_colorPipelineVersion == ColorPipelineVersion.LegacyV1
            ? "LegacyV1 (explicit compatibility)"
            : "ManagedV2 (exact ICC)") + $" · TIFF input={DescribeTiffInput()}";

    /// <summary>
    /// "Unspecified" is an implementation word for "the user set no override", which is the normal
    /// state now that files are read rather than interrogated. The diagnostic says what actually
    /// happened instead, including the evidence when detection has run.
    /// </summary>
    private string DescribeTiffInput()
    {
        if (_tiffInputAssumption != TiffInputAssumption.Unspecified)
            return _tiffInputAssumption.ToString();
        return _tiffInputDetection is { } detection
            ? $"auto({detection.Evidence} -> {detection.Assumption})"
            : "auto";
    }

    /// <summary>Detection for the open roll, refreshed whenever the roll or the override changes.</summary>
    private TiffInputDetection? _tiffInputDetection;

    private bool _tiffInputNoticeDismissed;

    /// <summary>
    /// Shown only when detection had to fall back on convention — that is, when the answer was NOT
    /// read out of the file. A file that declared itself needs no prompt, and nagging about those
    /// would train the user to dismiss the one case that matters.
    /// </summary>
    public bool ShowTiffInputNotice =>
        _colorPipelineVersion == ColorPipelineVersion.ManagedV2
        && _tiffInputAssumption == TiffInputAssumption.Unspecified
        && _tiffInputDetection is { IsConclusive: false }
        && !_tiffInputNoticeDismissed;

    public string TiffInputNoticeText => _tiffInputDetection is { } detection
        ? Loc.F($"这卷 TIFF 没有可用的色彩声明，已按 sRGB 处理。{detection.Diagnostic}。看着不对就改为线性。")
        : string.Empty;

    public void DismissTiffInputNotice()
    {
        if (_tiffInputNoticeDismissed) return;
        _tiffInputNoticeDismissed = true;
        OnPropertyChanged(nameof(ShowTiffInputNotice));
    }

    /// <summary>
    /// Re-reads the open roll's first frame and republishes the notice. Cheap: one file open for
    /// tags only, no pixels. RAW rolls and legacy projects short-circuit to no detection at all.
    /// </summary>
    private void RefreshTiffInputDetection()
    {
        TiffInputDetection? detection = null;
        if (_colorPipelineVersion == ColorPipelineVersion.ManagedV2
            && _tiffInputAssumption == TiffInputAssumption.Unspecified)
        {
            string? path = Frames
                .FirstOrDefault(frame => !frame.IsVirtual && !RawDecode.IsRawExtension(frame.Path))
                ?.Path;
            if (path is not null && File.Exists(path))
                detection = TiffInputDetector.Detect(path);
        }

        _tiffInputDetection = detection;
        OnPropertyChanged(nameof(ShowTiffInputNotice));
        OnPropertyChanged(nameof(TiffInputNoticeText));
        OnPropertyChanged(nameof(ColorPipelineDiagnostic));
    }

    /// <summary>
    /// Applies a roll-level override after the fact — the correction path that replaces the old
    /// upfront question. The user is deciding here with the picture in front of them, which is the
    /// only point at which "linear or sRGB" is answerable by looking.
    /// </summary>
    public async Task SetTiffInputAssumptionAsync(TiffInputAssumption assumption)
    {
        if (assumption is not (TiffInputAssumption.Linear or TiffInputAssumption.Srgb))
            throw new ArgumentOutOfRangeException(nameof(assumption), assumption, null);
        if (_tiffInputAssumption == assumption) return;

        CommitLiveParams(CurrentFrame);
        SetTiffInputAssumption(assumption);
        _tiffInputNoticeDismissed = false;
        await InvalidateColourStateAndReloadAsync();
        RefreshTiffInputDetection();
        string label = assumption == TiffInputAssumption.Linear ? Loc.T("线性") : "sRGB";
        StatusText = Loc.F($"整卷 TIFF 输入已改为 {label}，全卷已重新解码。");
    }


    private void SetColorPipelineVersion(ColorPipelineVersion value)
    {
        if (_colorPipelineVersion == value) return;
        _colorPipelineVersion = value;
        OnPropertyChanged(nameof(UsesLegacyColorPipeline));
        OnPropertyChanged(nameof(CanChooseHdrPeak));
        OnPropertyChanged(nameof(HdrPeakHint));
        OnPropertyChanged(nameof(ShowLegacyColorPipelineNotice));
        OnPropertyChanged(nameof(LegacyColorPipelineNotice));
        OnPropertyChanged(nameof(ColorPipelineDiagnostic));
    }

    private void SetTiffInputAssumption(TiffInputAssumption value)
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        if (_tiffInputAssumption == value) return;
        _tiffInputAssumption = value;
        OnPropertyChanged(nameof(ColorPipelineDiagnostic));
    }

    /// <summary>
    /// Explicit L2 opt-in migration. The old project is never changed by load/save alone; this
    /// interaction invalidates every decode/render cache whose meaning depends on the pipeline
    /// version, then re-decodes the current frame before publishing v2 pixels.
    /// </summary>
    public async Task MigrateColorPipelineToV2Async()
    {
        if (!UsesLegacyColorPipeline) return;

        CommitLiveParams(CurrentFrame);
        Project.Data migration = BuildProjectData();
        Project.MigrateColorPipelineToV2(migration);

        SetColorPipelineVersion(migration.ColorPipelineVersion);
        await InvalidateColourStateAndReloadAsync();
        RefreshTiffInputDetection();
        StatusText = Loc.T("已迁移到色彩管理版（v2）；工程将以新版本保存，外观变化不会自动回退。");
    }

    /// <summary>
    /// Drops every decoded artefact whose meaning depends on the colour admission, then re-decodes
    /// the current frame and restarts the roll's background work. Shared by the pipeline migration
    /// and by a roll-level TIFF input change: both invalidate exactly the same things, and letting
    /// them drift apart would leave one of the two showing pixels from the previous admission.
    /// </summary>
    private async Task InvalidateColourStateAndReloadAsync()
    {
        _renderCts?.Cancel();
        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _patchCts?.Cancel();
        _switchToken++;
        ClearSharpPatch();

        _previews.Clear();
        ClearTiles();
        _negativeWb.Clear();
        lock (_decoding) _decoding.Clear();
        lock (_fullSlotGate) _fullSlot = null;
        _regionSlot = null;
        _previewWorking = null;
        _previewMargin = null;
        foreach (RollFrame frame in Frames) SetThumbnail(frame, null);
        PreviewImage = null;
        HasImage = false;
        MarkRollDirty();

        RollFrame? current = CurrentFrame;
        if (current is not null)
            await SwitchFrameAsync(current);

        RestartThumbnails();
        StartRollWarmUp();
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

        // Keep every incoming colour/calibration value local until all potentially long-running
        // preparation is over. The roll currently on screen remains editable while the busy
        // indicator is up; installing these values early would let its autosave serialize the
        // new project's semantics into the old project's file.
        ColorPipelineVersion incomingPipelineVersion = data.ColorPipelineVersion;
        TiffInputAssumption incomingTiffInputAssumption =
            TiffInputAssumptionPolicy.FromPersistedLinearFlag(
                data.Meta.TiffIsLinear, incomingPipelineVersion);
        string? incomingCalSourceDir = data.Meta.CalSourcePath;
        string[]? incomingCalRgbPaths = data.Meta.CalRgbPaths is { } r && r.ContainsKey("R")
            ? new[] { r["R"], r.GetValueOrDefault("G", ""), r.GetValueOrDefault("B", "") }
            : null;
        string? incomingLccSourcePath = data.Meta.LccPath;

        // Before anything reads pixels: the negatives may have moved since this was saved.
        bool relinked = await RelinkIfMissingAsync(data);

        // Recompute the roll-level ops (never stored in the file) from their source paths.
        double[,]? dm = null, cm = null; ImageBuffer? lcc = null;
        try
        {
            var contentPaths = data.Frames.Where(f => !f.IsVirtual).Select(f => f.SourcePath).ToList();
            await Task.Run(() =>
            {
                string[]? rgb = incomingCalRgbPaths is { Length: 3 } p && p.All(File.Exists)
                    ? incomingCalRgbPaths
                    : null;
                if (rgb is null
                    && !string.IsNullOrEmpty(incomingCalSourceDir)
                    && Directory.Exists(incomingCalSourceDir))
                {
                    var (rp, gp, bp) = DecoupleCalibration.FindRgbCalFiles(incomingCalSourceDir);
                    rgb = new[] { rp, gp, bp };
                    incomingCalRgbPaths = rgb;
                }
                if (rgb is not null)
                    (dm, cm) = CalibratePathA(
                        rgb,
                        contentPaths,
                        incomingPipelineVersion,
                        incomingTiffInputAssumption,
                        cachePreviews: false);
                if (!string.IsNullOrEmpty(incomingLccSourcePath)
                    && File.Exists(incomingLccSourcePath))
                {
                    ReportBackground(Loc.T("载入平场校正 …"));
                    lcc = Lcc.LoadFlatField(incomingLccSourcePath, tiffIsLinear: true);
                }
                ReportBackground("");
            });
        }
        catch (Exception ex) { StatusText = Loc.T("工程标定重算失败（按无解耦打开）：") + ex.Message; }

        // Relink/calibration can leave the dialog up for a long time. Capture any edits made to
        // the outgoing roll during that interval while all of its own state is still installed.
        await FlushRollAsync();

        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _previews.Clear(); ClearTiles(); _negativeWb.Clear(); _fullSlot = null; _regionSlot = null;
        lock (_decoding) _decoding.Clear();

        // Detach from the outgoing roll BEFORE its state is replaced — same reason as in
        // LoadRollAsync. Assigning the notes below fires Notes.PropertyChanged → MarkRollDirty,
        // which would dirty the roll being LEFT and then carry that flag into the one being
        // opened, so that merely looking at an old roll rewrote its .ncproj and bumped its
        // 修改时间. Opening is not an edit; the relink above is the only change a load can make.
        _roll = null;
        _rollDirty = false;
        _sheetDirty = false;

        SetColorPipelineVersion(incomingPipelineVersion);
        SetTiffInputAssumption(incomingTiffInputAssumption);
        // 旧模型的工程载入后画面会变——面板顶部据此提示重跑标定。
        NeedsRecalibration = data.NeedsRecalibration;
        _calSourceDir = incomingCalSourceDir;
        _calRgbPaths = incomingCalRgbPaths;
        _lccSourcePath = incomingLccSourcePath;
        _decoupleMatrix = dm; _decoupleChromaMatrix = cm;
        if (lcc is not null) { _lccFlatField = lcc; LccAvailable = true; LccStatus = Loc.T("已载入平场（工程）"); }
        else { _lccFlatField = null; LccAvailable = false; LccStatus = Loc.T("未载入平场校正"); }

        // Roll notes.
        Notes.CameraBody = data.Meta.CameraBody; Notes.FilmStock = data.Meta.FilmStock;
        Notes.FilmIso = data.Meta.FilmIso; Notes.RollNumber = data.Meta.RollNumber;
        Notes.DevLab = data.Meta.DevLab; Notes.DevProcess = data.Meta.DevProcess;
        Notes.DevDate = data.Meta.DevDate; Notes.Location = data.Meta.Location;
        Notes.RollNote = data.Meta.RollNote; Notes.Format = data.Meta.Format;

        // Rebuild under the incoming colour state. Preparation deliberately did not populate
        // shared caches, so no outgoing decode can be mistaken for one of these frames.
        _prevFrame = null;
        // Same guard LoadRollAsync raises, and for the same reason: rebuilding Frames pushes the
        // strip's two-way SelectedItem binding back into CurrentFrame, re-entering
        // OnCurrentFrameChanged while the controls still hold the OUTGOING roll's state. Clearing
        // _prevFrame is not enough — the binding's own null-then-reselect sets it again, and the
        // reselect's fold then stamps the incoming frame 1 with the old roll's _cropRect (null on
        // an ordinary roll). On a reopened SPLIT scan that is frame 1's pre-crop, erased before it
        // is ever applied. This flag is the half of the guard that covers it (see CommitLiveParams).
        _paramsLoaded = false;
        _pendingSprocketPrompt = false;
        _undo.Clear(); _redo.Clear(); _committed = null; UpdateUndoState();
        foreach (RollFrame f in Frames) Retire(f.Thumbnail);   // the outgoing roll's strip
        // Rebuild under the reorder guard, so the strip's binding cannot start a switch MID-build.
        // Frames.Clear() pushes null through SelectedItem and the first Frames.Add makes the
        // ListBox auto-select it and push it straight back — a switch that would decode frame 1
        // while _splitPaths still describes the OUTGOING roll, i.e. without the region path, and
        // that the deliberate assignment below could not supersede: CurrentFrame would already
        // hold that very frame, so [ObservableProperty]'s equality check makes the write a no-op
        // and OnCurrentFrameChanged never fires again. Frame 1 kept the whole scan, un-split.
        _reordering = true;
        try
        {
            Frames.Clear();
            foreach (Project.Frame pf in data.Frames)
            {
                FrameParams fp = pf.Params;
                fp.DecoupleMatrix = dm; fp.DecoupleMode = DecoupleMode.Linear; fp.DecoupleChromaMatrix = cm;
                fp.LccFlatField = lcc;   // roll-uniform (matches import); global toggle gates it
                Frames.Add(new RollFrame(pf.SourcePath, pf.IsVirtual) { Params = fp });
            }
            CurrentFrame = null;   // so the assignment below is a real change, not a no-op
        }
        finally { _reordering = false; }
        LccEnabled = lcc is not null;
        RefreshSplitPaths();        // reopened split rolls get the sharp region previews too
        IsBusy = false;
        CurrentFrame = Frames[0];   // triggers SwitchFrameAsync → decode + LoadParams + render

        // Autosave now tracks THIS project file. Adopted after the frames are in place so the
        // entry's frame count is the real one.
        _autoSave.Discard();
        AdoptProject(path);
        if (relinked) MarkRollDirty();   // the repaired paths, written back on the next idle pause

        // Opening a DIFFERENT project must offer its own migration again — the dismissal is about
        // one roll, not about the session. (This is the path a legacy project actually arrives by;
        // LoadRollAsync only ever creates ManagedV2 rolls, which have no banner to dismiss.)
        _legacyNoticeDismissed = false;
        OnPropertyChanged(nameof(ShowLegacyColorPipelineNotice));

        RefreshTiffInputDetection();
        StatusText = Loc.F($"工程已打开：{Path.GetFileName(path)}（{Frames.Count} 帧）");
        StartRollWarmUp();
        ReleaseBulkBuffers();   // the calibration/import full-res decodes are dead; uncommit them
        await Task.CompletedTask;
    }

    /// <summary>Open a roll from the import dialog: compute Path-A decouple + LCC (roll-level), then load.</summary>
    public async Task LoadRollWithConfigAsync(ImportConfig cfg)
    {
        if (cfg.Paths.Count == 0) return;

        // No admission gate here any more. A TIFF that carries no usable ICC is resolved by
        // TiffInputDetector from the colorimetry the file itself declares, and only falls back to a
        // labelled convention when it declares none. Demanding an upfront Linear/sRGB answer taxed
        // every import to cover a minority of files, and asked at the one moment the user cannot
        // judge it — before a single pixel is on screen. cfg.TiffInputAssumption still carries an
        // explicit override when the user set one; the roll can also be re-decided afterwards.

        // Save the outgoing roll while all of its own colour state is still installed. The
        // incoming assumption is adopted only after preparation succeeds, so a bad calibration
        // file cannot alter either the saved project or the live caches of the roll on screen.
        await FlushRollAsync();
        IsBusy = true;
        StatusText = Loc.T("正在准备导入 …");

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

                    (dm, cm) = CalibratePathA(
                        calRgb,
                        cfg.Paths,
                        ColorPipelineVersion.ManagedV2,
                        cfg.TiffInputAssumption,
                        cachePreviews: false);
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

        // Preparation may be long and the busy indicator does not lock the old roll's controls.
        // Flush once more while its pipeline/assumption are still installed, so edits made during
        // calibration cannot be lost when adoption detaches it below.
        await FlushRollAsync();

        // Preparation was side-effect free. From this point onward the incoming roll is being
        // adopted, so retire every cache whose pixels were decoded under the outgoing contract.
        _previews.Clear(); ClearTiles(); _negativeWb.Clear(); _fullSlot = null; _regionSlot = null;
        lock (_decoding) _decoding.Clear();

        // Retain calibration SOURCE paths so a saved .ncproj can recompute matrices on load.
        _calSourceDir = cfg.PathA ? cfg.CalDir : null;
        _calRgbPaths = calRgb;
        _lccSourcePath = (cfg.LccEnabled && !string.IsNullOrWhiteSpace(cfg.LccPath)) ? cfg.LccPath : null;

        // Set roll-level ops BEFORE loading so the sprocket estimate + auto film-base (which run
        // during LoadRollAsync) sample t_base in the DECOUPLED domain and the first render decouples.
        // The auto-inversion choice rides along for the same reason: ApplySprocketAutoAsync acts on it
        // partway through the load.
        _cfgAutoInvert = cfg.AutoInvert;
        _decoupleMatrix = dm; _decoupleChromaMatrix = cm;
        if (lccField is not null) { _lccFlatField = lccField; LccAvailable = true; LccStatus = Loc.T("已载入平场：") + lccName; }
        SetTiffInputAssumption(cfg.TiffInputAssumption);
        IsBusy = false;

        _configLoad = true;
        try { await LoadRollAsync(cfg.Paths); }
        finally { _configLoad = false; }

        // Detection is not repeated here: LoadRollAsync above owns it for every entry point, and
        // SetTiffInputAssumption ran before the load, so it has already seen this roll's choice.

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
    /// Returns the MATRIX and not a chroma_amp triple, though DecoupleCalibration can measure
    /// both and the CLI prints both. They are alternatives, not layers: Inversion multiplies by
    /// the bare chroma_grade whenever a chroma matrix is present and never looks at amp, so a
    /// roll carrying both would silently ignore the amp. The matrix wins because it compensates
    /// per chroma AXIS (it is built from 1/ampYb and 1/ampRg) rather than per RGB channel.
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
    private (double[,] Dm, double[,] Cm) CalibratePathA(
        string[] calRgb,
        IReadOnlyList<string> paths,
        ColorPipelineVersion pipelineVersion,
        TiffInputAssumption tiffInputAssumption,
        bool cachePreviews)
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
                roi[i] = ImageIo.RoiMeanFull(
                    calRgb[i],
                    pipelineVersion,
                    ColorManagement,
                    tiffInputAssumption);
            }
            else
            {
                int fi = i - 3;
                // Both sizes off ONE decode, neither of them via a full-resolution frame. These are
                // the roll's first frames — exactly what the warm-up would decode next — so their
                // previews go into the cache and that work is not paid for twice.
                var (outs, srcW, srcH) = ImageIo.LoadWorkingPreviews(
                    paths[fi],
                    pipelineVersion,
                    ColorManagement,
                    tiffInputAssumption,
                    PreviewMaxEdge,
                    720);
                if (cachePreviews)
                {
                    string previewKey = PreviewKey(
                        paths[fi],
                        preCrop: null,
                        pipelineVersion,
                        tiffInputAssumption);
                    _previews.Put(previewKey, outs[0], srcW, srcH);
                    CaptureTile(previewKey, outs[0]);
                }
                negs[fi] = outs[1].Pixels;
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
        // Config imports flush before their potentially-failing preparation, while the outgoing
        // roll's assumption is still active. Direct callers still flush here.
        if (!_configLoad) await FlushRollAsync();
        _autoSave.Discard();
        // Detach from the outgoing roll BEFORE its frames are replaced: anything that dirties the
        // roll between here and RegisterRoll would otherwise be pointed at the old entry.
        _roll = null;
        _rollDirty = false;
        _sheetDirty = false;
        SetColorPipelineVersion(ColorPipelineVersion.ManagedV2);
        // Startup/automation can call this lower-level entry without the import dialog. It may
        // open a profiled TIFF, but an untagged one must fail closed instead of inheriting the
        // previous roll's choice. LoadRollWithConfigAsync sets the deliberate choice first.
        if (!_configLoad) SetTiffInputAssumption(TiffInputAssumption.Unspecified);
        Notes.Reset();            // notes are per-roll; a new roll starts blank
        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _prevFrame = null;
        // The controls still show the OUTGOING roll, and they stay that way until the incoming
        // first frame finishes decoding and LoadParams runs. Say so before Frames is touched:
        // rebuilding the collection pushes the strip's two-way SelectedItem binding back into
        // CurrentFrame, and every write-back that lands in that window is gated on this flag (see
        // CommitLiveParams). Without it the first frame of the new roll is stamped with the old
        // roll's controls — on a split import that means its pre-crop is replaced by null.
        _paramsLoaded = false;
        _pendingSprocketPrompt = true;
        if (!_configLoad)   // config path pre-sets roll-level ops before this call; don't wipe them
        {
            _decoupleMatrix = null; _decoupleChromaMatrix = null;
            _lccFlatField = null; LccAvailable = false; LccEnabled = false;
        }
        _undo.Clear(); _redo.Clear(); _committed = null; UpdateUndoState();
        if (!_configLoad)   // the config path already cleared, and has since cached real work
        {
            _previews.Clear(); ClearTiles(); _negativeWb.Clear(); _fullSlot = null; _regionSlot = null;   // never serve the previous roll's pixels
            lock (_decoding) _decoding.Clear();
        }
        foreach (RollFrame f in Frames) Retire(f.Thumbnail);   // the outgoing roll's strip
        Frames.Clear();
        // File-name order, not the order the paths arrived in. A folder import is already sorted,
        // but a hand-picked selection comes back in whatever order the platform picker chose, and
        // a roll assembled from several adds arrives in add order — the strip would then read as
        // the order the files were TOUCHED rather than the order they were shot. Sorting the paths
        // rather than the finished frames keeps each split scan's virtual copies next to their
        // parent, since they are all contributed by one path.
        foreach (string p in SortedByName(paths)) AddFramesForPath(p);
        RefreshSplitPaths();        // before the first switch, which consults it
        CurrentFrame = Frames[0];   // triggers SwitchFrameAsync (decode + render)
        RegisterRoll(paths);        // new roll → new catalog entry + project file

        // HERE, not in the callers. Every way of opening a roll lands in this method, and the
        // detection is the ONLY thing that tells a user their untagged TIFF was assumed sRGB —
        // the forced import question that used to ask it is gone. It lived in
        // LoadRollWithConfigAsync alone, so the import dialog disclosed and the two paths that
        // bypass it (command line, double-click, 添加图像) silently did not: the roll was assumed
        // sRGB with no notice and no way to correct it. The dismissal resets with the roll for the
        // same reason — dismissing one roll's notice must not suppress the next roll's.
        //
        // Frames must already be populated: the detector reads the first non-virtual, non-RAW
        // frame off disk. Cheap — header tags only, no pixels.
        _tiffInputNoticeDismissed = false;
        _legacyNoticeDismissed = false;
        RefreshTiffInputDetection();

        // Fire and forget: the import must return as soon as frame 1 is on screen. Awaiting the
        // roll here is what made importing feel like it hung — it did not come back until every
        // frame in the roll had been decoded.
        StartRollWarmUp();
        ReleaseBulkBuffers();   // the calibration/import full-res decodes are dead; uncommit them
        await Task.CompletedTask;
    }

    /// <summary>Source paths in film-strip order — by file name, numerically aware.</summary>
    private static List<string> SortedByName(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        list.Sort(NaturalOrder.Instance);
        return list;
    }

    /// <summary>
    /// Frames contributed by one source file: normally one, but a scan that the split pre-pass
    /// cut into a strip contributes one per negative.
    ///
    /// The first is the real frame and the rest are virtual copies of it, which is the shape the
    /// rest of the app already expects from a shared source file — the project writer, the
    /// catalog's frame count and the missing-file relink all key off exactly one non-virtual
    /// entry per path. Each carries its own crop, so they are independent photographs that merely
    /// happen to be stored together.
    /// </summary>
    private void AddFramesForPath(string path)
    {
        // Scanner TIFF: the ICC matrix applied on load already corrects the inter-channel
        // differences, so the sensor-crosstalk boost chroma_grade exists to undo is not wanted
        // on top of it — that would be a double amplification. The preference is a RAW-path
        // setting for exactly that reason; a scan is pinned at 1.0 regardless of it.

        if (!_splitPlans.TryGetValue(path, out var rects) || rects.Count <= 1)
        {
            var single = new RollFrame(path);
            // A lone rect is a strip cut down to one negative, not a crop the user drew — so it is
            // this frame's cell as much as any sibling's would be, and SplitCell is set to match.
            // Harmless when it covers the whole file: the re-anchoring is then the identity.
            if (rects is { Count: 1 }) { single.Params.CropRect = rects[0]; single.Params.SplitCell = rects[0]; }
            Frames.Add(single);
            return;
        }

        var parent = new RollFrame(path);
        parent.Params.CropRect = rects[0];
        parent.Params.SplitCell = rects[0];
        Frames.Add(parent);
        for (int i = 1; i < rects.Count; i++)
        {
            RollFrame copy = RollFrame.MakeVirtualCopy(parent);
            copy.Params.CropRect = rects[i];
            copy.Params.SplitCell = rects[i];
            Frames.Add(copy);
        }
    }

    /// <summary>Crops agreed in the split dialog, by source path. Consumed by the next
    /// <see cref="LoadRollAsync"/> and cleared with the roll.</summary>
    private readonly Dictionary<string, IReadOnlyList<(double X, double Y, double W, double H)>>
        _splitPlans = new();

    /// <summary>Hand the split dialog's decisions to the load that follows.</summary>
    public void SetSplitPlans(
        IEnumerable<(string Path, IReadOnlyList<(double X, double Y, double W, double H)> Rects)> plans)
    {
        _splitPlans.Clear();
        foreach (var (path, rects) in plans) _splitPlans[path] = rects;
    }

    /// <summary>Decode the selected frame, load its params into the UI, render.</summary>
    private async Task SwitchFrameAsync(RollFrame frame)
    {
        IsBusy = true;
        StatusText = Loc.F($"正在解码 {frame.FileName} …");
        int tok = ++_switchToken;
        // _cropRect still belongs to the frame being left, so until LoadParams runs below the
        // controls describe no frame in particular. SplitCropOf reads this to know which rect to
        // trust, and CommitLiveParams refuses to write the controls back onto a frame while it is
        // false — the decode below is awaited, and everything that fires meanwhile (the strip's
        // SelectedItem binding, autosave, the auto-invert chain) would otherwise stamp the
        // incoming frame with the outgoing frame's state.
        _paramsLoaded = false;
        try
        {
            // Cache hit → no decode at all; otherwise join whoever is already decoding this file.
            // Re-selecting a frame (or a virtual copy, which shares its parent's path) must never
            // pay for LibRaw again.
            // A frame that owns only part of its file gets its region cut from the source before
            // the downsample, so it keeps the full preview budget instead of the fraction its
            // share of the strip would leave it. What comes back is the frame PLUS the split
            // margin, so the render still crops — but against the box, not the whole scan. See
            // ForPreview.
            var pre = SplitCropOf(frame);
            PreviewCache.Entry entry = await PreviewAsync(frame.Path, pre);
            if (tok != _switchToken) return;   // superseded by a newer switch
            AdoptPreview(frame, entry, pre, PreviewKey(frame.Path, pre));
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
            _paramsLoaded = true;              // _cropRect now describes THIS frame
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
        // p.OutputIntent is deliberately NOT adopted: a roll saved with the old NONE intent would
        // otherwise load with a blank-looking preview and no control left to change it back.
        // The preview is always the full render now; linear is an export-time choice.
        // Adopt the roll's saved step-4 target without writing it back or dirtying the roll —
        // this is loading, not choosing.
        SyncOutputSpace(p.ResolvedOutputSpace.Name);
        SyncHdrPeak(p.HdrPeakNits);
        SyncPrintLut(p.PrintLut);
        // Stage 1 — film base
        TBaseR = p.TBase[0]; TBaseG = p.TBase[1]; TBaseB = p.TBase[2];
        DMinPerChannel = (double[])p.DMinPerChannel.Clone();
        DMaxPerChannel = (double[])p.DMaxPerChannel.Clone();
        // Stage-2 的色温/色调滑块已随【色偏修正】一组移除，但存下来的 wb_gains 仍然照常载入、
        // 照常参与渲染——旧工程的观感因此逐位不变。
        //
        // 不折进亮端端点。看上去两者都是逐通道的对数域操作，实际不是：Stage-2 增益是线性域的
        // 【乘法】，等价于给密度【加】一个常数；而端点决定的是【斜率】。加常数与改斜率只能在
        // 某一个密度值上重合，不可能对所有像素等价。实测把 (色温70/色调-30) 折进端点后，
        // R/B 比在薄部偏 -18%、中间调 +4%、浓部 +53%——旧卷会明显变色。
        //
        // 所以旧卷保留它已有的那一层增益，新卷则一律是 1,1,1（没有控件能再写它），色偏统一
        // 由亮端端点承担。这是唯一既不改旧观感、又不留下第二处色偏来源的做法。
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
        // Carried, not assumed: a legacy curve stays legacy until the user edits it.
        _curveHasEndpoints = p.CurveHasEndpoints;
        // Geometry
        Rotation = p.Rotation; _quarterTurns = p.QuarterTurns; _flipH = p.FlipH; _flipV = p.FlipV;
        _cropRect = p.CropRect;
        _splitCell = p.SplitCell;
        FilmBaseText = "";
        _filmBaseSampled = true;
        SyncEndpointViews();            // 亮度/色温/色调/黑场 读数跟上刚载入的六个端点
        _suppressRender = false;

        FrameParamsLoaded?.Invoke(p);   // view syncs the curve editor
        ScheduleRender();
    }

}
