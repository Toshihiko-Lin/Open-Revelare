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
/// <see cref="MainViewModel"/> —— 参数广播 / 复制粘贴（按 <see cref="Models.SyncOptions"/> 过滤字段）。
/// </summary>
public partial class MainViewModel
{
    // ── Broadcast / copy-paste (fields gated by SyncOptions) ────────────────────
    public SyncOptions Sync { get; } = new();
    private FrameParams? _calClipboard, _sceneClipboard;
    [ObservableProperty] private bool _hasCalClipboard;
    [ObservableProperty] private bool _hasSceneClipboard;

    /// <summary>Copy Stage-1 calibration (per SyncOptions) from the current frame to every other frame.</summary>
    public void ApplyCalibrationToRoll() => Broadcast(cal: true, scene: false, onlySelected: false, Loc.T("标定"));

    /// <summary>Copy Stage-2 scene adjustments (per SyncOptions) from the current frame to every other frame.</summary>
    public void ApplySceneToRoll() => Broadcast(cal: false, scene: true, onlySelected: false, Loc.T("场景"));

    public void CopyCalibration() { _calClipboard = BuildParams(); HasCalClipboard = true; StatusText = Loc.T("已复制 Cineon 标定"); }
    public void CopyScene() { _sceneClipboard = BuildParams(); HasSceneClipboard = true; StatusText = Loc.T("已复制 Display 参数"); }

    /// <summary>Paste the copied calibration onto the ticked frames.</summary>
    public void PasteCalibrationToSelected() => Paste(_calClipboard, cal: true, scene: false, Loc.T("标定"));
    public void PasteSceneToSelected() => Paste(_sceneClipboard, cal: false, scene: true, Loc.T("场景"));

    /// <summary>
    /// Paste onto the CURRENT frame only — the one-to-one case the roll-wide broadcasts could not
    /// express. Matching one frame to another is a matter of ticking it in the strip and pasting
    /// to "selected"; successful batch actions clear their one-shot selection themselves.
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

    /// <summary>
    /// The 几何 panel's own broadcast: the current frame's orientation and straighten angle onto
    /// the ticked frames, or the whole roll when none are ticked. Deliberately ignores the 同步项
    /// geometry ticks — those default to off; this button IS the request.
    ///
    /// The crop is NOT carried: a crop is a composition drawn on one picture, and a roll that was
    /// shot sideways needs the turn on every frame but the framing on none of them. Crops travel
    /// through 同步项 → 裁切 with the paste commands, where asking for it is explicit.
    /// </summary>
    public void ApplyGeometryToFrames()
    {
        if (CurrentFrame is null) return;
        bool hadSelection = Frames.Any(f => f.IsSelected);
        CommitUndo();
        // Through ForStorage: with the crop tool open BuildParams suppresses the crop for the
        // render, and stored (here, and on every target) that null would erase it.
        FrameParams src = LiveParams.ForStorage(BuildParams(), _cropEditing, _cropRect);
        CurrentFrame.Params = src;
        var ticked = Frames.Where(f => f.IsSelected && !ReferenceEquals(f, CurrentFrame)).ToList();
        bool onlySelected = ticked.Count > 0;
        int n = 0;
        foreach (RollFrame f in Frames)
        {
            if (ReferenceEquals(f, CurrentFrame)) continue;
            if (onlySelected && !f.IsSelected) continue;
            FrameParams d = f.Params;
            d.QuarterTurns = src.QuarterTurns; d.FlipH = src.FlipH; d.FlipV = src.FlipV;
            d.Rotation = src.Rotation;
            SetThumbnail(f, null); n++;
        }
        StatusText = onlySelected
            ? Loc.F($"已把旋转 / 翻转和拉直应用到勾选的 {n} 帧")
            : Loc.F($"已把旋转 / 翻转和拉直应用到整卷（{n} 帧）");
        if (hadSelection) ClearStripSelection();
        MarkEdit();
        RestartThumbnails();
    }

    private void Broadcast(bool cal, bool scene, bool onlySelected, string what)
    {
        if (CurrentFrame is null) return;
        CommitUndo();   // close the previous edit as its own undo step
        FrameParams src = LiveParams.ForStorage(BuildParams(), _cropEditing, _cropRect);   // never store a suppressed crop
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
        if (n > 0) ClearStripSelection();
        MarkEdit();
        RestartThumbnails();
    }

    /// <summary>Copy the SyncOptions-enabled field groups from s into d.</summary>
    private void CopyGroups(FrameParams s, FrameParams d, bool cal, bool scene)
    {
        if (cal)
        {
            // 片基与两端一起走，没有单独的「白平衡」开关。两端端点就是白平衡（通道间差即
            // 色偏），而它们又是相对片基测的密度——分成两个开关就必然出现「只勾一个」的
            // 半套标定：换了片基却不换端点，等于让目标帧拿一把新尺子去量旧刻度。
            if (Sync.CalFilmBase)
            {
                d.TBase = (double[])s.TBase.Clone();
                d.DMinPerChannel = (double[])s.DMinPerChannel.Clone();
                d.DMaxPerChannel = (double[])s.DMaxPerChannel.Clone();
            }
            if (Sync.CalChroma) { d.ChromaChannelScale = (double[])s.ChromaChannelScale.Clone(); }
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
                // Travels WITH the points — the flag says how to read them, so copying one
                // without the other would re-anchor the curve on the receiving frames.
                d.CurveHasEndpoints = s.CurveHasEndpoints;
            }
        }
        if (Sync.GeomOrientation) { d.QuarterTurns = s.QuarterTurns; d.FlipH = s.FlipH; d.FlipV = s.FlipV; }
        if (Sync.GeomStraighten) d.Rotation = s.Rotation;
        // Re-anchored onto the target's own negative, not copied verbatim — see RebaseCrop.
        // Runs AFTER the orientation groups above so it reads the orientation the target will
        // actually have: the rect is stored in the oriented frame, and syncing a quarter turn in
        // the same pass would otherwise leave the crop measured against the old axes.
        if (Sync.GeomCrop) d.CropRect = RebaseCrop(s, d);
    }

    /// <summary>
    /// The source frame's crop, expressed against the TARGET frame's own negative.
    ///
    /// The algebra is <see cref="CropRebase"/>'s; what this adds is the orientation round trip.
    /// The cells are FILE-space rects while the crop is stored ORIENTED, so this takes the same
    /// three steps <see cref="ForRegion"/> does: down to file space, across, back out to the
    /// TARGET's orientation — which may differ from the source's, and by this point in
    /// <see cref="CopyGroups"/> is already whatever the sync left it.
    /// </summary>
    private static (double X, double Y, double W, double H)? RebaseCrop(FrameParams s, FrameParams d)
    {
        if (s.CropRect is not { } rect) return null;   // "no crop" travels as-is
        return OrientRect(CropRebase.Rebase(UnorientRect(rect, s)!.Value, s.SplitCell, d.SplitCell), d);
    }

}
