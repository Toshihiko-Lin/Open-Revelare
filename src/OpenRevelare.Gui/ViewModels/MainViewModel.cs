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
    private Task<PreviewCache.Entry> PreviewAsync(string path) => PreviewAsync(path, null);

    /// <summary>
    /// How much slack the region decode leaves around a split frame, as a fraction of the frame's
    /// own size on each side.
    ///
    /// The split is a coarse first guess and routinely clips into the picture, so the crop tool has
    /// to be able to push an edge back OUT. Decoding the frame exactly would make that impossible:
    /// there would be no pixels beyond the edge to reveal. Decoding the WHOLE strip instead is the
    /// other extreme — it spends the preview budget on the neighbours (a strip cut six ways leaves
    /// each frame ~265 of 1600 px, visibly soft) which is what the region decode existed to fix.
    ///
    /// 15% each side is the default compromise: it costs ~30% linear resolution against an exact
    /// cut and still leaves each frame far sharper than its share of a whole-strip preview, while
    /// covering the misdetections that actually happen (the split lands on a bright band INSIDE the
    /// frame, off by a fraction of a frame, not by a whole one).
    ///
    /// Set from the split dialog, where the user is deciding how much to trust these dividers in
    /// the first place — a clean strip wants 0 and the full resolution back, a strip the detector
    /// kept clipping wants room to drag into. Raising it is not free (the frame keeps a 1/(1+2m)
    /// share of the preview budget), which is why that dialog states the cost rather than
    /// presenting the choice as neutral. A miss bigger than the margin is a bad split rather than a
    /// bad crop, and re-splitting is the tool for that.
    /// </summary>
    [ObservableProperty] private double _splitMargin = 0.15;

    /// <summary>
    /// A new margin changes which pixels every split frame needs, so the current preview (decoded
    /// against the OLD box) is stale and the tiles are keyed by boxes nobody will ask for again.
    ///
    /// Normally a no-op in practice: the import sets this BEFORE the roll loads, so there is
    /// nothing decoded yet to invalidate. It is written to survive being set later anyway — the
    /// tiles are the sheet's and the film strip's source, and one keyed to a superseded box would
    /// keep drawing the old framing for the rest of the roll's life. The preview cache is left
    /// alone: it is an LRU that evicts itself, and its old entries are still valid images should
    /// the margin come back.
    /// </summary>
    partial void OnSplitMarginChanged(double value)
    {
        if (_splitPaths.Count == 0) return;   // nothing on this roll decodes by region
        ClearTiles();
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        if (!ResyncSplitPreview()) ScheduleRender();
        RestartThumbnails();
    }

    /// <summary>The margin box actually decoded for the current frame, in SOURCE-FILE coordinates,
    /// or null when <see cref="_previewLinear"/> is a plain whole-file preview. Paired with
    /// <see cref="_previewFrameRect"/>, which locates the frame inside it.</summary>
    private (double X, double Y, double W, double H)? _previewMargin;

    /// <summary>
    /// Where the frame sits inside <see cref="_previewLinear"/>, normalised against that buffer, or
    /// null when the buffer is not a margin decode.
    ///
    /// This is what the pipeline's crop stage must use in place of the stored rect: the stored rect
    /// is normalised against the WHOLE scan, and the buffer on hand is a small window onto it.
    /// </summary>
    private (double X, double Y, double W, double H)? _previewFrameRect;

    /// <summary>True when <see cref="_previewLinear"/> is a region decode rather than the whole
    /// file, so the stored crop rect does not describe it.</summary>
    private bool _previewPreCropped => _previewMargin is not null;

    /// <summary>
    /// Expand a frame's rect by <see cref="SplitMargin"/> on each side, clamped to the file.
    ///
    /// Clamping is why the margin cannot be assumed symmetric: the first and last frame of a strip
    /// sit against the file edge and get slack on one side only. Everything downstream therefore
    /// derives the frame's position from the two rects rather than assuming a fixed inset.
    /// </summary>
    private (double X, double Y, double W, double H) WithMargin(
        (double X, double Y, double W, double H) r)
    {
        double mx = r.W * SplitMargin, my = r.H * SplitMargin;
        double x0 = Math.Max(0.0, r.X - mx), y0 = Math.Max(0.0, r.Y - my);
        double x1 = Math.Min(1.0, r.X + r.W + mx), y1 = Math.Min(1.0, r.Y + r.H + my);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Re-express <paramref name="inner"/> (normalised against the whole file) relative to
    /// <paramref name="box"/> (likewise), i.e. as a rect of the decoded margin buffer.
    /// </summary>
    private static (double X, double Y, double W, double H) Relative(
        (double X, double Y, double W, double H) inner,
        (double X, double Y, double W, double H) box)
        => ((inner.X - box.X) / box.W, (inner.Y - box.Y) / box.H, inner.W / box.W, inner.H / box.H);

    /// <summary>The inverse of <see cref="Relative"/>: a rect of the margin buffer back to
    /// whole-file coordinates. This is how a crop drawn on screen becomes a storable rect.</summary>
    private static (double X, double Y, double W, double H) Absolute(
        (double X, double Y, double W, double H) inner,
        (double X, double Y, double W, double H) box)
        => (box.X + inner.X * box.W, box.Y + inner.Y * box.H, inner.W * box.W, inner.H * box.H);

    /// <summary>
    /// Params as they should be run against <see cref="_previewLinear"/> (or anything derived from
    /// it, such as the drag-resolution copy).
    ///
    /// On a split frame the decoder cut a MARGIN BOX out of the strip — the frame plus ~15% slack
    /// on each side — so the buffer is neither the whole scan (which the stored rect is normalised
    /// against) nor the frame itself. The rect is therefore rewritten into the buffer's own
    /// coordinates rather than dropped: dropping it would show the slack as part of the picture,
    /// and leaving it alone would cut a fraction of a fraction.
    ///
    /// While the crop tool is open the rect is suppressed entirely, which is what puts the slack on
    /// screen and lets a clipped edge be dragged back out.
    ///
    /// EVERY path that feeds _previewLinear to the pipeline goes through here. The rule lived
    /// inline in the debounced render for a while and the interactive drag path did not have it,
    /// so moving a slider re-cropped the preview while the settled render did not.
    /// </summary>
    private FrameParams ForPreview(FrameParams p)
    {
        if (_previewMargin is null) return p;                  // plain whole-file preview
        if (p.CropRect is null && _previewFrameRect is null) return p;
        p = p.Clone();
        // Oriented to match the pixels: ApplyCrop runs AFTER orientation, and _previewFrameRect is
        // measured in the raw file's axes.
        p.CropRect = _cropEditing ? null : OrientRect(_previewFrameRect, p);
        return p;
    }

    /// <summary>
    /// Carry a raw-space rect through the orientation the pipeline will have applied by the time it
    /// crops, so the two agree.
    ///
    /// <see cref="Pipeline.ProcessFrame"/> orients first and crops second, and a quarter turn swaps
    /// the buffer's axes underneath the rect. The straighten rotation is not applied here: it
    /// preserves the buffer's size, so a normalised rect survives it unchanged — the same reason
    /// <see cref="CropFrameSize"/> ignores it.
    /// </summary>
    private static (double X, double Y, double W, double H)? OrientRect(
        (double X, double Y, double W, double H)? rect, FrameParams p)
    {
        if (rect is not { } r) return null;
        for (int i = 0; i < (((p.QuarterTurns % 4) + 4) % 4); i++) r = RotateCropCw(r);
        if (p.FlipH) r = FlipCropH(r);
        if (p.FlipV) r = FlipCropV(r);
        return r;
    }

    /// <summary>
    /// <see cref="ForPreview"/> for an ARBITRARY frame rendered off a region decode of its own —
    /// the film strip, the sheet tiles, the contact sheet. Same rule, but the frame's position in
    /// its box is derived from the arguments instead of the current-frame fields.
    /// </summary>
    /// <param name="margin">The box that was decoded, in file coordinates, or null for a whole-file
    /// buffer (in which case the params are already right and come back untouched).</param>
    private static FrameParams ForRegion(FrameParams p, RollFrame f,
                                         (double X, double Y, double W, double H)? margin)
    {
        if (margin is not { } box || f.Params.CropRect is not { } rect) return p;
        p = p.Clone();
        // Down to file space, in against the box, back out to oriented space — the box itself is a
        // file-space rect, so the middle step cannot happen in the oriented frame.
        p.CropRect = OrientRect(Relative(UnorientRect(rect, f.Params)!.Value, box), p);
        return p;
    }

    /// <summary>The inverse of <see cref="OrientRect"/>: an oriented-frame rect back into the raw
    /// file's axes. Undoes the flips first, then the turns, because the forward order is turns
    /// then flips.</summary>
    private static (double X, double Y, double W, double H)? UnorientRect(
        (double X, double Y, double W, double H)? rect, FrameParams p)
    {
        if (rect is not { } r) return null;
        if (p.FlipV) r = FlipCropV(r);
        if (p.FlipH) r = FlipCropH(r);
        for (int i = 0; i < (((p.QuarterTurns % 4) + 4) % 4); i++) r = RotateCropCcw(r);
        return r;
    }

    /// <summary>True once <see cref="LoadParams"/> has pushed the current frame's params into the
    /// UI, i.e. once <see cref="_cropRect"/> describes the CURRENT frame rather than the one being
    /// switched away from. <see cref="SplitCropOf"/> needs the distinction.</summary>
    private bool _paramsLoaded;

    /// <summary>
    /// Fold the live control values back into <paramref name="frame"/> — the ONE way a frame's
    /// stored params are updated from the UI.
    ///
    /// Refuses while <see cref="_paramsLoaded"/> is false, which is the whole point of it existing.
    /// Between <c>CurrentFrame = …</c> and the <see cref="LoadParams"/> at the end of
    /// <see cref="SwitchFrameAsync"/> the controls still hold the OUTGOING frame's state (or, on a
    /// fresh roll, the previous roll's), so writing them onto the incoming frame does not save an
    /// edit — it invents one. <see cref="BuildParams"/> reads <see cref="_cropRect"/> for
    /// <see cref="FrameParams.CropRect"/>, so on an import that window ends with the frame's crop
    /// REPLACED BY NULL.
    ///
    /// That window is wide open on import and the film strip writes into it: the strip binds
    /// SelectedItem two-way to <see cref="CurrentFrame"/>, so rebuilding Frames pushes the
    /// selection back through the binding and re-enters <see cref="OnCurrentFrameChanged"/>, whose
    /// outgoing-frame fold is this call. The decode it is racing is the roll's FIRST, so the victim
    /// is always frame 1 — and on a split import frame 1's params are its share of the strip, which
    /// is why a multi-strip import (several files' decodes queued at the gate ahead of it, so the
    /// window stays open far longer) showed the first strip's first negative uncropped: the whole
    /// scan, with its pre-crop erased before it was ever applied. The <see cref="HasImage"/> guard
    /// at the call site does not cover this — HasImage stays true from the previous roll.
    ///
    /// Nothing is lost by refusing: a frame whose params have not been loaded yet has no live edit
    /// to capture, and the frame already holds the params the load put there.
    /// </summary>
    private void CommitLiveParams(RollFrame? frame)
    {
        if (frame is null || !_paramsLoaded) return;
        frame.Params = BuildParams();
    }

    /// <summary>
    /// The frame's own rect within its source file, or null when the frame owns the whole file.
    /// Split frames only — on an ordinary frame the file IS the frame.
    ///
    /// UN-oriented on the way out. CropRect is stored against the ORIENTED frame (that is what
    /// makes RotateCropCw and friends correct, and the whole reason the crop travels with a quarter
    /// turn), but the region decoder addresses the raw file. The two coincide at import, when
    /// orientation is identity — which is why this went unnoticed — and diverge the moment the user
    /// rotates a split frame: the decoder would then cut a sideways box out of the strip.
    /// </summary>
    private (double X, double Y, double W, double H)? SplitRectOf(RollFrame frame)
    {
        if (!_splitPaths.Contains(frame.Path)) return null;
        // The current frame's crop lives in _cropRect and is only written back to Params when the
        // frame is left, so a crop the user just committed is not in Params yet — the resync that
        // follows SetCrop would re-bake the region the user just replaced. Read the live rect, but
        // only once _paramsLoaded says _cropRect actually belongs to this frame: during a frame
        // SWITCH it still holds the outgoing frame's crop.
        bool current = ReferenceEquals(frame, CurrentFrame) && _paramsLoaded;
        var live = current ? _cropRect : frame.Params.CropRect;
        if (live is not { } rect) return null;
        // A full-frame rect gains nothing from the region path and would only bypass the cache
        // entry the rest of the roll shares. Checked before un-orienting: a turn permutes the
        // rect's components but not its full-frame-ness.
        if (rect.W >= 0.999 && rect.H >= 0.999) return null;
        // The live orientation for the current frame, the stored one otherwise — matching whichever
        // rect was just read, since the two travel together.
        return UnorientRect(rect, current
            ? new FrameParams { QuarterTurns = _quarterTurns, FlipH = _flipH, FlipV = _flipV }
            : frame.Params);
    }

    /// <summary>
    /// The box to cut from the source before downsampling, or null to preview the whole file.
    ///
    /// This is the frame's rect plus <see cref="SplitMargin"/> on each side. The slack is decoded
    /// unconditionally — including when the crop tool is closed — because it is what the tool needs
    /// the moment it opens, and re-decoding on entry would put a visible stall in front of every
    /// crop. At the 0.15 default it costs ~30% linear resolution against an exact cut, which is
    /// still far sharper than this frame's share of a whole-strip preview.
    ///
    /// At a margin of 0 the box collapses onto the frame and the crop below becomes a whole-buffer
    /// copy. That is the correct reading of "cut exactly, keep every pixel of resolution, and give
    /// up expanding" — not a case to special-case away.
    /// </summary>
    private (double X, double Y, double W, double H)? SplitCropOf(RollFrame frame)
        => SplitRectOf(frame) is { } rect ? WithMargin(rect) : null;

    /// <summary>
    /// Sources that more than one frame draws on — the split scans.
    ///
    /// Rebuilt from the frame list rather than remembered from the import, so a roll REOPENED
    /// from its .ncproj gets the sharp region previews too. The project file stores each frame's
    /// path and crop, which is all this needs; nothing extra had to be persisted.
    /// </summary>
    private readonly HashSet<string> _splitPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recompute <see cref="_splitPaths"/> from the current frames.</summary>
    private void RefreshSplitPaths()
    {
        _splitPaths.Clear();
        foreach (var group in Frames.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2) continue;
            // Virtual copies of a WHOLE frame share every pixel and must keep sharing one cache
            // entry; only frames carrying different crops are separate images.
            var crops = group.Select(f => f.Params.CropRect).Distinct().ToList();
            if (crops.Count <= 1) continue;
            _splitPaths.Add(group.Key);
            // Backfill the cells for a roll reopened from a project written before they were
            // recorded — the same test identifies both, since a group with differing crops IS a
            // split scan. The crop is all such a file has, and for a split frame not since
            // re-cropped by hand it IS the cell, the equality the import starts from. A frame the
            // user did crop comes back with its cell set to that crop rather than to the whole
            // negative, which costs some of the shape on the next broadcast but still keeps every
            // frame on its own negative; guessing a wider cell would be inventing pixels.
            foreach (RollFrame f in group)
                f.Params.SplitCell ??= f.Params.CropRect;
        }
    }

    /// <summary>
    /// Cache identity of one decoded image: the file, plus the region of it that was decoded.
    ///
    /// The rect has to be in the key because a split scan is several DIFFERENT images inside one
    /// file — keying on the path alone hands every frame of the strip whichever slice was decoded
    /// first. Virtual copies of a whole frame carry no rect and so keep sharing one entry, which
    /// is what they should do: they really are the same pixels.
    /// </summary>
    private static string PreviewKey(string path, (double X, double Y, double W, double H)? preCrop)
        => preCrop is { } pc ? $"{path}|{pc.X:F6},{pc.Y:F6},{pc.W:F6},{pc.H:F6}" : path;

    /// <summary>
    /// As above, but for a frame that owns only part of its source file.
    ///
    /// A split scan holds several negatives, and previewing one by downsampling the whole strip
    /// and cropping a slice out of it spends the preview budget on the other frames: a strip cut
    /// six ways leaves each frame about 260 px of the 1600, which is visibly soft. When
    /// <paramref name="preCrop"/> is given the region is cut from the source FIRST and
    /// downsampled after, so the frame gets the whole budget. Such previews are cached under a
    /// key that includes the rect — six frames of one file are six different images, and sharing
    /// one entry between them (which is right for virtual copies of a whole frame) would serve
    /// each of them the first one's pixels.
    /// </summary>
    private Task<PreviewCache.Entry> PreviewAsync(string path,
                                                  (double X, double Y, double W, double H)? preCrop)
    {
        string key = PreviewKey(path, preCrop);

        if (_previews.Get(key) is { } hit) { CaptureTile(key, hit.Preview); return Task.FromResult(hit); }
        lock (_decoding)
        {
            if (_decoding.TryGetValue(key, out Task<PreviewCache.Entry>? running)) return running;
            Task<PreviewCache.Entry> task = Task.Run(() =>
            {
                // Straight to preview size: the full-resolution float frame this used to decode
                // and immediately throw away is the biggest allocation in the program.
                var (preview, srcW, srcH) = preCrop is { } rect
                    ? ImageIo.LoadPreviewRegion(path, rect, PreviewMaxEdge)
                    : ImageIo.LoadPreview(path, PreviewMaxEdge);
                var e = new PreviewCache.Entry(preview, srcW, srcH);
                _previews.Put(key, e.Preview, e.SourceWidth, e.SourceHeight);
                CaptureTile(key, e.Preview);
                return e;
            });
            _decoding[key] = task;
            _ = task.ContinueWith(_ => { lock (_decoding) _decoding.Remove(key); },
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
    // what a params change changes.
    //
    // Keyed by PREVIEW KEY, not by source path — a split scan's six negatives are six tiles. Under
    // a path key the first slice decoded claimed the entry for the whole strip (CaptureTile returns
    // early when the key is present), so the other five frames drew their thumbnail from frame 1's
    // pixels and then cropped THAT by their own rect: a slice of the wrong slice, at whatever
    // aspect the double crop produced. Virtual copies of a whole frame have no rect in their key
    // and still share one tile, which is correct.
    private const int TileMaxEdge = 320;   // ≈ the cell width of a 2048 px sheet at 6 columns
    private readonly Dictionary<string, ImageBuffer> _tiles = new(StringComparer.OrdinalIgnoreCase);

    private void CaptureTile(string key, ImageBuffer preview)
    {
        lock (_tiles)
        {
            if (_tiles.ContainsKey(key)) return;
            _tiles[key] = Resample.Box(preview, TileMaxEdge);
        }
    }

    /// <summary>The tile for a frame, which is the tile of the region that frame owns.</summary>
    private ImageBuffer? TileFor(RollFrame f)
    {
        string key = PreviewKey(f.Path, SplitCropOf(f));
        lock (_tiles) return _tiles.TryGetValue(key, out ImageBuffer? t) ? t : null;
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
    /// <summary>
    /// True while the roll-wide auto-inversion is still pooling frames — i.e. while what is on
    /// screen comes from stage 1's SINGLE-frame measurement broadcast to the whole roll.
    ///
    /// Drives a dismissible notice over the preview. The provisional state is not a defect worth
    /// hiding, but it is indistinguishable from the finished result by eye: the opening frame's
    /// solve stands in for the roll, and on a roll whose first frame is unrepresentative (roll 21
    /// opens on P8060012, whose kept area holds no highlight) every thumbnail is visibly off until
    /// stage 2 lands and the strip jumps. Saying so is what stops that reading as "this tool is
    /// broken" during the seconds before the real answer arrives.
    ///
    /// Set alongside stage 1's broadcast and cleared by <see cref="FinishAutoInvert"/>, so it
    /// tracks the analysis rather than a timer.
    /// </summary>
    [ObservableProperty] private bool _rollAnalysisPending;

    /// <summary>
    /// Set when the user dismisses the <see cref="RollAnalysisPending"/> notice, so it stays down
    /// for the rest of THIS analysis. Cleared when a new one starts — a dismissal is about the
    /// notice in front of them, not a permanent preference.
    /// </summary>
    [ObservableProperty] private bool _rollAnalysisNoticeDismissed;

    /// <summary>Whether the notice is actually on screen: pending AND not dismissed.</summary>
    public bool ShowRollAnalysisNotice => RollAnalysisPending && !RollAnalysisNoticeDismissed;

    partial void OnRollAnalysisPendingChanged(bool value)
        => OnPropertyChanged(nameof(ShowRollAnalysisNotice));

    partial void OnRollAnalysisNoticeDismissedChanged(bool value)
        => OnPropertyChanged(nameof(ShowRollAnalysisNotice));

    /// <summary>Dismiss the roll-analysis notice. The analysis itself keeps running.</summary>
    public void DismissRollAnalysisNotice() => RollAnalysisNoticeDismissed = true;

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

    partial void OnCurrentFrameChanged(RollFrame? value)
    {
        // A reorder pulls the selected frame out of Frames and puts it back, which the strip's
        // SelectedItem binding reports as null-then-reselect. Nothing about the frame changed, so
        // neither half of that is a real switch: folding params against the null would run with no
        // outgoing frame, and the re-select would re-render a frame already on screen.
        if (_reordering) return;

        // Persist the outgoing frame's live edits before swapping in the new one.
        // Skipped during a restore switch — the frames already hold the restored params.
        // _paramsLoaded (inside CommitLiveParams) is the load-in-flight half of this guard, and it
        // is the half that matters on import: HasImage stays true from the PREVIOUS roll, so on its
        // own it lets the incoming roll's first frame be overwritten with the old roll's controls.
        if (_prevFrame is not null && HasImage && !_restoring && _paramsLoaded)
        {
            CommitUndo();   // flush any pending edit on the outgoing frame
            CommitLiveParams(_prevFrame);
            RefreshThumbnail(_prevFrame);
        }
        _prevFrame = value;
        if (value is not null) _ = SwitchFrameAsync(value);
    }

    /// <summary>True while <see cref="Reorder"/> is shuffling Frames — see the guard above.</summary>
    private bool _reordering;

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
        CommitLiveParams(CurrentFrame);
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

    /// <summary>
    /// The camera's own colour matrix for this roll, read once at import. Roll-level like
    /// <see cref="_decoupleMatrix"/> and for the same reason: it is a property of the capture
    /// device, identical for every frame, and re-reading it per frame would only cost time.
    ///
    /// Null for scans (their ICC path already characterises them) and for cameras LibRaw does
    /// not know, which is the historical uncharacterised behaviour.
    /// </summary>

    // Path A 分光解耦（卷级；导入时从 R/G/B 校正图算出，应用到整卷）
    private double[,]? _decoupleMatrix;
    // NOT accompanied by a DecoupleChromaAmp, deliberately. Inversion treats amp and the chroma
    // matrix as mutually EXCLUSIVE paths, not as two layers: with a matrix present it multiplies
    // by the bare chroma_grade and never reads amp (Inversion.cs, the useMatrix branch). The
    // matrix is the better of the two — ChromaAxisCompensationMatrix already carries 1/amp per
    // chroma AXIS (yellow-blue, red-green), where amp is one scalar per RGB channel — so the
    // roll carries the matrix alone. The CLI computes both only because --decouple-chroma-amp
    // exists as a fallback for callers that have no matrix. Setting one here would be dead state.
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

    /// <summary>
    /// Rebuild the red diagnostic overlay showing which pixels the sprocket threshold catches.
    ///
    /// The mask is measured on the NEGATIVE — sprocket holes and the light board are the brightest
    /// raw pixels, which is the whole basis of the threshold — but it is displayed stretched over
    /// the finished preview, and that preview has been through the geometry stage. So the mask has
    /// to make the same journey: orient, straighten, crop. Skipping it lines a whole-strip mask up
    /// against a single cropped frame, and every hole sits somewhere it does not belong. The
    /// mismatch is worst on a split scan, where the preview is one sixth of what the mask covers,
    /// but a plain rotation or crop misplaces it just as surely.
    /// </summary>
    private void UpdateSprocketOverlay()
    {
        if (!ShowSprocketMask || _previewLinear is null) { SprocketMaskOverlay = null; return; }

        // Carry the mask as an image so the existing geometry operators can move it: they resample
        // pixels, and a bool[] has no resampler. 1 = masked.
        var flags = new ImageBuffer(_previewLinear.Width, _previewLinear.Height);
        bool[] raw = Sprocket.MakeMask(_previewLinear.Data, _previewLinear.PixelCount,
                                       (float)SprocketThreshold);
        for (int p = 0; p < raw.Length; p++)
        {
            if (!raw[p]) continue;
            int b = p * 3;
            flags.Data[b] = flags.Data[b + 1] = flags.Data[b + 2] = 1.0f;
        }

        if (_quarterTurns != 0 || _flipH || _flipV)
            flags = Geometry.ApplyOrientation(flags, _quarterTurns, _flipH, _flipV);
        if (Rotation != 0.0)
            flags = Geometry.ApplyRotation(flags, Rotation, fill: 0.0f);   // rotated-in corners are not mask
        // Exactly the crop the PICTURE gets, taken from the same place the picture takes it: on a
        // split frame that is the frame's box-relative rect, not the stored whole-scan one, and
        // while the crop tool is open it is no crop at all. Deriving it independently here is how
        // the mask and the picture drift apart.
        if (ForPreview(BuildParams()).CropRect is { } rect)
            flags = Geometry.ApplyCrop(flags, rect);

        var shaped = new bool[flags.PixelCount];
        for (int p = 0; p < shaped.Length; p++) shaped[p] = flags.Data[p * 3] > 0.5f;
        SprocketMaskOverlay = BitmapConvert.ToMaskOverlay(shaped, flags.Width, flags.Height);
    }

    // 输出意图不再是胶卷级模式：预览恒为完整渲染，"线性" 是单次导出的属性
    // （导出弹窗的「导出为场景线性 ACEScg」勾选框），见 ForExport。

    // ══ 反相：两端各三个绝对密度，就这六个数 ═══════════════════════════════════
    //
    // 渲染消费 scale[3]+offset[3]，这里就存六个数，一一对应。历史上这里曾有十余个参数描述
    // 同样的六个自由度（grade/pivot、wb_high、wb_offset、d_max、scan_ev），每一个多余的都
    // 表现为「两个滑块做同一件事」，且迟早被同时写入、把一个校正做两遍。
    //
    // 用户想调的三件事都是这六个数的不同读法，**不需要额外字段**：
    //
    //   两端拉近/拉远  → 反差    实测 ±23%
    //   通道间差       → 色偏    展开三个分量各自调整
    //
    // 没有「亮度」：两端同向移动虽然保住跨度，却会让各通道 offset 变得不一样多（实测 R/B
    // 偏 ±3.5%），不是零色偏的亮度。真正零色偏的亮度是线性域乘常数 = 曝光，在 Stage 2。
    //
    // 所以界面上没有「亮度」「反差」「色温」这些字段——它们是 D_min / D_max 两个标量与其
    // 展开分量的派生读数，任何为它们单独立字段的做法都是在重新制造上面那个局面。

    /// <summary>片基透射率 T_base——把裸片基放到密度 0 的除数。暗端密度相对它陈述。</summary>
    [ObservableProperty] private double _tBaseR = 0.82;
    [ObservableProperty] private double _tBaseG = 0.51;
    [ObservableProperty] private double _tBaseB = 0.29;
    partial void OnTBaseRChanged(double value) => ScheduleRender();
    partial void OnTBaseGChanged(double value) => ScheduleRender();
    partial void OnTBaseBChanged(double value) => ScheduleRender();

    /// <summary>暗端：逐通道黑点密度（对 T=1 的绝对值）。橙色片基必然 R&lt;G&lt;B。</summary>
    [ObservableProperty] private double _dMinR;
    [ObservableProperty] private double _dMinG;
    [ObservableProperty] private double _dMinB;
    partial void OnDMinRChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }
    partial void OnDMinGChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }
    partial void OnDMinBChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }

    /// <summary>亮端：逐通道白点密度（典型 1.8–2.4）。高光白平衡就是这三个数。</summary>
    [ObservableProperty] private double _dMaxR = 2.0;
    [ObservableProperty] private double _dMaxG = 2.0;
    [ObservableProperty] private double _dMaxB = 2.0;
    partial void OnDMaxRChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }
    partial void OnDMaxGChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }
    partial void OnDMaxBChanged(double value) { SyncScalarsFromEndpoints(); ScheduleRender(); }

    /// <summary>暗端三个分量的数组视图。同一份数据，不是第二个字段。</summary>
    public double[] DMinPerChannel
    {
        get => new[] { DMinR, DMinG, DMinB };
        set { if (value is { Length: 3 }) { DMinR = value[0]; DMinG = value[1]; DMinB = value[2]; } }
    }

    /// <summary>亮端三个分量的数组视图。</summary>
    public double[] DMaxPerChannel
    {
        get => new[] { DMaxR, DMaxG, DMaxB };
        set { if (value is { Length: 3 }) { DMaxR = value[0]; DMaxG = value[1]; DMaxB = value[2]; } }
    }

    // ── 两个标量：D_min / D_max ────────────────────────────────────────────────
    //
    // 每个标量是那一端的**算术均值**，展开的三个分量是它的明细。父子关系：拖标量 = 三个
    // 分量同步平移（加性，严格保住通道间差 = 色偏不变）；改分量 = 只动色偏，标量不变。
    //
    // 加性而非乘性：加性平移的通道间差逐位保持，乘性缩放则会让差值按比例变化。而且暗端
    // 常态就在 0，几何均值在那里没有定义。

    private bool _syncingEndpointView;

    /// <summary>暗端位置（三个暗端密度的均值）。</summary>
    [ObservableProperty] private double _dMinLevel;
    /// <summary>亮端位置（三个亮端密度的均值）。与 D_min 的距离即反差。</summary>
    [ObservableProperty] private double _dMaxLevel = 2.0;

    partial void OnDMinLevelChanged(double value) => PushLevel(shadow: true);
    partial void OnDMaxLevelChanged(double value) => PushLevel(shadow: false);

    /// <summary>某一端的标量 → 该端三个分量同步平移，保住通道间差。</summary>
    private void PushLevel(bool shadow)
    {
        if (_syncingEndpointView) return;
        _syncingEndpointView = true;
        try
        {
            if (shadow)
            {
                double d = DMinLevel - Mean(DMinPerChannel);
                DMinR += d; DMinG += d; DMinB += d;
            }
            else
            {
                double d = DMaxLevel - Mean(DMaxPerChannel);
                DMaxR += d; DMaxG += d; DMaxB += d;
            }
        }
        finally { _syncingEndpointView = false; }
        ScheduleRender();
    }

    /// <summary>六个分量 → 三个标量读数。采样、自动标定、载入工程后都要刷新。</summary>
    private void SyncScalarsFromEndpoints()
    {
        if (_syncingEndpointView) return;
        _syncingEndpointView = true;
        try
        {
            DMinLevel = Mean(DMinPerChannel);
            DMaxLevel = Mean(DMaxPerChannel);
        }
        finally { _syncingEndpointView = false; }
    }

    private static double Mean(double[] v) => (v[0] + v[1] + v[2]) / 3.0;

    /// <summary>两端的标量读数一起刷新。载入工程 / 重置 / 整卷标定后调用。</summary>
    private void SyncEndpointViews() => SyncScalarsFromEndpoints();

    /// <summary>
    /// 这一卷的标定来自旧模型，载入后画面与保存时不同。旧的 d_max/scan_ev 与现在固定的输出
    /// 范围不是同一量纲，静默折算实测在薄部偏 -18%、浓部 +53%（比不折算更糟），所以如实提示
    /// 用户重跑标定。
    /// </summary>
    [ObservableProperty] private bool _needsRecalibration;

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

    // ── 过曝/欠曝指示（纯视图诊断，不存工程） ──────────────────────────────────
    [ObservableProperty] private bool _showClipping;
    [ObservableProperty] private Bitmap? _clippingOverlay;

    partial void OnShowClippingChanged(bool value)
    {
        if (!value) ClippingOverlay = null;
        else ScheduleRender();
    }

    partial void OnClippingOverlayChanging(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue)) Retire(oldValue);
    }

    private WriteableBitmap? BuildClippingOverlay(ImageBuffer outImg)
    {
        ClippingDetect.Detect(outImg.Data, outImg.PixelCount,
                              0.02f, 0.98f, out bool[] shadows, out bool[] highlights);
        return BitmapConvert.ToClippingOverlay(shadows, highlights, outImg.Width, outImg.Height);
    }

    // ══ Geometry (Core applies: orientation → straighten → crop) ════════════════
    [ObservableProperty] private double _rotation;                 // 拉直角度（CW）
    private int _quarterTurns;
    private bool _flipH, _flipV;
    private (double X, double Y, double W, double H)? _cropRect;

    /// <summary>The current frame's <see cref="FrameParams.SplitCell"/>, carried alongside
    /// <see cref="_cropRect"/> so <see cref="BuildParams"/> can put it back.
    ///
    /// No control edits this — it is fixed at import and only ever read. It has to be held live
    /// all the same, because BuildParams rebuilds the whole params object from these fields and
    /// anything not listed there is DROPPED: leaving it out would erase the current frame's cell
    /// on the next commit, and the crop broadcast would be back to collapsing the copies for
    /// whichever frame the user had been looking at.</summary>
    private (double X, double Y, double W, double H)? _splitCell;

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
    /// <remarks>
    /// The cache is consulted under the SAME key the current preview was decoded under, not the
    /// bare path — on a split frame the file holds the whole strip while the buffer on screen is
    /// this frame's margin box, and it is the box the view's rect is normalised against (see the
    /// coordinate-bridge note above <see cref="CurrentCrop"/>). Reading the bare path would hand an
    /// aspect-ratio preset the strip's ≈6:1 while the user is looking at a single 3:2 negative.
    /// </remarks>
    public (int W, int H)? CropFrameSize
    {
        get
        {
            int w, h;
            if (CurrentFrame is { } f && _previews.Get(PreviewKey(f.Path, SplitCropOf(f))) is { } e)
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

    /// <summary>
    /// Opening the tool reveals the decoded slack; closing it hides it again.
    ///
    /// No decode either way: the margin box is already in <see cref="_previewLinear"/>, so this is
    /// a pure render toggle — <see cref="ForPreview"/> stops applying the frame rect and the ~15%
    /// beyond each edge comes into view, which is exactly the material a too-tight split needs to
    /// be dragged back over. That is the whole reason the slack is decoded up front rather than
    /// fetched on entry: a re-decode here would stall the start of every crop.
    /// </summary>
    public bool CropEditing
    {
        get => _cropEditing;
        set
        {
            if (_cropEditing == value) return;
            _cropEditing = value;
            OnPropertyChanged(nameof(CropFrameSize));   // the space the rect is normalised against
            ScheduleRender();
        }
    }

    /// <summary>
    /// Re-decode when the current frame's REGION changes — a committed crop moves the margin box,
    /// so the buffer on hand no longer covers the right part of the strip. Returns whether a reload
    /// was started (the caller renders itself if not).
    ///
    /// Comparing against the key the current buffer was loaded under makes it a no-op in every
    /// other case, so it is safe to call on any crop change.
    /// </summary>
    private bool ResyncSplitPreview()
    {
        if (CurrentFrame is not { } f || !_splitPaths.Contains(f.Path)) return false;
        var pre = SplitCropOf(f);
        string want = PreviewKey(f.Path, pre);
        if (want == _previewKey) return false;
        _ = ReloadRegionAsync(f, pre, want);
        return true;
    }

    /// <summary>The preview-cache key <see cref="_previewLinear"/> was loaded under, so a resync can
    /// tell whether the buffer on hand is already the right region.</summary>
    private string? _previewKey;

    /// <summary>
    /// Point <see cref="_previewLinear"/> at a different region of the same file.
    ///
    /// Guarded by the frame-switch token for the same reason <see cref="SwitchFrameAsync"/> is: the
    /// decode is awaited, and a user who leaves the frame mid-decode must not have the outgoing
    /// frame's pixels land on the incoming one.
    /// </summary>
    private async Task ReloadRegionAsync(RollFrame frame,
                                         (double X, double Y, double W, double H)? pre, string key)
    {
        int tok = _switchToken;
        try
        {
            PreviewCache.Entry entry = await PreviewAsync(frame.Path, pre);
            if (tok != _switchToken) return;
            AdoptPreview(frame, entry, pre, key);
            _dragSmall = null;               // belongs to the buffer we just replaced
            OnPropertyChanged(nameof(CropFrameSize));
            ScheduleRender();
        }
        catch (Exception ex) { if (tok == _switchToken) ReportRenderFailure(ex); }
    }

    /// <summary>
    /// Install a decoded preview as the current one, recording which region of the file it is.
    ///
    /// The frame's position INSIDE the margin box is computed here, once, rather than re-derived at
    /// each render: the margin is clamped at the file edges, so the first and last frame of a strip
    /// are not centred in their box and no fixed inset describes them.
    /// </summary>
    private void AdoptPreview(RollFrame frame, PreviewCache.Entry entry,
                              (double X, double Y, double W, double H)? margin, string key)
    {
        _previewLinear = entry.Preview;
        _previewMargin = margin;
        _previewFrameRect = margin is { } box && SplitRectOf(frame) is { } rect
                                ? Relative(rect, box)
                                : null;
        _previewKey = key;
    }

    // ── The crop tool's coordinate bridge ───────────────────────────────────────
    //
    // The view speaks DISPLAYED-FRAME coordinates: whatever is on screen while the tool is open,
    // normalised 0..1. The model stores WHOLE-FILE coordinates. On an ordinary frame those are the
    // same space and both conversions are the identity, which is why this never had to exist.
    //
    // On a split frame the screen is showing the margin box — the frame plus ~15% slack — so the
    // two differ by that box, and by the orientation between the raw file and the display. Getting
    // this wrong does not look like an error: the crop simply frames the wrong part of the picture,
    // which is precisely the class of bug the orientation comment above records.

    /// <summary>
    /// A stored (oriented-frame) rect as the view should draw it, or null if there is none.
    ///
    /// Must be the exact inverse of <see cref="FromDisplay"/>, so it mirrors its structure: the
    /// identity on an ordinary frame — the stored space and the drawn space are both the oriented
    /// frame — and a down-to-file / relative-to-box / back-out round trip on a split one.
    /// </summary>
    private (double X, double Y, double W, double H)? ToDisplay(
        (double X, double Y, double W, double H)? rect)
    {
        if (_previewMargin is not { } box || rect is not { } r) return rect;
        FrameParams p = BuildParams();
        return OrientRect(Relative(UnorientRect(r, p)!.Value, box), p);
    }

    /// <summary>
    /// The inverse of <see cref="ToDisplay"/>: what the view drew, as a storable rect.
    ///
    /// The stored rect lives in the ORIENTED frame (see <see cref="SplitRectOf"/> for why — it is
    /// what makes <see cref="RotateCropCw"/> and the pipeline's orient-then-crop order agree), and
    /// the view already draws in that space. So on an ordinary frame this is the IDENTITY, and the
    /// un-orient exists solely to reach the margin box, which is a FILE-space rect: down to file
    /// space, in against the box, then back out to oriented space — the same three steps
    /// <see cref="ForRegion"/> takes, in the same order.
    ///
    /// That closing re-orient used to be missing. The round trip through <see cref="ToDisplay"/>
    /// still looked right (it re-oriented on the way back out), which is what hid it — but
    /// everything that reads <see cref="_cropRect"/> DIRECTLY got a raw-axes rect where an
    /// oriented one was promised: <see cref="Pipeline.ProcessFrame"/>, which crops after
    /// orienting, and the rotate buttons, which turn the stored rect with the picture. Crop a
    /// rotated frame and the applied result came out with the axes swapped and the position
    /// drifted — the frame on screen was right, what landed was not.
    /// </summary>
    private (double X, double Y, double W, double H) FromDisplay(
        (double X, double Y, double W, double H) rect)
    {
        if (_previewMargin is not { } box) return rect;   // ordinary frame: already oriented
        FrameParams p = BuildParams();
        // Undo the orientation in reverse order: the forward direction is turns then flips.
        if (p.FlipV) rect = FlipCropV(rect);
        if (p.FlipH) rect = FlipCropH(rect);
        for (int i = 0; i < (((p.QuarterTurns % 4) + 4) % 4); i++) rect = RotateCropCcw(rect);
        return OrientRect(Absolute(rect, box), p)!.Value;
    }

    /// <summary>The stored crop, so re-entering the crop tool ADJUSTS the existing frame instead
    /// of starting over. In the view's coordinates — on a split frame the stored rect describes the
    /// whole scan, but the tool is drawing over the margin box.</summary>
    public (double X, double Y, double W, double H)? CurrentCrop => ToDisplay(_cropRect);

    public void SetCrop((double X, double Y, double W, double H) rect)
    {
        _cropRect = FromDisplay(rect);
        var s = _cropRect.Value;
        StatusText = Loc.F($"裁切 {s.X:F2},{s.Y:F2},{s.W:F2},{s.H:F2}");
        // A split frame's new rect moves its margin box, so the region on hand is the wrong part of
        // the strip; ResyncSplitPreview re-decodes and renders. No-op for everyone else.
        if (!ResyncSplitPreview()) ScheduleRender();
    }
    public void ClearCrop()
    {
        _cropRect = null;
        StatusText = Loc.T("已清除裁切");
        // Clearing a split frame's crop means it now owns the WHOLE scan — back to the strip.
        if (!ResyncSplitPreview()) ScheduleRender();
    }

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

    /// <summary>
    /// True while the preview is showing the UN-INVERTED negative (film-base sampling).
    ///
    /// The sharp patch used to be rendered through the full pipeline unconditionally, so zooming
    /// past the patch threshold while picking the film base pasted the finished positive — masked
    /// and inverted — over the negative it was meant to be sampled from. The patch now renders in
    /// the same un-inverted form as the view around it (RegionRender's negative mode), so
    /// pixel-peeping a film-base sample shows real grain instead of the wrong picture.
    /// </summary>
    private bool _showingNegative;

    /// <summary>
    /// Per-source-file camera as-shot white balance, green-normalised — the DISPLAY gain that
    /// makes the negative view look like film on a light table instead of a green cast.
    ///
    /// Cached because the negative view is re-drawn on every turn of the frame while it is up,
    /// and because a null answer (TIFF scan, a camera with no as-shot record) must be remembered
    /// too — otherwise every redraw re-opens the file to learn nothing again. Hence the nullable
    /// VALUE in the dictionary rather than "absent means unknown".
    ///
    /// Keyed by PATH, like the preview cache, so a split scan's frames and any virtual copies
    /// share the one probe. Cleared with the roll.
    /// </summary>
    private readonly Dictionary<string, double[]?> _negativeWb = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The display white balance for the frame on screen, probing the file at most once.
    ///
    /// Null — meaning "show the UniWB decode as-is" — for a TIFF (a scanner's output is already
    /// balanced; there is no camera and no as-shot record to undo) and for any RAW whose
    /// coefficients cannot be read. Deliberately NOT a fallback guess: inventing gains would put
    /// an invented colour under a tool whose whole job is judging colour by eye.
    /// </summary>
    private double[]? CurrentNegativeWb()
    {
        if (CurrentFrame is not { } f) return null;
        string path = f.Path;
        if (_negativeWb.TryGetValue(path, out double[]? cached)) return cached;

        double[]? wb = RawDecode.IsRawExtension(path) ? RawDecode.CameraWhiteBalance(path) : null;
        _negativeWb[path] = wb;
        return wb;
    }

    /// <summary>
    /// True while the preview is showing the positive WITHOUT Stage-2 edits (before/after compare).
    ///
    /// Unlike the negative view this one has no patch: it strips Stage 2 out of the middle of a
    /// chain <see cref="RegionRender"/> applies as a whole, and the compare is a momentary hold
    /// rather than something to pixel-peep. The preview stands in, softer but truthful.
    /// </summary>
    private bool _showingBeforeEdits;
    /// <summary>
    /// 片基告警。**只在测不到裸露片基时非空**——成功时不回显 t_base 数值，那个数已经没有
    /// 滑块，它的意义由 D_min 承担。空字符串时整行在界面上隐藏，这样黑端分组的形状与亮端
    /// 一致（标量 → 采样按钮 → 逐通道）。
    /// </summary>
    [ObservableProperty] private string _filmBaseText = "";

    /// <summary>片基是否已经采过样。语言切换时用来决定是否重译告警文案。</summary>
    private bool _filmBaseSampled;

    // ── Tone curves (gamma-2.2 domain; set by the CurveEditor via SetCurves) ─────
    private List<(double X, double Y)> _curveM = new(), _curveR = new(), _curveG = new(), _curveB = new();
    private bool _curvePreserveHue = true;

    /// <summary>
    /// The live curves carry their own endpoints (see <see cref="FrameParams.CurveHasEndpoints"/>).
    ///
    /// True for anything the editor has touched — it materialises both ends on first click — and
    /// false for a curve loaded from a project written before endpoints were draggable, which has
    /// interior points only and must keep ramping into the corners.
    /// </summary>
    private bool _curveHasEndpoints;

    /// <summary>Push the four channel curves + hue-preserve flag from the editor and re-render.</summary>
    public void SetCurves(IReadOnlyList<(double X, double Y)> m, IReadOnlyList<(double X, double Y)> r,
                          IReadOnlyList<(double X, double Y)> g, IReadOnlyList<(double X, double Y)> b,
                          bool preserveHue)
    {
        // Anything arriving from the editor has been through EnsureEndpoints, so its ends are the
        // user's own black and white point from here on.
        _curveHasEndpoints = true;
        _curveM = new List<(double, double)>(m);
        _curveR = new List<(double, double)>(r);
        _curveG = new List<(double, double)>(g);
        _curveB = new List<(double, double)>(b);
        _curvePreserveHue = preserveHue;
        ScheduleRender();
    }

    private double[] TBaseArr() => new[] { TBaseR, TBaseG, TBaseB };

    /// <summary>Snapshot the current state into a FrameParams for a render/export.</summary>
    private FrameParams BuildParams() => new()
    {
        // Always BASIC. The intent stopped being a roll-level mode when the output space became
        // one: "linear" is a property of a particular EXPORT (hand this file to a colourist),
        // not of how the roll is being worked on. The preview is therefore always the full
        // render, which is what makes the output-space picker mean what it says.
        OutputIntent = OutputIntent.Basic,
        // Step-4 target: the space Stage 2 runs in and the file is written in.
        OutputSpace = OutputSpaces[_outputSpaceIndex].Name,
        // The print-film emulation that runs INSIDE step 4. Like the output space it belongs to
        // this snapshot rather than being read off the frame: this is the state the picker is
        // showing, and the preview, the thumbnails and an export all have to render the same
        // thing. Omitting it left the roll's frames carrying the LUT while every render built its
        // parameters from here — so the preview stayed pass-through and moving to another frame
        // wrote the pass-through value back over the roll.
        PrintLut = _printLutIndex > 0 ? _printLutPaths[_printLutIndex] : "",
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
        // Stage 1 — 反相的全部自由度：片基 + 两端各三个绝对密度
        TBase = TBaseArr(),
        DMinPerChannel = DMinPerChannel,
        DMaxPerChannel = DMaxPerChannel,
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
        CurveHasEndpoints = _curveHasEndpoints,
        // Geometry
        Rotation = Rotation,
        QuarterTurns = _quarterTurns,
        FlipH = _flipH,
        FlipV = _flipV,
        // Suppressed while the crop frame is being positioned — see CropEditing.
        CropRect = _cropEditing ? null : _cropRect,
        // Not suppressed with the crop above: the cell is where this frame's negative sits in the
        // strip, which does not stop being true while the crop tool is open.
        SplitCell = _splitCell,
    };

    // ── Sampling view: show the NEGATIVE while picking the film base ─────────────
    public void ShowNegativeView()
    {
        if (_previewLinear is null) return;
        // The patch on screen holds POSITIVE pixels, so it goes; the flag makes the NEXT one
        // render as a negative instead. Dropping it without the flag is not enough, because
        // zooming in here asks for another one immediately.
        _showingNegative = true;
        ClearSharpPatch();
        _savedPositive = PreviewImage;
        RefreshNegativeView();
    }

    /// <summary>
    /// (Re)draw the negative into <see cref="PreviewImage"/>.
    ///
    /// Split out from <see cref="ShowNegativeView"/> because the view is not static: turning the
    /// frame while it is up has to move the negative with it, and that arrives through
    /// <see cref="ScheduleRender"/> rather than through arming the tool again. Only the drawing is
    /// shared — the flag, the saved positive and the patch are entry-time concerns.
    /// </summary>
    private void RefreshNegativeView()
    {
        if (_previewLinear is not { } neg) return;
        // The buffer is scene-linear ACEScg (pre-inversion). Step 4 takes it to the roll's output
        // space, which is the space BitmapConvert is expecting — applying a bare sRGB gamma here
        // would encode the right curve onto the wrong primaries.
        var disp = new ImageBuffer(neg.Width, neg.Height, (float[])neg.Data.Clone());
        // ORIENTED to match the positive that was just on screen. Everything else in the pipeline
        // is deliberately skipped here (that is the point of the view), but orientation is not a
        // photometric step — it is which way up the picture is, and the user has already answered
        // that. Leaving it off meant turning a sideways scan upright and then having the negative
        // flop back onto its side the moment the film-base tool was armed.
        disp = OrientForNegative(disp);
        // The camera's own white balance, applied for VIEWING ONLY. The buffer underneath stays
        // UniWB — every Stage-1 sampler reads _previewLinear, not this copy — but a UniWB negative
        // shown raw reads GREEN, because a Bayer sensor's green channel has about twice the
        // response of red and blue. That is exactly the wrong thing under a tool that asks the
        // user to point at "the brightest ORANGE film base": the base does not look orange, and
        // the highlight sampler's "darkest part of the negative" is judged through a cast too.
        // Null for a scanner TIFF or a camera with no as-shot record, in which case this is a
        // no-op and the view is what it always was.
        NegativeView.ApplyWhiteBalance(disp.Data, CurrentNegativeWb());
        // Plain step 4, never the roll's print-film emulation: this buffer is a NEGATIVE. A print
        // stock characterises how a finished positive prints, so feeding it un-inverted film would
        // render a look nobody asked for over an image the user is only here to sample.
        ColorPipeline.ToOutputSpace(disp.Data, CurrentOutputSpace);
        PreviewImage = BitmapConvert.ToBitmap(disp);
    }

    /// <summary>
    /// The orientation half of the geometry chain, applied to a negative-view buffer.
    ///
    /// Quarter turns and flips only — NOT straighten, and NOT crop. The straighten angle would
    /// bring in fill corners and a crop would hide the very film base being sampled (it lives in
    /// the frame's margins), and neither is needed to answer "which way up is this". Keeping the
    /// buffer's content complete is also what lets <see cref="UnorientNegativeSampleRect"/> be a pure
    /// coordinate map: the pixels are permuted, never resampled or dropped.
    /// </summary>
    private ImageBuffer OrientForNegative(ImageBuffer img)
        => _quarterTurns % 4 == 0 && !_flipH && !_flipV
               ? img
               : Geometry.ApplyOrientation(img, _quarterTurns, _flipH, _flipV);

    /// <summary>
    /// A rect drawn on the ORIENTED negative view, mapped back into the raw preview buffer's own
    /// axes — which is where every Stage-1 sampler reads.
    ///
    /// <see cref="ShowNegativeView"/> turns the pixels on screen; the samplers do not turn with
    /// them, because <see cref="Stage1Source"/> works on <see cref="_previewLinear"/> as decoded.
    /// So the selection has to come back the other way, or picking the orange base in the corner
    /// of an upright scan would average a rectangle from the opposite corner of the strip.
    ///
    /// CALLED FROM THE VIEW, at pointer-release, and deliberately NOT from inside each sampler:
    /// the release handler runs <c>ExitMode</c> — which restores the positive view and clears
    /// <see cref="_showingNegative"/> — BEFORE dispatching to the sampler, so a flag check made
    /// inside the sampler always sees false and skips the turn. The question "was this drawn on
    /// the negative?" can only be asked while that view is still up.
    ///
    /// A no-op in the positive view: those rects are normalised against the displayed frame the
    /// pipeline itself produced, so they need no correction.
    /// </summary>
    public (double X, double Y, double W, double H) UnorientNegativeSampleRect(
        (double X, double Y, double W, double H) rect)
    {
        if (!_showingNegative) return rect;
        if (_flipV) rect = FlipCropV(rect);
        if (_flipH) rect = FlipCropH(rect);
        for (int i = 0; i < (((_quarterTurns % 4) + 4) % 4); i++) rect = RotateCropCcw(rect);
        return rect;
    }

    public void ShowPositiveView()
    {
        _showingNegative = false;
        // The patch up now is a NEGATIVE one — it belongs to the view being left.
        ClearSharpPatch();
        if (_savedPositive is not null) { PreviewImage = _savedPositive; _savedPositive = null; }
        ScheduleRender();
    }

    // ── Before/after compare: show the positive WITHOUT Stage-2 (scene) edits ─────
    public void ShowBeforeEdits()
    {
        if (_previewLinear is null) return;
        _showingBeforeEdits = true;
        ClearSharpPatch();   // patch was rendered WITH the Stage-2 edits this view strips
        FrameParams p = BuildParams();
        RollFrame.ResetScene(p);   // strip every Stage-2 adjustment
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, ForPreview(p));
        PreviewImage = BitmapConvert.ToBitmap(pos);
    }

    public void ShowAfterEdits()
    {
        _showingBeforeEdits = false;
        ScheduleRender();   // re-render the fully edited positive
    }

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

    /// <summary>
    /// 片基采样：量出裸片基的**绝对密度**，写进黑端。
    ///
    /// 密度对 T=1 而言（TBase 恒为 1,1,1），所以量到的就是 −log10(片基透射率)。C-41 的橙色
    /// 片基必然 R&lt;G&lt;B，典型 ~0.09/0.29/0.54——这三个数是可验证的物理量。
    ///
    /// 曾经这里写的是 t_base（除数），黑端则恒为 0,0,0。那样片基信息藏在一个没有滑块的字段
    /// 里，界面上看不到黑端的任何客观数值，用户无从判断自动标定对不对。两种写法渲染逐位相同
    /// （把片基从除数移到减数是同一个仿射变换），所以改成显示绝对值没有代价。
    /// </summary>
    public void SampleFilmBase((double X, double Y, double W, double H) rect) => TrySample(Loc.T("片基采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        double[] tb = FilmBase.SampleTBase(src, rect);
        _filmBaseSampled = true;
        // 亮端不动：它已经是对 T=1 的绝对密度，与这次采样无关。只有黑端被重新定义。
        DMinPerChannel = TBaseToDensity(tb);
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
        // Compared on TOTAL transmission against a LOW threshold — both halves matter.
        //
        // Per channel was wrong because the base is orange: a real C-41 base at UniWB reads about
        // (0.21, 0.18, 0.06), blue at 30% of red, while the frame's p99.9 comes from bare light
        // panel and sprocket holes that carry no mask at all and are green-dominant on top of it.
        // Testing channel-by-channel asks the mask's most-absorbed channel to rival an unfiltered
        // one, which no correctly sampled base can do.
        //
        // 0.4 was wrong because the mask is DENSE (~0.5–0.8 D). Such a base transmits well under
        // half of bare-panel light by construction, so demanding 40% demanded that the mask barely
        // absorb. What actually separates "found the base" from "missed it" is that picture
        // content is denser still: measured ratios run 0.19–0.61 for real bases against 0.05–0.11
        // for a rect that landed on the picture or a shadow. 0.08 sits below the former and under
        // the latter with room to spare.
        double[] br = ImageIo.BrightReference(src);
        double tbSum = tb[0] + tb[1] + tb[2];
        double brSum = br[0] + br[1] + br[2];
        if (tbSum < brSum * 0.08)
        {
            FilmBaseText = Loc.T("⚠ 采样区偏暗，可能不是片基——请在负片视图中对准最亮的橙色片基重采");
            StatusText = FilmBaseText;
        }
        else
        {
            FilmBaseText = "";
            StatusText = Loc.F($"片基采样 → 黑端 {DMinR:F3} / {DMinG:F3} / {DMinB:F3}");
        }
    });

    /// <summary>
    /// 透射率 → 对 T=1 的绝对密度。片基采样与自动片基共用，保证两条路写出同一个量。
    /// </summary>
    private static double[] TBaseToDensity(double[] t)
    {
        var d = new double[3];
        for (int c = 0; c < 3; c++) d[c] = -Math.Log10(Math.Max(t[c], 1e-10));
        return d;
    }

    /// <summary>
    /// 高光采样：框负片上最浓的区域（= 正片高光），测出亮端三个密度。
    ///
    /// 这里曾有两个按钮——「框选亮部」解白平衡、「框选 D_max」定端点——它们测的是同一个量，
    /// 只是一个把结果normalise成比例、一个保留绝对值。高光白平衡与高光端点本就是一件事，
    /// 所以合并成一个。
    /// </summary>
    public void SampleDMax((double X, double Y, double W, double H) rect) => TrySample(Loc.T("高光采样"), () =>
    {
        if (Stage1Source(_previewLinear) is not { } src) return;
        double[] hi = FilmBase.SampleDMaxPerChannelFromRect(src, rect, TBaseArr());
        DMaxPerChannel = hi;
        StatusText = Loc.F($"高光采样 → 亮端 {DMaxLevel:F3}（逐通道 {hi[0]:F3} / {hi[1]:F3} / {hi[2]:F3}）");
    });

    /// <summary>
    /// Measure the sprocket/light-board threshold from the imported frame, apply it to the whole
    /// roll, then run the auto chain. The import-time entry point.
    ///
    /// The threshold has to be settled BEFORE the chain runs, not after: it is the light-board cut
    /// that keeps the board out of both the film-base estimate and the highlight pick, and
    /// re-running the chain later would be the only way to fold in a threshold that arrived
    /// afterwards. That ordering is why this method — rather than the end of LoadRollAsync — is
    /// where import-time auto-inversion belongs.
    ///
    /// This used to be a modal dialog the user had to clear before the roll would open. It is now
    /// measured and applied silently: the dialog's own default was
    /// <see cref="Sprocket.EstimateSprocketThreshold"/>'s answer, which is what runs here, and the
    /// threshold stays adjustable in 整卷校准 → 齿孔遮罩 with the same live mask overlay.
    ///
    /// <see cref="Sprocket.NoBoard"/> is a real answer rather than a failure — a flatbed scan has
    /// no light board, and forcing a cut onto one would mask off the film's own highlights. It maps
    /// to SprocketEnabled = false, exactly what the dialog's 跳过 did.
    ///
    /// Note this decides only the MASK-FILL toggle (齿孔遮罩, which paints the board white in the
    /// output). The automatic measurements exclude the board either way — they re-derive the cut
    /// themselves through <see cref="FilmBase.HighDensityKeepMask"/> — so a roll that lands here
    /// with no board still gets its statistics taken over film pixels only.
    /// </summary>
    public void ApplySprocketAuto()
    {
        double? threshold = null;
        if (_previewLinear is { } preview)
        {
            double est = Sprocket.EstimateSprocketThreshold(preview);
            if (est < Sprocket.NoBoard) threshold = est;
        }

        if (threshold is double thr)
        {
            SprocketEnabled = true; SprocketThreshold = thr;
            foreach (RollFrame f in Frames) { f.Params.SprocketEnabled = true; f.Params.SprocketThreshold = thr; }
        }
        else
        {
            SprocketEnabled = false;
            foreach (RollFrame f in Frames) f.Params.SprocketEnabled = false;
        }
        AutoInvertOnImportRun();
        UpdateSprocketOverlay();
    }

    /// <summary>
    /// The import-time run of <see cref="AutoInvertRollAsync"/>, gated on the import dialog's
    /// checkbox (<see cref="ImportConfig.AutoInvert"/>).
    ///
    /// Unchecked means NOTHING is measured — not even the film base. The roll opens on pipeline
    /// defaults and every value is the user's to set. An earlier version still auto-detected the
    /// base here on the theory that a roll with no base at all is useless, but that makes the
    /// checkbox lie: someone who unticks "自动整卷分析去色罩" is saying they intend to calibrate
    /// this roll by hand, and silently seeding t_base both overwrites the starting point they
    /// wanted and hides that anything happened.
    /// </summary>
    private void AutoInvertOnImportRun()
    {
        if (_cfgAutoInvert) _ = AutoInvertRollAsync();
    }

    /// <summary>
    /// This import's auto-inversion choice, taken from the import dialog's checkbox.
    ///
    /// Held as a field because the decision is made in <see cref="LoadRollWithConfigAsync"/> but
    /// acted on later, in <see cref="ApplySprocketAuto"/> — the chain has to wait for the
    /// sprocket threshold. Defaults true so a roll opened by any other route (a saved project, the
    /// catalog) still behaves as before.
    /// </summary>
    private bool _cfgAutoInvert = true;

    /// <summary>Estimate T_base excluding the light-board (given the sprocket threshold) → all frames.</summary>
    /// <param name="useMode">
    /// True → measure the base as the brightest dense luma MODE
    /// (<see cref="FilmBase.EstimateTBaseByMode"/>), which is what the auto chain wants: on a
    /// copy-stand negative the board's transition shoulder survives the board cut and owns every
    /// bright tail, so a percentile lands on the shoulder rather than on the base. Falls back to
    /// the roll estimator when no mode clears the density floor.
    /// </param>
    /// <returns>True if a base was estimated; false if the estimator rejected the frame.</returns>
    private bool AutoFilmBaseFromRoll(double? sprocketThreshold, bool useMode = false,
                                      bool broadcastToRoll = true)
    {
        if (_previewLinear is null) return false;
        bool ok = false;
        try
        {
            // Path A: t_base must live in the DECOUPLED domain (the pipeline decouples BEFORE
            // dividing by t_base). Sample values from the decoupled negative; masks stay on the raw
            // (its luma is where the sprocket threshold was calibrated). Mirrors Python's valueImages.
            ImageBuffer? dec = Stage1Source(_previewLinear);
            ImageBuffer? values = ReferenceEquals(dec, _previewLinear) ? null : dec;
            double[]? tb = useMode
                ? FilmBase.EstimateTBaseByMode(_previewLinear, sprocketThreshold, values)
                : null;
            // Then the edge sliver: a scan with no board still often keeps a thin strip of bare
            // rebate, which is the real base but far too small for any percentile to find. Tried
            // before the tail estimator because when it answers at all it has identified an
            // actual piece of film base, whereas the tail is a fallback that measures whatever
            // happens to be brightest.
            tb ??= FilmBase.EstimateTBaseFromEdgeSliver(_previewLinear, values);
            tb ??= values is null
                ? FilmBase.EstimateTBaseFromRoll(new[] { _previewLinear }, sprocketThreshold)
                : FilmBase.EstimateTBaseFromRoll(new[] { _previewLinear }, sprocketThreshold,
                                                 valueImages: new[] { values });
            // 片基的绝对密度进黑端；TBase 保持中性 1,1,1（参考点是完全透光）。
            DMinPerChannel = TBaseToDensity(tb);
            // Only broadcast to the whole roll when invoked from the roll-wide chain. Per-frame
            // buttons (自动黑点, 自动单张) pass broadcastToRoll: false so the other frames keep
            // whatever they already hold — overwriting their d_min is the bug those callers fix.
            if (broadcastToRoll)
            {
                double[] dmin = DMinPerChannel;
                foreach (RollFrame f in Frames) f.Params.DMinPerChannel = (double[])dmin.Clone();
            }
            _filmBaseSampled = true;
            ok = true;

            // Sanity-check the result against what a C-41 base physically IS: an orange dye layer,
            // so R > G > B, and by a clear margin. A neutral answer means the estimator found no
            // bare base and fell back to the picture's own highlights — which happens on a scan
            // already cropped to the image area, where no base is in frame at all. The number it
            // returns is then not a film base and everything downstream inherits that, so it has
            // to be said out loud rather than shown as a normal measurement.
            if (IsPlausibleFilmBase(tb))
            {
                FilmBaseText = "";
                StatusText = Loc.T("已自动检测片基") + (sprocketThreshold is null ? Loc.T("（无齿孔模式）") : Loc.T("与齿孔阈值"));
            }
            else
            {
                FilmBaseText = Loc.T("⚠ 未测到裸露片基——自动结果只是画面最亮处，请手动【片基采样】");
                StatusText = Loc.T("⚠ 未测到橙色片基：这一卷可能已裁掉片基区域，自动结果仅供参考——请用【片基采样】手动标定");
            }
        }
        catch (Exception ex) { StatusText = Loc.T("自动片基检测失败：") + ex.Message; }
        // When broadcasting, every frame just took new d_min so all thumbnails are stale — drop
        // them before restarting. Otherwise DecodeThumbnailsAsync skips frames that still show
        // stale thumbnails. For per-frame calls only the current frame's thumbnail needs refresh;
        // the caller handles that.
        if (ok && broadcastToRoll) foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
        return ok;
    }

    /// <summary>
    /// Whether a measured t_base looks like a real C-41 film base rather than a fallback.
    ///
    /// The mask is an orange dye layer, so its transmittance is ordered R > G > B and the R-to-B
    /// ratio is large — the hand-sampled references in this project sit around 0.20 / 0.175 /
    /// 0.06, a ratio above 3. A base measured off picture highlights instead comes back nearly
    /// neutral (0.716 / 0.729 / 0.725 on the 归档 samples, ratio 0.99, and with G above R, which
    /// no C-41 base can be). A modest ratio floor separates the two cleanly without rejecting a
    /// thin or faded base.
    /// </summary>
    private static bool IsPlausibleFilmBase(double[] tb)
        => tb.Length == 3 && tb[0] > tb[1] && tb[1] > tb[2]
           && tb[0] / Math.Max(tb[2], 1e-6) >= FilmBaseMinRatio;


    /// <summary>Least R:B ratio for <see cref="IsPlausibleFilmBase"/>. Set well below a real
    /// base's ≈3 and well above the ≈1 a neutral fallback returns.</summary>
    private const double FilmBaseMinRatio = 1.35;

    /// <summary>
    /// The light-board cut the auto chain should use, measured from the frame rather than taken
    /// from the sprocket dialog.
    ///
    /// The dialog cannot be trusted as the only source here. 跳过 leaves SprocketEnabled false
    /// with no threshold, and the chain would then estimate the base in pure-brightness mode with
    /// the board fully included — the board IS the brightest thing in frame, so it becomes the
    /// "base", and t_base comes back near-clipped and neutral instead of orange. On a measured
    /// sample that failure returned (0.568, 0.987, 0.591) — G highest of the three, which no
    /// C-41 base can be — against a hand-sampled (0.200, 0.175, 0.060).
    ///
    /// A user-set threshold still wins: an enabled 齿孔遮罩 means the cut was looked at on the real
    /// frame, and the estimator is a heuristic. Only when there is no user value does this measure
    /// one — which since the import dialog was removed is also the import-time path, and
    /// <see cref="Sprocket.EstimateSprocketThreshold"/> reports <see cref="Sprocket.NoBoard"/>
    /// on a frame that genuinely has no board, which maps back to null (pure-brightness mode).
    /// </summary>
    private double? AutoBoardCut()
    {
        if (SprocketEnabled) return SprocketThreshold;
        if (AutoRegion() is not { } raw) return null;
        double thr = Sprocket.EstimateSprocketThreshold(raw);
        return thr >= Sprocket.NoBoard ? null : thr;
    }

    /// <summary>
    /// Auto inversion over the WHOLE ROLL: decode every frame, measure each one, reduce the
    /// measurements to ONE set of parameters, apply that set to every frame. This is the NexFilm
    /// import flow (its 自动反相 runs <c>compute_auto_base</c> → crosstalk → per-channel
    /// <c>compute_auto_color_limits</c>) expressed in this pipeline's terms.
    ///
    /// Roll-wide and not per-frame, deliberately. A roll is one strip of one film developed in one
    /// batch, and the four parameters here describe THAT, not any individual scene — so the frames
    /// are repeated measurements of a shared quantity, and pooling them is what makes the estimate
    /// better than any single frame's. It also means the roll stays visually of a piece, which a
    /// per-frame solve cannot promise: it would silently colour-correct away a sunset or a tungsten
    /// interior, because to a single-frame estimator those are indistinguishable from a cast.
    /// Per-frame differences remain the user's to make afterwards, on top of a consistent base.
    ///
    /// How each parameter is pooled differs, because their semantics differ — see
    /// <see cref="FilmBase.EstimateTBaseByModeFromRoll"/> (median: one physical material),
    /// <see cref="FilmBase.AutoWbHighFromRoll"/> (densest frame: the roll's true brightest
    /// highlight) and <see cref="FilmBase.DetectDMaxFromRoll"/> (upper percentile: only
    /// well-exposed frames reach the film's ceiling).
    ///
    /// Runs on import, from <see cref="AutoInvertOnImportRun"/>, and on demand from the 自动（整卷）
    /// button via <see cref="AutoInvertRollCommandAsync"/>. Every step it performs is also its own
    /// button in the 整卷校准 panel, so the chain button IS a second way to do the same thing —
    /// that redundancy is the point: the individual buttons document the physics, while the chain
    /// is the path for someone who just wants the roll inverted. Note that re-running it over a
    /// half-graded roll discards the user's wb_high and levels, which is why it is only ever
    /// reached by an explicit press.
    ///
    /// The ORDER is the part that is not obvious, and it is wrong in both other directions:
    ///
    ///  1. t_base first — everything downstream is a density measured as −log10(T / t_base), so
    ///     a later step run against a stale base measures the wrong quantity entirely.
    ///  2. wb_high second, against the pooled base. wb_offset is deliberately NOT auto-solved:
    ///     the class remarks on <see cref="FilmBase"/> require the additive shadow term to be
    ///     sampled BEFORE the multiplicative highlight term, and there is no unsupervised way to
    ///     find a neutral shadow — a dark scene object is not a grey card. Leaving it at zero
    ///     makes wb_high's solve reduce to the clean wb_high[c] = max_d / D[c], which is exactly
    ///     what NexFilm does (its exposure_offset is identically zero).
    ///  3. D-max third: it is a density percentile of T / t_base, so it needs the base, and it
    ///     sets the white end the levels then measure against.
    ///  4. Levels last, on the rendered positive — it is the only step that measures OUTPUT, so
    ///     it must see the other three already applied.
    ///
    /// Diverges from NexFilm on one point on purpose: it does NOT stretch the three channels to
    /// independent endpoints. Per-channel stretching is most of why NexFilm's result looks neutral
    /// out of the box, but it also flattens the scene's own cast. Steps 1–2 here already
    /// neutralise the mask and the highlight, and 黑场/白场 stay achromatic, so a cast survives.
    ///
    /// The current frame is measured and applied FIRST, before the background pass over the rest:
    /// the user gets a usable picture immediately, and the roll-wide refinement lands after. The
    /// two-stage shape is why this is async and why the status line reports twice.
    /// </summary>
    private async Task AutoInvertRollAsync()
    {
        if (_previewLinear is null) return;

        // ── Stage 1: the current frame alone, so there is something to look at at once ──────
        //
        // This must run the WHOLE chain, not just the base. Estimating t_base and stopping leaves
        // wb_high at 1,1,1 and the levels untouched, so the preview is a mask-removed but
        // ungraded picture — which reads as "去色罩没做完", because it is not done. The remaining
        // three steps are cheap here: they measure the already-decoded current frame.
        //
        // Stage 1's answer is broadcast to the whole roll and is provisional by construction: it
        // is ONE frame's measurement standing in for the roll until stage 2 pools every frame. On
        // a roll whose opening frame is unrepresentative — roll 21 starts on P8060012, a boundary
        // frame with no highlight in its kept area — the whole strip is visibly off until stage 2
        // lands. That is why <see cref="RollAnalysisPending"/> exists: the provisional state is
        // announced rather than left to look like the finished result.
        double? cut = AutoBoardCut();
        if (!AutoFilmBaseFromRoll(cut, useMode: true))
            return;   // AutoFilmBaseFromRoll already reported why; a base-less chain is meaningless

        _suppressRender = true;
        try
        {
            // Neutral start: both level sliders are OFFSETS from the untouched endpoints, so
            // neutral is 0 for each. The highlight endpoint needs no such reset — AutoDetectDMax
            // overwrites all three channels of it outright.
            // LEVELS ARE LEFT NEUTRAL — the display rendering already places both ends.
            // CineonToDisplay normalises the film base at code 95 to display black and rolls the
            // latitude above 685 off toward white, so the picture arrives with its endpoints
            // already set. Measuring percentiles off that render and stretching them to 0..1
            // re-does the black end (a no-op now — the base reads 0.000, so the black slider
            // always solved to 0) and OVERRIDES the white end, pushing the highlights the shoulder
            // just rolled off back up against the clip. Same objection as under a print-film cube,
            // whose toe and shoulder are its look: a rendering that has placed its own ends should
            // not then be renormalised. The 自动色阶 button stays available for a scan that really
            // does need it.
            Black = 0.0; White = 0.0;
            AutoDetectDMax();
        }
        finally { _suppressRender = false; }

        ApplyAutoChainToRoll();
        ScheduleRender();
        // Thumbnails are already stale at this point — every frame just took the current frame's
        // parameters — so drop them now rather than only at the end of stage 2. Otherwise the
        // strip shows raw negatives for the whole length of the roll analysis.
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
        StatusText = Loc.T("去色罩（当前帧）完成，正在分析整卷 …");

        // ── Stage 2: pool the whole roll ───────────────────────────────────────────────────
        List<RollFrame> frames = Frames.ToList();
        if (frames.Count <= 1) { FinishAutoInvert(); return; }

        // Everything on screen is now one frame's answer standing in for the roll. Say so, and
        // let the notice be dismissed — a user who already knows should not have to keep reading
        // it. Raised here rather than at the top of the method because a single-frame roll (the
        // early return above) never has a provisional stage to warn about.
        RollAnalysisNoticeDismissed = false;
        RollAnalysisPending = true;

        var cts = new CancellationTokenSource();
        _autoInvertCts?.Cancel();
        _autoInvertCts = cts;
        CancellationToken ct = cts.Token;

        try
        {
            // Walk outward from the current frame, and dedupe by preview key — the SAME order and
            // the same work unit WarmRollAsync uses. Both matter for speed: this pass shares the
            // warm-up's decodes through PreviewAsync's cache and in-flight table (no frame is ever
            // decoded twice), but only if the two ask for frames in the same order. Walking the
            // roll in index order while the warm-up walks outward means constantly asking for the
            // one frame it has not reached yet, which serialises this behind it.
            int start = Math.Max(0, CurrentFrame is { } cur ? frames.IndexOf(cur) : 0);
            var order = new List<(string Path, (double X, double Y, double W, double H)? Pre, RollFrame Frame)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < frames.Count; i++)
            {
                RollFrame f = frames[(start + i) % frames.Count];
                var pre = SplitCropOf(f);
                if (seen.Add(PreviewKey(f.Path, pre))) order.Add((f.Path, pre, f));
            }

            var masks = new List<ImageBuffer>();
            var values = new List<ImageBuffer>();
            // Uncropped counterparts of `values`, for the film-base estimators only — see the
            // note where they are filled.
            var baseSources = new List<ImageBuffer>();
            var gate = new object();
            int done = 0;
            ReportBackground(Loc.F($"整卷分析 0/{order.Count} …"));
            await Parallel.ForEachAsync(order, new ParallelOptions
            {
                CancellationToken = ct,
                // Same ceiling as the warm-up, and for the same reason: each in-flight decode
                // holds a few hundred MB and the UI still needs a core.
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 3, 1, 3),
            }, async (item, token) =>
            {
                var (path, pre, frame) = item;
                ImageBuffer raw;
                try { raw = (await PreviewAsync(path, pre).WaitAsync(token)).Preview; }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Undecodable frame → it simply does not vote.
                    ReportBackground(Loc.F($"整卷分析 {Interlocked.Increment(ref done)}/{order.Count} …"));
                    return;
                }

                // Restrict to the KEPT PICTURE before measuring anything — the same rule
                // AutoRegion states for the single-frame path. What PreviewAsync returns is the
                // whole file (single-frame scans get no region decode, so `pre` is null), and a
                // scan's black surround and film edges are denser than any real tone: leaving
                // them in hands D_max the border instead of the scene's highlight, and drags the
                // roll's D-max reduction with it. On a region decode the buffer is already the
                // margin box, so the frame's own rect is relative to that box, not to the file.
                // Un-oriented throughout: this is the raw sampling domain, before the geometry
                // stage, which is the same domain AutoRegion works in.
                var stored = UnorientRect(frame.Params.CropRect, frame.Params);
                var crop = pre is { } box && stored is { } inner ? Relative(inner, box) : stored;
                if (crop is { } c && (c.W >= 0.999 && c.H >= 0.999)) crop = null;

                // Same two-buffer contract the single-frame path uses: masks key off the RAW
                // luma (where the board cut is calibrated), values come from the Stage-1
                // (decoupled) domain the inversion actually divides.
                //
                // Stage 1 runs on the FULL buffer and the crop is applied AFTER, exactly as
                // AutoRegionStage1 does: vignette correction is radial about the frame centre, so
                // correcting an already-cropped buffer would centre the falloff on the wrong point.
                ImageBuffer? dec = Stage1Source(raw);
                ImageBuffer val = ReferenceEquals(dec, raw) || dec is null ? raw : dec;

                // The UNCROPPED buffer is kept alongside the cropped one, because t_base is the
                // one measurement that must NOT see the crop.
                //
                // Every other statistic here wants the kept picture: D-max and the highlight pick
                // are about the scene, so the border must go. The film base is the opposite — it
                // is bare rebate at the frame's edge, which is exactly what a crop removes. Stage
                // 1 measures the whole frame and finds it; stage 2 was cropping first, so the
                // sliver estimator had nothing left to find and fell through to the bright-tail
                // fallback, which reports picture highlights instead. That is why a roll opened
                // with a correct base drifted the moment the roll-wide pass finished, and why
                // re-running it on one frame could not recover: measured on 图像 001a, a 2% crop
                // cut the sliver's vote count from 7/8 frames to 3/8 and a 5% crop to 0/8, at
                // which point t_base jumped from (0.397, 0.272, 0.155) to the tail's answer.
                ImageBuffer baseSrc = val;

                if (crop is { } cc)
                {
                    bool shared = ReferenceEquals(val, raw);
                    raw = Geometry.ApplyCrop(raw, cc);
                    val = shared ? raw : Geometry.ApplyCrop(val, cc);
                }
                lock (gate) { masks.Add(raw); values.Add(val); baseSources.Add(baseSrc); }
                ReportBackground(Loc.F($"整卷分析 {Interlocked.Increment(ref done)}/{order.Count} …"));
            });
            ReportBackground("");
            ct.ThrowIfCancellationRequested();
            if (masks.Count == 0) { FinishAutoInvert(); return; }

            bool sameDomain = masks.Count == values.Count
                              && !masks.Where((m, i) => !ReferenceEquals(m, values[i])).Any();
            IReadOnlyList<ImageBuffer>? valueList = sameDomain ? null : values;

            // t_base is measured on the UNCROPPED frames — the base lives in the margin a crop
            // removes, so cropping first is what made this diverge from stage 1. Everything after
            // it (wb_high, D-max, the endpoints) still uses the cropped buffers, because those
            // are measurements of the SCENE and the border would corrupt them.
            IReadOnlyList<ImageBuffer> baseList =
                baseSources.Count == masks.Count ? baseSources : masks;

            double[]? rollBase = await Task.Run(
                () => FilmBase.EstimateTBaseByModeFromRoll(baseList, cut), ct);
            // Same order as the single-frame chain: an identified sliver of real base beats a
            // bright-tail percentile, which only measures whatever happens to be brightest.
            rollBase ??= await Task.Run(
                () => FilmBase.EstimateTBaseFromEdgeSliverFromRoll(baseList), ct);
            rollBase ??= await Task.Run(
                () => FilmBase.EstimateTBaseFromRoll(baseList, cut), ct);

            // 估计器一律传中性参考，让它们直接产出**对 T=1 的绝对密度**——与 D_min 同一基准。
            //
            // 传 rollBase 会得到「相对片基」的密度，而黑端现在是绝对密度，两端基准不一致：
            // 跨度 = D_max − D_min 会各通道少算一个 D_min，而三通道少得不一样多
            // （实测 R 少 0.086、B 少 0.538）=> 反差变小且严重偏色。
            double[] neutralRef = { 1.0, 1.0, 1.0 };

            double[]? rollWbHigh = null;
            try
            {
                rollWbHigh = await Task.Run(
                    () => FilmBase.AutoWbHighFromRoll(masks, neutralRef, cut, valueList), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* no usable highlight across the roll — keep the current-frame solve */ }

            // Roll-wide highlight endpoints. Masked with the same two cuts the t_base estimator
            // uses: an opaque film-edge line would inflate the channels unequally and show up as
            // a colour cast.
            double[]? rollDMaxPerCh = await Task.Run(
                () => FilmBase.DetectDMaxPerChannelFromRoll(values, neutralRef, 90.0, masks, cut), ct);

            ct.ThrowIfCancellationRequested();

            _suppressRender = true;
            try
            {
                // 片基的绝对密度就是黑端本身。
                DMinPerChannel = TBaseToDensity(rollBase);

                // The highlight endpoint, from whichever measurement is available. Both estimators
                // return the same quantity — three absolute densities — so there is nothing to
                // reconcile and no second field left to contradict them. The per-channel D-max
                // detector is preferred: it pools an upper percentile across the roll, where
                // AutoWbHighFromRoll takes the single densest frame's highlight.
                double[]? rollHighlight = rollDMaxPerCh ?? rollWbHigh;
                if (rollHighlight is not null) DMaxPerChannel = rollHighlight;
                // The detector's endpoints ARE the placement — both the channels' relative spans
                // (the colour balance) and where the picture sits.
                // Levels stay neutral — the display rendering has already placed both ends; see
                // the note in AutoInvertRollAsync's stage 1.
                Black = 0.0; White = 0.0;
            }
            finally { _suppressRender = false; }

            // Same plausibility check the single-frame path applies — the roll-pooled base is no
            // more guaranteed to be a real film base than a single frame's, and reporting it as a
            // plain measurement here would silently overwrite the warning stage 1 just raised.
            FilmBaseText = IsPlausibleFilmBase(rollBase)
                ? ""
                : Loc.T("⚠ 未测到裸露片基——自动结果只是画面最亮处，请手动【片基采样】");
            ApplyAutoChainToRoll();
            NeedsRecalibration = false;   // 重跑过了，提示可以撤下
            FinishAutoInvert(masks.Count);
        }
        // Both failure paths clear the notice as well as the progress line. A cancelled or failed
        // analysis is exactly the case where "正在分析" must stop being displayed: the roll is
        // staying on stage 1's provisional numbers, and a notice promising a result that is no
        // longer coming would sit there for the rest of the session.
        catch (OperationCanceledException)
        {
            ReportBackground("");
            RollAnalysisPending = false;
        }
        catch (Exception ex)
        {
            ReportBackground("");
            RollAnalysisPending = false;
            StatusText = Loc.T("整卷分析去色罩失败：") + ex.Message;
        }
    }

    /// <summary>Cancels an in-flight <see cref="AutoInvertRollAsync"/> when a new roll loads.</summary>
    private CancellationTokenSource? _autoInvertCts;

    /// <summary>
    /// Push the four auto-chain parameters onto EVERY frame.
    ///
    /// Without this the chain would only ever reach the frame that happens to be selected: the
    /// sliders are committed to <c>CurrentFrame.Params</c> on frame switch, so frames 2..N would
    /// keep pipeline defaults and the roll would come out inconsistent. t_base was already being
    /// distributed this way by <see cref="AutoFilmBaseFromRoll"/>; the other three were not, which
    /// was a straightforward bug.
    ///
    /// Only the four values the chain sets are written. Anything else a frame carries — crop,
    /// rotation, per-frame Stage-2 grading — is left alone.
    /// </summary>
    private void ApplyAutoChainToRoll()
    {
        double[] tb = TBaseArr(), dmc = DMaxPerChannel, dmn = DMinPerChannel;
        double black = WbMath.BlackSliderToPoint(Black), white = WbMath.WhiteSliderToPoint(White);
        foreach (RollFrame f in Frames)
        {
            f.Params.TBase = (double[])tb.Clone();
            // Roll-uniform: the endpoints were measured across the roll, so a single flat-lit
            // frame is not normalised on its own.
            f.Params.DMaxPerChannel = (double[])dmc.Clone();
            f.Params.DMinPerChannel = (double[])dmn.Clone();
            f.Params.BlackPoint = black;
            f.Params.WhitePoint = white;
        }
        // The current frame's params are rebuilt from the sliders on switch anyway, but the loop
        // above has just overwritten them with the same values, so nothing is lost either way —
        // including when CommitLiveParams declines because a frame switch is still in flight, in
        // which case the loop's direct write is already the whole answer for that frame.
        CommitLiveParams(CurrentFrame);

        // The loop above mutated every OTHER frame's params directly, and nothing else notices
        // that: autosave is driven by the slider bindings, which only fire for the frame on
        // screen. Without this the broadcast lives in memory and dies with the session — the
        // saved .ncproj keeps whatever the other frames had before, so reopening the roll shows
        // it un-broadcast and the button looks like it did nothing.
        MarkRollDirty();
    }

    private void FinishAutoInvert(int voted = 1)
    {
        // The roll-wide numbers are in: what is on screen is no longer provisional.
        RollAnalysisPending = false;
        StatusText = Loc.F($"整卷去色罩完成（{voted} 帧参与）· 片基 {TBaseR:F3}, {TBaseG:F3}, {TBaseB:F3} · 亮端 {DMaxR:F3}, {DMaxG:F3}, {DMaxB:F3}");
        ScheduleRender();
        // Drop the existing thumbnails before asking for new ones. DecodeThumbnailsAsync skips
        // any frame that already HAS a thumbnail — it exists to fill gaps during import — so
        // RestartThumbnails on its own is a no-op here and the strip would keep showing the
        // pre-inversion render for the rest of the roll's life. Same invalidate-then-restart
        // pair OnSplitMarginChanged uses, and for the same reason.
        foreach (RollFrame f in Frames) SetThumbnail(f, null);
        RestartThumbnails();
    }

    /// <summary>
    /// The crop that isolates the kept picture inside <see cref="_previewLinear"/>.
    ///
    /// On a split frame the buffer is the margin box, so the frame sits at
    /// <see cref="_previewFrameRect"/> within it — the stored rect describes the whole scan and
    /// would select a sliver, skewing every auto-detection on a split scan. NOT oriented: the
    /// callers work in the raw sampling domain, before the geometry stage.
    /// </summary>
    private (double X, double Y, double W, double H)? AutoCrop
        => _previewMargin is not null ? _previewFrameRect : _cropRect;

    /// <summary>The RAW preview restricted to the current crop (else the whole frame) — auto-detections
    /// analyse only the kept picture so sprockets / film edges / borders don't skew D-max or WB.
    /// This is the MASK domain; for measured values use <see cref="AutoRegionStage1"/>.</summary>
    private ImageBuffer? AutoRegion()
        => _previewLinear is { } prev && AutoCrop is { } c ? Geometry.ApplyCrop(prev, c) : _previewLinear;

    /// <summary>The same region in the Stage-1 sampling domain (decoupled under Path A). The
    /// photometric chain runs on the FULL preview before cropping — vignette is radial about the
    /// frame centre, so correcting a crop in isolation would centre the falloff on the wrong point.</summary>
    private ImageBuffer? AutoRegionStage1()
    {
        if (Stage1Source(_previewLinear) is not { } s) return null;
        return AutoCrop is { } c ? Geometry.ApplyCrop(s, c) : s;
    }

    /// <summary>
    /// Whether the last <see cref="AutoDetectDMax"/> actually solved the highlight end.
    ///
    /// A field rather than a return value because <see cref="AutoDetectDMax"/> is also a command
    /// bound straight to a button, and because the caller that needs the answer —
    /// <see cref="AutoInvertCurrentFrame"/> — reads it several steps later, after other steps have
    /// rewritten StatusText. Inspecting the status text instead would tie control flow to a
    /// translated string.
    /// </summary>
    private bool _lastHighlightMeasured;

    /// <summary>Auto-detect D-max = 99.9th density percentile of the T_norm (T / t_base) frame.</summary>
    public void AutoDetectDMax()
    {
        ImageBuffer? src = AutoRegionStage1();
        if (src is null) return;
        // 不再预先除以 t_base：参考透射率恒为 1,1,1，估计器直接产出对 T=1 的绝对密度，
        // 与 D_min 同基准。（这里曾经按片基归一化，那是旧模型的做法。）
        //
        // Masked on the RAW region: both valleys are calibrated on raw luma. Without this the
        // light board and — far more damaging — the opaque blocking card sit inside the
        // percentile, and the card, being denser than any exposed area, simply becomes D-max.
        ImageBuffer? mask = AutoRegion();
        double? cut = AutoBoardCut();

        // BOTH ends, not just the scalar.
        //
        // The scalar is the output RANGE — where white lands. The white END is the per-channel
        // endpoint set, and the inversion divides by it. Setting only the scalar therefore
        // calibrates the black end (t_base) and leaves the white end wherever it happened to be:
        // on a fresh roll that is the neutral default, so the roll inverts through endpoints that
        // were never measured. Measured on 图像 001a that put the midtone red/blue ratio at 0.529
        // against 1.174 once the endpoints were measured — the picture was visibly wrong, and no
        // amount of re-running 单张 could fix it because 单张 was the thing not measuring them.
        // Same TWO estimators, in the same order, as the roll pass's `rollDMaxPerCh ?? rollWbHigh`.
        //
        // The per-channel detector is preferred but it can decline — it returns null when no frame
        // yielded a usable triplet, e.g. every kept pixel hit the density ceiling or the keep mask
        // left nothing. The roll pass has a second estimator behind it for exactly that case; this
        // path had none, so a decline left the highlight endpoint at whatever it happened to hold
        // — the neutral default on a fresh roll — while the status line below still printed those
        // stale numbers as though they had just been measured. That is the "单张没把高光段测上"
        // gap: not a wrong measurement but a missing one, reported as success.
        double[]? highlight = null;
        if (mask is not null)
        {
            highlight = FilmBase.DetectDMaxPerChannelFromRoll(
                new[] { src }, new[] { 1.0, 1.0, 1.0 }, 90.0, new[] { mask }, cut);

            // Fallback: the densest-highlight solve. It answers from the same masked pixels but
            // reduces them differently, so it still produces a triplet where the percentile
            // detector abstained. Throws when there is genuinely no usable highlight, which is
            // the one case where leaving the endpoint alone is right.
            if (highlight is null)
            {
                try
                {
                    highlight = FilmBase.AutoWbHighFromRoll(
                        new[] { mask }, new[] { 1.0, 1.0, 1.0 }, cut,
                        valueImages: ReferenceEquals(mask, src) ? null : new[] { src });
                }
                catch { /* no usable highlight in this frame — say so below */ }
            }
        }

        if (highlight is not null) DMaxPerChannel = highlight;
        _lastHighlightMeasured = highlight is not null;

        StatusText = highlight is not null
            ? Loc.F($"自动高光 → 亮端 {DMaxLevel:F3}（逐通道 {DMaxR:F3} / {DMaxG:F3} / {DMaxB:F3}）")
            : Loc.T("⚠ 这一帧测不到高光——亮端保持原值，请用【高光采样】手动标定或改用【自动（整卷）】");
    }

    /// <summary>
    /// The 自动（单张）button: geometry + the full inversion chain, for the CURRENT frame only.
    ///
    /// Same four photometric steps as <see cref="AutoInvertRollAsync"/>'s stage 1, in the same
    /// order and for the same reasons (see that method's remarks — t_base must precede
    /// everything, levels must come last).
    ///
    /// Deliberately does NOT touch the other frames. That is the whole distinction from the
    /// roll button: this one is the escape hatch for the frame the roll-wide solve got wrong —
    /// a lone tungsten interior, a frame shot on a different light source — so writing its
    /// result outward would defeat its purpose.
    /// </summary>
    public void AutoInvertCurrentFrame()
    {
        if (_previewLinear is null) return;

        // broadcastToRoll: false — this button is the single-frame escape hatch; the other frames
        // must keep whatever the roll-wide solve (or their own per-frame edits) already gave them.
        double? cut = AutoBoardCut();
        if (!AutoFilmBaseFromRoll(cut, useMode: true, broadcastToRoll: false)) return;

        bool highlightMeasured;
        _suppressRender = true;
        try
        {
            // Neutral start, for the reasons given in AutoInvertRollAsync: stale levels would clip
            // the positive the meter reads. The highlight endpoint needs no reset —
            // AutoDetectDMax overwrites all three channels of it.
            Black = 0.0; White = 0.0;
            // AutoDetectDMax sets BOTH ends — the scalar output range and the per-channel
            // highlight endpoint, which IS the highlight balance.
            AutoDetectDMax();
            // Whether the highlight end was actually measured — see the field's remarks. The
            // completion line at the end of this method would otherwise overwrite AutoDetectDMax's
            // warning with "完成" plus the untouched endpoint, so the failure would reach the user
            // as a success carrying stale numbers.
            highlightMeasured = _lastHighlightMeasured;
            // Levels stay neutral here too — see the note in AutoInvertRollAsync's stage 1.
        }
        finally { _suppressRender = false; }

        CommitLiveParams(CurrentFrame);
        MarkRollDirty();

        ScheduleRender();
        // Only this frame's thumbnail changed — the other frames were untouched.
        if (CurrentFrame is not null) { SetThumbnail(CurrentFrame, null); RestartThumbnails(); }
        StatusText = highlightMeasured
            ? Loc.F($"单张去色罩完成 · 片基 {TBaseR:F3}, {TBaseG:F3}, {TBaseB:F3} · 亮端 {DMaxLevel:F3}")
            : Loc.F($"单张去色罩完成（⚠ 这一帧测不到高光，亮端 {DMaxLevel:F3} 为原值）· 片基 {TBaseR:F3}, {TBaseG:F3}, {TBaseB:F3}");
    }

    /// <summary>
    /// The 自动（整卷）button: re-run the roll-wide auto-inversion on demand.
    ///
    /// Exposed as a button now, against the original note on <see cref="AutoInvertRollAsync"/>
    /// that a chain button would duplicate the individual step buttons. That reasoning held while
    /// the chain was import-only, but it left the automation invisible: the one control that does
    /// the whole job lived in a checkbox in the import dialog, and a user who unticked it, or who
    /// opened an existing roll, had no way back to it short of re-importing.
    ///
    /// It does overwrite wb_high and the levels across the roll, which is why it is a distinct,
    /// explicitly-pressed button rather than something that re-runs on its own.
    /// </summary>
    public Task AutoInvertRollCommandAsync() => AutoInvertRollAsync();

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

    // Stage-2 grey-point WB is gone along with the 色偏修正 group it fed. Colour balance is the
    // inversion's white end — one place, in 整卷校准 → 亮端 — and a second set of temp/tint on
    // top of the rendered positive could only mask what the endpoint already said.

    /// <summary>
    /// 自动黑点：自动找出裸露片基并写入暗端——【自动白点】在黑端的对称件。
    ///
    /// 估计器一直存在（自动链的第一步就是它），只是没有按钮，于是两端不对称：亮端有手动+两个
    /// 自动，黑端只有手动。三级回退与自动链完全相同：灯板下的片基峰 → 边缘裸片基窄带 → 亮端
    /// 分位（此时结果不是真片基，会告警）。
    /// </summary>
    public void AutoFilmBase()
    {
        if (_previewLinear is null) return;
        if (!AutoFilmBaseFromRoll(AutoBoardCut(), useMode: true, broadcastToRoll: false)) return;
        CommitLiveParams(CurrentFrame);
        MarkRollDirty();
        if (CurrentFrame is not null) { SetThumbnail(CurrentFrame, null); RestartThumbnails(); }
        ScheduleRender();
    }

    /// <summary>
    /// 最亮点白 (Stage 1, NegativeConvert way): find the frame's brightest neutral scene point and
    /// treat it as pure white → the per-channel HIGHLIGHT ENDPOINT, landing on the 亮端 sliders.
    /// Ports Python's 自动（寻找最亮点并视为纯白）via <see cref="FilmBase.AutoWbHighFromRoll"/>
    /// on the single current frame.
    ///
    /// It writes the endpoint because that is where the inversion reads the white end from. It
    /// previously wrote a separate wb_high multiplier, which the endpoint model had already
    /// stopped consuming — so this button rendered nothing at all.
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

        // 参考一律中性，与【自动（整卷）】和【自动高光】完全一致。
        //
        // 这里曾传 TBaseArr()，那会让估计器产出「相对片基」的密度，而黑端是**对 T=1 的绝对
        // 密度**——两端基准不一致，跨度 D_max − D_min 就会各通道少算一个 D_min，而三通道少得
        // 不一样多（整卷路径实测 R 少 0.086、B 少 0.538）=> 反差变小且严重偏色。新工程的 TBase
        // 恒为 1,1,1 所以看不出来，但旧工程会从文件里读回非中性的 TBase，那时这个按钮与另外
        // 两条路径就给出不同的亮端。
        double[] neutralRef = { 1.0, 1.0, 1.0 };
        IReadOnlyList<ImageBuffer>? values = ReferenceEquals(raw, val) ? null : new[] { val };
        double? cut = AutoBoardCut();

        // 同样的两个估计器、同样的顺序，与 AutoDetectDMax 和整卷链一致：逐通道端点优先，
        // 它弃权时才回退到最浓高光解。此前这里只有后者，于是三条「自动白端」路径在同一张片子
        // 上可能给出两个不同的答案。
        double[]? highlight = null;
        try
        {
            highlight = FilmBase.DetectDMaxPerChannelFromRoll(
                new[] { val }, neutralRef, 90.0, new[] { raw }, cut);
        }
        catch { /* 逐通道端点弃权——下面的回退还有机会 */ }

        if (highlight is null)
        {
            try
            {
                highlight = FilmBase.AutoWbHighFromRoll(new[] { raw }, neutralRef, cut, values);
            }
            catch (Exception ex)
            {
                StatusText = Loc.T("自动白平衡失败：") + ex.Message;
                return;
            }
        }

        if (highlight is null)
        {
            StatusText = Loc.T("⚠ 这一帧测不到高光——亮端保持原值，请用【高光采样】手动标定或改用【自动（整卷）】");
            return;
        }

        DMaxPerChannel = highlight;
        StatusText = Loc.F($"自动白点 → 亮端 {DMaxLevel:F3}（逐通道 {DMaxR:F3} / {DMaxG:F3} / {DMaxB:F3}）");
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
    private FrameParams BuildDeepWbRenderParams(double[] highlight, double iterDMax) => new()
    {
        OutputIntent = OutputIntent.Basic,
        TBase = TBaseArr(),
        DMinPerChannel = DMinPerChannel,
        // The trial highlight endpoint for THIS round. It is the quantity being solved, so it must
        // be what the render uses; everything else here matches BuildParams, because the net judges
        // a rendered positive and that render has to be the one the user is looking at.
        DMaxPerChannel = (double[])highlight.Clone(),
        DistortionK1 = DistortionK1, VignetteAmount = VignetteAmount, VignetteFalloff = VignetteFalloff,
        LccFlatField = LccEnabled && LccAvailable ? _lccFlatField : null,
        // Path A decoupling — MUST match BuildParams. The net judges a rendered positive and its
        // gains are folded straight into the highlight endpoint, which is then applied to a
        // pipeline that DOES decouple; iterating on an un-decoupled render solves the endpoint in
        // the wrong colour basis and lands magenta. d_highlight is measured on the decoupled
        // negative for the same reason, and rawDelta divides the net's log-gains BY that
        // d_highlight — so if these two disagree the mismatch is baked into every iteration. The
        // input characterisation is here for the same reason: the net judges colour, so it must
        // judge it in the space the export uses.
        DecoupleMatrix = _decoupleMatrix,
        DecoupleMode = DecoupleMode.Linear,
        DecoupleChromaMatrix = _decoupleChromaMatrix,
        SprocketEnabled = SprocketEnabled, SprocketThreshold = SprocketThreshold,
        // AutoCrop, not _cropRect: this renders _previewLinear, which on a split frame is the
        // margin box, and the stored rect would have the net judge a sliver of the negative.
        // Oriented, because ProcessFrame crops after the geometry stage.
        CropRect = OrientRect(AutoCrop, new FrameParams
        {
            QuarterTurns = _quarterTurns, FlipH = _flipH, FlipV = _flipV,
        }),
        Rotation = Rotation, QuarterTurns = _quarterTurns, FlipH = _flipH, FlipV = _flipV,
        // Stage 2 reset to defaults (the WB decision must not be polluted by artistic edits).
    };

    /// <summary>
    /// Smart white balance (Beta) — port of the source worker (gui/main_window.py
    /// _AutoWBAffineWorker + white_balance.nn_wb_high_step): start from the measured highlight,
    /// then iterate the Deep-WB net, folding a chroma-only density delta into the per-channel
    /// HIGHLIGHT ENDPOINT each round (adaptive output range, BASIC-rendered positive), up to 50
    /// rounds or |log_gains| &lt; 0.01.
    ///
    /// The net's decision lands on the endpoint because the endpoint is the white end the
    /// inversion actually reads (see <see cref="DensityEndpoints"/>). The worker this ports
    /// accumulated a wb_high multiplier instead; under the endpoint model nothing consumed that,
    /// so the button ran its 50 rounds and changed no pixels. The iteration is otherwise the same
    /// arithmetic — a multiplier on a fixed measured highlight and an endpoint moving away from
    /// that same highlight are the same one-parameter-per-channel family, related by
    /// <c>endpoint = d_highlight / wb_high</c>.
    /// </summary>
    public async Task AutoWbAiAsync()
    {
        if (_previewLinear is null) return;
        IsBusy = true;
        StatusText = Loc.T("智能白平衡分析中 …");
        try
        {
            // The solve needs the density→output SLOPE, which under the endpoint model is
            // per-channel and comes from the endpoints rather than from a grade parameter. The
            // mean is the right scalar here because the iteration only uses it to normalise a
            // chroma-only delta, and that delta is re-derived from the render every round.
            DensityEndpoints slopeRef = DensityEndpoints.For(BuildParams());
            double grade = (slopeRef.Scale[0] + slopeRef.Scale[1] + slopeRef.Scale[2]) / 3.0;
            double[] tBase = TBaseArr(), wbOffset = DMinPerChannel;
            // raw — the pipeline decouples internally, which only holds because
            // BuildDeepWbRenderParams carries DecoupleMatrix. Do not drop it there.
            ImageBuffer neg = _previewLinear;
            // The highlight anchor is measured the same way 自动亮部 WB measures it: masks off the
            // RAW region (where the sprocket cut and the dark valley are calibrated), values off the
            // decoupled one (where t_base/wb_high live and where the render below lands).
            ImageBuffer? anchorRaw = AutoRegion();
            ImageBuffer? anchorVal = AutoRegionStage1();
            if (anchorRaw is null || anchorVal is null) return;
            // The calibrated highlight endpoint — the roll's, already no-clip rescaled. Read on the
            // UI thread and cloned, because the observable properties behind it are not safe to
            // touch from the worker below.
            double[] calibratedEp = DMaxPerChannel;
            double? boardCut = AutoBoardCut();

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
                    boardCut,
                    valueImages: ReferenceEquals(anchorRaw, anchorVal) ? null : new[] { anchorVal });

                // Step 1 — start from the CALIBRATED endpoint, not from dHigh.
                //
                // dHigh is the raw top-tail density of THIS ONE FRAME and nothing else. The
                // calibrated endpoint is the same co-sited pick pooled across the roll and then
                // lifted by the uniform no-clip rescale (FilmBase.RescaleToClearChannelMax), so it
                // sits systematically HIGHER. Seeding from dHigh threw that rescale away, and since
                // Scale = outRange / (dMax - dMin), a lower endpoint is a steeper slope: white
                // arrives at a lower density, the whole frame lifts, and the real highlights blow.
                // That is the reported overexposure, and it was there before the net ran a single
                // round — every other path in the app (整卷标定, 单张自动高光) writes the calibrated
                // endpoint, and this was the only one that did not.
                //
                // The net decides BALANCE; placement is the calibration's job and stays its job.
                var ep = new double[3];
                for (int c = 0; c < 3; c++) ep[c] = Math.Max(calibratedEp[c], 1e-6);
                // dHigh is still measured — it is the divisor that turns the net's log-gains into a
                // density-slope delta below — but it no longer sets where the picture sits.
                // Debug, not Console: this is a WinExe with no console attached, so the writes went
                // nowhere a user could read. Debug.WriteLine reaches the debugger's output window
                // while developing and compiles out of Release entirely.
                Debug.WriteLine($"[AIWB] d_highlight={dHigh[0]:F4},{dHigh[1]:F4},{dHigh[2]:F4} " +
                                $"start={ep[0]:F4},{ep[1]:F4},{ep[2]:F4} slope={grade:F3}");

                // Step 2 — NN chroma-only iteration.
                bool conv = false;
                for (int it = 1; it <= 50; it++)
                {
                    // The output range that puts this iteration's highlight exactly at white.
                    // White lands at 0 when density reaches the channel's span, so the range the
                    // render needs is the widest span across the three channels.
                    double iterDMax = double.NegativeInfinity;
                    for (int c = 0; c < 3; c++) iterDMax = Math.Max(iterDMax, ep[c] - wbOffset[c]);
                    iterDMax = Math.Max(iterDMax, 1e-6);

                    ImageBuffer pos = Pipeline.ProcessFrame(neg, BuildDeepWbRenderParams(ep, iterDMax));
                    var (inp, outp) = corr.CorrectOnce(pos);
                    var (li, lo) = MeanLinearHighlight(inp, outp);

                    var logGains = new double[3];
                    for (int c = 0; c < 3; c++)
                        logGains[c] = Math.Log10(Math.Max(Math.Max(lo[c], 1e-8) / Math.Max(li[c], 1e-8), 1e-8));
                    double meanLog = (logGains[0] + logGains[1] + logGains[2]) / 3.0;

                    // chroma-only density delta (strip brightness on the delta itself). The net
                    // wants channel c brighter (logGains[c] > mean) ⇒ its white must come on
                    // EARLIER ⇒ a smaller endpoint, hence the subtraction. Under the multiplier
                    // form the same correction was an addition, because wb_high sat in the
                    // denominator of the endpoint — same move, opposite sign.
                    var rawDelta = new double[3];
                    for (int c = 0; c < 3; c++) rawDelta[c] = (logGains[c] - meanLog) / (grade * Math.Max(dHigh[c], 1e-6));
                    double meanRaw = (rawDelta[0] + rawDelta[1] + rawDelta[2]) / 3.0;

                    double dev = 0;
                    // The mean endpoint before the step. Pinning it afterwards is what makes this
                    // iteration actually chroma-only — see the renormalisation below.
                    double meanBefore = (ep[0] + ep[1] + ep[2]) / 3.0;
                    for (int c = 0; c < 3; c++)
                    {
                        // Scaled by the channel's own endpoint so the step is the same relative
                        // move the multiplier form made (it added to wh, and ep = dHigh/wh).
                        ep[c] = Math.Max(ep[c] * (1.0 - (rawDelta[c] - meanRaw)), 1e-3);
                        dev = Math.Max(dev, Math.Abs(logGains[c] - meanLog));
                    }

                    // RENORMALISE so the mean endpoint is exactly where it started.
                    //
                    // (rawDelta - meanRaw) sums to zero across channels, which WOULD be chroma-only
                    // if it were added. It is applied multiplicatively against three unequal ep[c],
                    // and mean(ep·(1-d)) = mean(ep) - mean(ep·d), where mean(ep·d) is only zero when
                    // the endpoints happen to be equal. So the "chroma-only" step leaked brightness
                    // on every round and compounded it over 50 — the endpoint mean drifted and the
                    // channels diverged with it. One shared factor puts the mean back without
                    // touching a single ratio between the channels, which is the only thing the net
                    // is entitled to move here.
                    double meanAfter = (ep[0] + ep[1] + ep[2]) / 3.0;
                    if (meanAfter > 1e-9)
                    {
                        double renorm = meanBefore / meanAfter;
                        for (int c = 0; c < 3; c++) ep[c] = Math.Max(ep[c] * renorm, 1e-3);
                    }

                    Debug.WriteLine($"[AIWB] iter {it}: range={iterDMax:F4} log_gains=" +
                                    $"{logGains[0]:F4},{logGains[1]:F4},{logGains[2]:F4} dev={dev:F4} " +
                                    $"endpoint={ep[0]:F4},{ep[1]:F4},{ep[2]:F4}");
                    int round = it;
                    Dispatcher.UIThread.Post(() => StatusText = Loc.F($"智能白平衡 第 {round}/50 轮 · 收敛度 {dev:F4}"));
                    if (dev < 0.01) { conv = true; break; }
                }

                // Step 3 — re-impose the no-clip guarantee the calibration carries.
                //
                // The loop moves the channels apart from each other, so a triple that cleared every
                // channel's extreme on round 1 need not clear it on the round it converges. The
                // endpoints are per-channel DIVISORS: any channel whose endpoint slips below its own
                // densest kept pixel clips there, and no Stage-2 control can bring it back. This is
                // the same uniform lift DetectDMaxPerChannelFromRoll applies to its own answer, from
                // the same shared helper — one factor for all three, so the balance the net just
                // solved survives untouched and only the placement moves.
                // Values off the DECOUPLED buffer and masks off the RAW one — the same split dHigh
                // is measured with above, and required for the same reason: the endpoint lives in
                // the decoupled domain, while the sprocket cut and dark valley are calibrated on
                // raw luma. Comparing an endpoint against maxima drawn from the other domain would
                // scale by a ratio between two different colour bases.
                double[] chanMax = FilmBase.MaxChannelDensityFromRoll(
                    new[] { anchorVal }, tBase,
                    masks: new[] { anchorRaw }, sprocketThreshold: boardCut);
                double[] safe = FilmBase.RescaleToClearChannelMax(ep, chanMax);
                Debug.WriteLine($"[AIWB] final: ep={ep[0]:F4},{ep[1]:F4},{ep[2]:F4} " +
                                $"chanMax={chanMax[0]:F4},{chanMax[1]:F4},{chanMax[2]:F4} " +
                                $"safe={safe[0]:F4},{safe[1]:F4},{safe[2]:F4}");
                return (safe, conv);
            });

            // The net's decision, on the field the inversion reads. Writing the three densities
            // drives SyncHighlightFromEndpoint through the property setters, so 亮度/色温/色调
            // land on the net's answer too — the user sees WHAT it decided in the same units they
            // would have dialled by hand, and can carry on adjusting from there.
            DMaxPerChannel = wbHigh;
            StatusText = Loc.F($"智能白平衡{(converged ? "" : Loc.T("（未收敛，仅供参考）"))} → 亮端 {DMaxR:F3} / {DMaxG:F3} / {DMaxB:F3}");
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
    /// <summary>
    /// Measure the rendered positive's ends and normalise to them.
    ///
    /// RUNS UNDER A PRINT-FILM LUT TOO. It was briefly blocked there, on the reasoning that a
    /// stock's toe and shoulder ARE its look and stretching them back to 0 and 1 flattens it —
    /// which is true, and is why it is not automatic on that path (see ApplyPrintLut, which leaves
    /// levels neutral when a cube is selected). But blocking the BUTTON took the judgement away
    /// from the user as well, and a scan whose highlight simply does not reach the stock's shoulder
    /// has a real gap that levels is the right tool for. So the default is neutral and the control
    /// stays available: not applied behind the user's back, not withheld from them either.
    /// </summary>
    public void AutoLevels()
    {
        if (_previewLinear is null) return;
        FrameParams p = BuildParams();
        p.BlackPoint = 0.0; p.WhitePoint = 1.0;   // measure the positive WITHOUT the current levels
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, ForPreview(p));
        // The mask is built on the RAW frame (where the valleys are calibrated) but indexes the
        // RENDERED positive, so the two must be the same size. ForPreview crops the render to the
        // frame rect on a split frame, which is exactly what AutoRegion returns — see KeepMaskFor,
        // which drops the mask rather than misapply it if they ever disagree.
        bool[]? keep = AutoRegion() is { } raw && raw.PixelCount == pos.PixelCount
            ? FilmBase.HighDensityKeepMask(raw, AutoBoardCut())
            : null;
        var (black, white) = LevelsPercentiles(pos.Data, 0.001, 0.999, keep);
        if (white - black < 1e-6) white = black + 1e-6;
        Black = WbMath.BlackPointToSlider(Math.Clamp(black, 0.0, 0.5));
        White = WbMath.WhitePointToSlider(Math.Clamp(white, 0.5, 1.0));
        StatusText = Loc.F($"自动色阶 → 黑场 {Black:F2} / 白场 {White:F2}");
    }

    /// <summary>
    /// Low/high percentiles over all RGB samples via a 4096-bin histogram on [0,1], with a spike
    /// guard on each end.
    ///
    /// Ported from NexFilm's <c>density_histogram_extremes</c>, which skips any bin holding more
    /// than 10% of the samples while under 20% accumulated. Without it a large flat region — a
    /// blown sky, a scanner's black surround, a clipped border left in the crop — piles into one
    /// bin, that single bin alone clears the 0.1% target, and the black or white point lands on
    /// the artefact instead of on the picture. Real picture tone carries grain and gradient, so
    /// it spreads across bins and survives the skip.
    ///
    /// Each end is scanned from its own side (the black point up from 0, the white point down
    /// from 1) so "accumulated so far" means distance into that end's own tail, which is what
    /// the 20% release threshold is measured against. The single forward pass this replaced
    /// could only have guarded the low end.
    /// </summary>
    /// <param name="keep">Per-PIXEL admission mask (not per sample), or null for every pixel. The
    /// board and the blocking card both survive the inversion as huge flat blocks — the board
    /// inverts to crushed black, the card to blown white — so on a frame that shows either, the
    /// two ends of this histogram are set by things that are not the photograph.</param>
    private static (double Black, double White) LevelsPercentiles(float[] data, double lowPct,
                                                                  double highPct, bool[]? keep = null)
    {
        const int bins = 4096;
        var hist = new int[bins];
        long n = 0;
        for (int p = 0; p * 3 + 2 < data.Length; p++)
        {
            if (keep is not null && p < keep.Length && !keep[p]) continue;
            for (int c = 0; c < 3; c++)
            {
                int b = (int)(data[p * 3 + c] * bins);
                hist[b < 0 ? 0 : b >= bins ? bins - 1 : b]++;
                n++;
            }
        }
        // Everything masked out (or an empty buffer): fall back to measuring the whole frame
        // rather than returning a degenerate 0..1 range that would flatten the picture.
        if (n == 0 && keep is not null) return LevelsPercentiles(data, lowPct, highPct, null);
        double spike = n * 0.10, guard = n * 0.20;

        // Walk one end of the histogram inward, skipping spike bins, and stop at `target`.
        double Scan(long target, bool ascending)
        {
            long acc = 0;
            for (int i = 0; i < bins; i++)
            {
                int b = ascending ? i : bins - 1 - i;
                if (hist[b] > spike && acc < guard) continue;
                acc += hist[b];
                if (acc >= target) return (b + 0.5) / bins;
            }
            return ascending ? 0.0 : 1.0;
        }

        return (Scan((long)(n * lowPct), ascending: true),
                Scan((long)(n * (1.0 - highPct)), ascending: false));
    }

    // ForPreview: the rect this is handed is normalised against the frame ON SCREEN, so it has to
    // measure the buffer that is on screen. Cropping again would both shrink the image and move
    // the sampled region off the spot the user clicked.
    private (double Min, double Max)? MinMaxLumaOfRenderedPositive((double X, double Y, double W, double H) rect)
    {
        if (_previewLinear is null) return null;
        ImageBuffer pos = Pipeline.ProcessFrame(_previewLinear, ForPreview(BuildParams()));
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
        // Stage 1 — lens / sprocket
        DistortionK1 = 0; VignetteAmount = 0; VignetteFalloff = 2.5;
        SprocketEnabled = false; SprocketThreshold = 0.9;
        // Stage 1 — film base
        TBaseR = TBaseG = TBaseB = 1.0;   // 参考透射率恒为中性；片基由黑端承载
        // 两端回到一组中性（无色偏）的默认值：黑在片基处，白在输出范围处。
        // 由自动标定或采样重新测量。
        DMinR = 0; DMinG = 0; DMinB = 0;
        DMaxR = DMaxG = DMaxB = FrameParams.OutputRange;
        // Stage 2
        Temp = 0; Tint = 0; ExposureEv = 0;
        Black = 0; White = 0; Contrast = 0; Highlights = 0; Shadows = 0; Saturation = 0;
        _curveM = new(); _curveR = new(); _curveG = new(); _curveB = new(); _curvePreserveHue = true;
        _curveHasEndpoints = false;
        // Geometry
        Rotation = 0; _quarterTurns = 0; _flipH = false; _flipV = false; _cropRect = null;
        // The cell goes with the crop here: a full geometry reset says this frame is the whole
        // file again, and a cell left behind would claim a negative the frame no longer occupies.
        _splitCell = null;
        FilmBaseText = "";
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
        // Populate the film-look picker before anything binds to it. It was only ever filled on
        // frame load, so with no roll open the collection was empty, the ComboBox had no row to
        // select, and it rendered blank instead of the standard entry — which is what an empty PrintLut
        // actually means and what the pipeline is doing.
        RebuildPrintLutList("");
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
        if (!_filmBaseSampled) FilmBaseText = "";
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
        CommitLiveParams(CurrentFrame);
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
        {
            // A split frame's tile was cut from the source as that frame's region PLUS its margin,
            // so the stored whole-scan rect does not describe it — re-expressed against the box, or
            // the cover gets a crop of a crop. Same rule as RenderPreviewAsync / RenderThumbnailAsync.
            cells.Add((TileFor(f), ForRegion(f.Params.Clone(), f, SplitCropOf(f))));
        }
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

        // 旧模型的工程载入后画面会变——面板顶部据此提示重跑标定。
        NeedsRecalibration = data.NeedsRecalibration;

        _calSourceDir = data.Meta.CalSourcePath;
        _calRgbPaths = data.Meta.CalRgbPaths is { } r && r.ContainsKey("R")
            ? new[] { r["R"], r.GetValueOrDefault("G", ""), r.GetValueOrDefault("B", "") } : null;
        _lccSourcePath = data.Meta.LccPath;

        // Drop the previous roll's pixels HERE, not further down: the calibration below caches the
        // previews of every frame it decodes, and a later Clear() would throw that work away.
        _thumbCts?.Cancel();
        _warmCts?.Cancel();
        _previews.Clear(); ClearTiles(); _negativeWb.Clear(); _fullSlot = null; _regionSlot = null;
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
        _previews.Clear(); ClearTiles(); _negativeWb.Clear(); _fullSlot = null; _regionSlot = null;
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

        // Set roll-level ops BEFORE loading so the sprocket estimate + auto film-base (which run
        // during LoadRollAsync) sample t_base in the DECOUPLED domain and the first render decouples.
        // The auto-inversion choice rides along for the same reason: ApplySprocketAuto acts on it
        // partway through the load.
        _cfgAutoInvert = cfg.AutoInvert;
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
                // Every one of these three resolves THIS frame's region, never the bare file: on a
                // split scan the file holds several negatives, and the path-keyed lookups used to
                // hand each frame the strip's first slice.
                var pre = SplitCropOf(f);
                ImageBuffer source = TileFor(f)
                                     ?? _previews.Get(PreviewKey(f.Path, pre))?.Preview
                                     ?? (await PreviewAsync(f.Path, pre).WaitAsync(ct)).Preview;
                await RenderThumbnailAsync(f, source, pre, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* skip undecodable frame */ }
        }
    }

    /// <summary>Render one frame's thumbnail off an already-decoded preview. Never decodes: every
    /// caller resolves the preview through <see cref="PreviewAsync"/> first, so the strip and the
    /// main view are guaranteed to be looking at the same pixels.</summary>
    /// <param name="margin">The region of the file <paramref name="preview"/> was decoded from, or
    /// null if it is the whole file. The stored rect is normalised against the whole scan, so on a
    /// region decode it has to be re-expressed against the box — left alone it cuts a fraction of a
    /// fraction and the strip shows a sliver at the wrong aspect ratio.</param>
    private async Task RenderThumbnailAsync(RollFrame f, ImageBuffer preview,
                                            (double X, double Y, double W, double H)? margin,
                                            CancellationToken ct)
    {
        FrameParams p = ForRegion(f.Params, f, margin);
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
        // _previewLinear may be a region decode — same rule as RenderPreviewAsync.
        FrameParams p = ForRegion(frame.Params, frame, _previewMargin);
        ImageBuffer prev = Resample.Box(_previewLinear, ThumbMaxEdge);
        SetThumbnail(frame, BitmapConvert.ToBitmap(Pipeline.ProcessFrame(prev, p)));
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

        // One work item per distinct IMAGE, not per file. Virtual copies of a whole frame share
        // their parent's path and no rect, so they still collapse to one decode; a split scan's
        // negatives each carry their own rect and are decoded separately, which is the point —
        // deduping those by path gave every frame of the strip the first one's pixels.
        var order = new List<(string Path, (double X, double Y, double W, double H)? Pre)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < frames.Count; i++)
        {
            RollFrame f = frames[(start + i) % frames.Count];
            var pre = SplitCropOf(f);
            if (seen.Add(PreviewKey(f.Path, pre))) order.Add((f.Path, pre));
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
            await Parallel.ForEachAsync(order, opts, async (item, token) =>
            {
                var (path, pre) = item;
                PreviewCache.Entry entry;
                try { entry = await PreviewAsync(path, pre).WaitAsync(token); }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref done); return; }   // undecodable → skip, keep going

                // Publish each frame's thumbnail the moment its decode lands, rather than at the
                // end of the roll — the strip fills in progressively instead of all at once.
                // Matched on the preview key, so a split frame only takes the decode of its OWN
                // slice and not a sibling's.
                string key = PreviewKey(path, pre);
                List<RollFrame> targets = await Dispatcher.UIThread.InvokeAsync(() =>
                    Frames.Where(f => f.Thumbnail is null &&
                                      PreviewKey(f.Path, SplitCropOf(f)) == key)
                          .ToList());
                foreach (RollFrame f in targets)
                {
                    if (token.IsCancellationRequested) return;
                    try { await RenderThumbnailAsync(f, entry.Preview, pre, token); }
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

    public void CopyCalibration() { _calClipboard = BuildParams(); HasCalClipboard = true; StatusText = Loc.T("已复制 Cineon 标定"); }
    public void CopyScene() { _sceneClipboard = BuildParams(); HasSceneClipboard = true; StatusText = Loc.T("已复制 Display 参数"); }

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

    // ══ 胶片风格（印片 LUT） ═══════════════════════════════════════════════════
    //
    // 与【输出空间】并列而不是并入其中，因为两者正交：LUT 决定画面被渲染成什么样，输出空间
    // 决定它被装进哪个容器。曾经把 "Kodak2383" 当成一个 ColorSpaceDef 塞进输出空间下拉——
    // 那是类型错误，三个色度坐标表达不了一张印片的响应，后来删掉了。
    //
    // 本软件不附带任何 LUT 文件。这些印片表征由各厂商自行授权，随附即是再分发；界面上出现的
    // 厂商名一律来自用户自己文件里的 TITLE，不是我们的声明。

    /// <summary>Cubes the picker offers, in order: 标准渲染 → 最近用过的 → 选择文件…</summary>
    public ObservableCollection<string> PrintLutNames { get; } = new();

    /// <summary>How many rows at the head of the picker are renderings rather than cube files —
    /// just the standard display rendering. Everything from here to the trailing "choose a file"
    /// verb is a path.
    ///
    /// A pure CST (Cineon log decoded, no rendering) briefly sat at row 1. It is gone from the
    /// pipeline too, not merely hidden here — an unrendered log plate is a step in someone else's
    /// grading pipeline rather than a look a roll picks, and it had no users. A roll naming the
    /// old <c>:cineon-log</c> sentinel now falls through to the standard rendering, which is what
    /// <see cref="ColorPipeline.ToOutputSpaceFor"/> does with any value that is not a resolvable
    /// cube.</summary>
    private const int FixedRows = 1;

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
        // ONE fixed row heads the list — the standard display rendering — so a lookup starts at
        // FixedRows, and the trailing "choose a file" verb is excluded as before.
        //
        int existing = _printLutPaths.Count == 0 ? -1
            : string.IsNullOrWhiteSpace(active) ? 0
            : _printLutPaths.FindIndex(FixedRows, Math.Max(_printLutPaths.Count - FixedRows - 1, 0),
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

        // A roll can name a cube that is not in this machine's history — a project from another
        // computer, or a file picked before the list was trimmed. It still belongs in the list,
        // otherwise the picker would show 无 while the render used a LUT.
        var paths = new List<string>(Settings.Current.RecentPrintLuts);
        if (!string.IsNullOrWhiteSpace(active)
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

        int i = _printLutPaths.FindIndex(FixedRows, _printLutPaths.Count - FixedRows - 1,
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

        try
        {
            PrintLuts.Forget(path);
            CubeLut lut = PrintLuts.Validate(path);
            StatusText = Loc.F($"已载入胶片风格：{lut.Title}（{lut.Size}³）。");
        }
        catch (Exception ex)
        {
            StatusText = Loc.F($"无法载入 LUT：{ex.Message}");
            return;
        }

        var recents = Settings.Current.RecentPrintLuts;
        recents.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, path);
        while (recents.Count > 8) recents.RemoveAt(recents.Count - 1);
        Settings.Save();

        RebuildPrintLutList(path);
        ApplyPrintLut(path);
    }

    /// <summary>Supplied by the view: shows a .cube open dialog, null if cancelled.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    /// <summary>What the selected output space is for, shown under the picker.</summary>
    public string OutputSpaceHint => OutputSpaces[_outputSpaceIndex].Name switch
    {
        "sRGB" => Loc.T("网页与大多数屏幕的通用选择。不确定就选它。"),
        "DisplayP3" => Loc.T("现代屏幕（Apple 设备、多数新款显示器）的宽色域，编码曲线与 sRGB 相同。"),
        "AdobeRGB" => Loc.T("色域比 sRGB 宽，青绿方向尤其明显，适合送印刷或继续修图。在不做色彩管理的软件里看会偏淡。"),
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

    private static void WriteExport(ImageBuffer img, string path, FrameParams p, ExportOptions opt)
    {
        // Downsample AFTER the render, not before: averaging finished pixels supersamples them,
        // whereas shrinking the negative first would throw away detail the render still needed
        // — and would move every Stage-1 measurement with it.
        ImageBuffer outImg = opt.Downsample ? Resample.Box(img, opt.MaxLongEdge) : img;

        // NO conversion happens here any more, and that is the point. The render already landed in
        // the roll's output space — step 4 ran before Stage 2, and Stage 2 ran inside it — so the
        // bytes on screen are the bytes to write. Converting again would be converting a second
        // time. All that is left is to name the space in the profile.
        //
        // NONE intent writes linear data: no profile offered here describes that, so the embed
        // request is skipped rather than producing a file whose profile disagrees with its pixels.
        ColorSpaceDef? icc = p.OutputIntent == OutputIntent.Basic && opt.EmbedIcc
            ? p.ResolvedOutputSpace
            : null;

        if (opt.Format == ExportFormat.Jpeg)
            JpegIO.ExportJpeg(outImg, path, opt.JpegQuality, null, icc);
        else TiffIO.ExportTiff16(outImg, path, opt.TiffCompression, icc);
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
                await Task.Run(() => WriteExport(Pipeline.ProcessFrame(ImageIo.LoadLinear(f.Path), ep),
                                                 outPath, ep, opt));
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
            var sources = new ImageBuffer[total];
            var cellParams = new FrameParams[total];
            // Warm previews first, on the shared decode path — this used to re-decode the entire
            // roll at full resolution just to shrink each frame to 900 px.
            for (int i = 0; i < total; i++)
            {
                // Each frame's OWN region: on a split scan the bare path would give every cell the
                // strip's first negative. The region is the frame plus its margin, so the crop
                // still runs below — against the box rather than the whole scan.
                var pre = SplitCropOf(frames[i]);
                sources[i] = (await PreviewAsync(frames[i].Path, pre)).Preview;
                cellParams[i] = ForRegion(frames[i].Params, frames[i], pre);
                ReportBackground(Loc.F($"印样 {++done}/{total} …"));
            }

            List<ImageBuffer> thumbs = await Task.Run(() =>
            {
                var t = new List<ImageBuffer>(total);
                for (int i = 0; i < total; i++)
                    t.Add(Pipeline.ProcessFrame(Resample.Box(sources[i], 900), cellParams[i]));
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
            FrameParams ep = ForExport(p, opt);
            await Task.Run(() => WriteExport(Pipeline.ProcessFrame(LoadFullLinear(srcPath), ep), path, ep, opt));
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
        // The before-edits view strips Stage 2 from a chain the region renderer applies whole, so
        // there is no matching patch to render and the (soft) preview stands in. The NEGATIVE view
        // does have one — see the negative flag threaded below.
        if (_showingBeforeEdits) return;
        bool negative = _showingNegative;
        // Read on the UI thread, where CurrentFrame is safe to touch, and captured for the
        // background render — the patch must carry the same gain as the preview it lands on.
        double[]? negativeWb = negative ? CurrentNegativeWb() : null;

        FrameParams p = BuildParams();
        string srcPath = frame.Path;
        int frameW, frameH;

        // Budget FIRST, and off the source DIMENSIONS, which the preview cache already knows.
        // Deciding after the decode would mean paying ~2 s of LibRaw for a patch we then refuse —
        // and at shallow zoom, where most of the frame is visible, refusing is the common case.
        // The FULL file's dimensions, deliberately, even for a split frame: everything below works
        // at full resolution and applies p.CropRect itself, so the geometry it needs is the whole
        // scan's. A split frame may have no path-keyed entry, though — the switch decodes it under
        // a region key — and returning early there silently disabled the sharp patch on every split
        // scan, so fall back to the region entry and scale the margin box back up to the file.
        var splitPre = SplitCropOf(frame);
        if (_previews.Get(srcPath) is { } full) (frameW, frameH) = (full.SourceWidth, full.SourceHeight);
        else if (splitPre is { } sp && _previews.Get(PreviewKey(srcPath, sp)) is { } part)
            (frameW, frameH) = ((int)Math.Round(part.SourceWidth / Math.Max(sp.W, 1e-9)),
                                (int)Math.Round(part.SourceHeight / Math.Max(sp.H, 1e-9)));
        else return;
        if (RegionRender.SourcePixelsFor(frameW, frameH, p, roi)
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
            var result = await Task.Run(() =>
            {
                ImageBuffer img; RegionRender.Roi realised;
                // Orientation-only bounds for the negative — that view applies no straighten and
                // no crop, so the fully-geometried rectangle would reserve (and return) the wrong
                // part of the file.
                var need = negative
                    ? RegionRender.RequiredSourceBoundsNegative(frameW, frameH, p, roi)
                    : RegionRender.RequiredSourceBounds(frameW, frameH, p, roi);
                ImageBuffer? slice = RegionSliceFor(srcPath, need, frameW, frameH);
                cts.Token.ThrowIfCancellationRequested();
                if (slice is not null)
                {
                    RegionSlot s = _regionSlot!;
                    (img, realised) = RegionRender.RenderFromSlice(slice, s.X0, s.Y0, frameW, frameH,
                                                                   p, roi, negative, negativeWb);
                }
                else
                {
                    // TIFF, or the DNG-Converter backend — neither can region-decode. Fall back
                    // to the whole frame, which is what this path always used to do.
                    ImageBuffer full = LoadFullLinear(srcPath);
                    cts.Token.ThrowIfCancellationRequested();
                    (img, realised) = RegionRender.Render(full, p, roi, negative, negativeWb);
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
        // The sprocket overlay is drawn in the FINISHED preview's geometry, so a rotation, a flip
        // or a crop moves it too. Refreshing here catches all of them at once — they each land in
        // this method, and hooking them individually is how one gets missed.
        if (ShowSprocketMask) UpdateSprocketOverlay();
        if (!_restoring) MarkEdit();   // a real, user-driven param change → undo-committable
        // The negative view owns the screen while it is up, so a render must not push a positive
        // into it — rotating mid-sampling did exactly that, replacing the negative being sampled
        // with the finished picture. Orientation is the one parameter the view does follow, so it
        // is re-derived here rather than skipped; everything else the view ignores anyway, which
        // makes this cheap and correct for both.
        if (_showingNegative) { RefreshNegativeView(); return; }
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
            // _dragSmall comes off _previewLinear, so it inherits its pre-cropped-ness.
            ImageBuffer outImg = Pipeline.ProcessFrame(_dragSmall, ForPreview(BuildParams()));
            // Histograms stay live: at a quarter of the pixels the pass is noise next to the
            // render, and a histogram that freezes mid-drag is exactly when it is being read.
            Histogram = HistogramData.FromBuffer(outImg.Data);
            ClippingOverlay = ShowClipping ? BuildClippingOverlay(outImg) : null;
            PreviewImage = BitmapConvert.ToBitmap(outImg);
        }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    private async Task RenderPreviewAsync(FrameParams p, CancellationToken ct)
    {
        ImageBuffer src = _previewLinear!;
        p = ForPreview(p);
        // Captured now, not read at apply time: if the user switches frames while this render is
        // in flight, the thumbnail it produces still belongs to the frame it was rendered from.
        RollFrame? frame = CurrentFrame;

        bool wantClipping = ShowClipping;

        (Bitmap bmp, HistogramData hist, Bitmap thumb, WriteableBitmap? clip) = await Task.Run(() =>
        {
            ImageBuffer outImg = Pipeline.ProcessFrame(src, p);
            ct.ThrowIfCancellationRequested();
            // Histogram on the same buffer that feeds the display (Basic = already sRGB-encoded).
            HistogramData h = HistogramData.FromBuffer(outImg.Data);
            WriteableBitmap? c = wantClipping ? BuildClippingOverlay(outImg) : null;
            // The film strip gets a SCALED COPY of this same finished positive — it does not run
            // its own pipeline pass. Until now the current frame's thumbnail was only rebuilt when
            // you LEFT the frame, so the strip showed a stale version of whatever you were
            // actively adjusting. Reusing the render costs one box pass over an image that is
            // already in cache, which is why the source does it here too
            // (main_window.py::_on_process_done → _film_strip.update_thumbnail(result)) rather
            // than paying for a second inversion. outImg is already cropped and oriented, so the
            // thumbnail matches the frame as composed.
            var t = (Bitmap)BitmapConvert.ToBitmap(Resample.Box(outImg, ThumbMaxEdge));
            return ((Bitmap)BitmapConvert.ToBitmap(outImg), h, t, c);
        }, ct);

        if (ct.IsCancellationRequested) { bmp.Dispose(); thumb.Dispose(); clip?.Dispose(); return; }
        void Apply()
        {
            PreviewImage = bmp;
            Histogram = hist;
            ClippingOverlay = clip;
            if (frame is not null) SetThumbnail(frame, thumb); else thumb.Dispose();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else await Dispatcher.UIThread.InvokeAsync(Apply);
    }
}
