using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> —— 全分辨率导出。
/// </summary>
public partial class MainViewModel
{
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
            ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
            TiffInputAssumption tiffInputAssumption = _tiffInputAssumption;
            var exportBox = SplitCropOf(frame);
            JpegIO.JpegFit? fit = await Task.Run(() =>
            {
                var (working, boxed) = LoadForExport(
                    srcPath, exportBox, ep, pipelineVersion, tiffInputAssumption, sharedSlot: true);
                return RenderAndWriteExport(working, boxed, path, opt, pipelineVersion);
            });
            StatusText = Loc.F($"已导出：{Path.GetFileName(path)} · {opt.Summary()}") + DescribeFit(fit);
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
    public sealed record SharpPatch(
        Bitmap Image,
        RenderedFrame Frame,
        PresentationScene Scene,
        double X,
        double Y,
        double W,
        double H);

    // Property is `Patch`, not `SharpPatch` — a generated property may not share its name
    // with the type it holds.
    [ObservableProperty] private SharpPatch? _patch;

    partial void OnPatchChanging(SharpPatch? oldValue, SharpPatch? newValue)
    {
        if (!ReferenceEquals(oldValue?.Image, newValue?.Image)) Retire(oldValue?.Image);
    }

    partial void OnPatchChanged(SharpPatch? value) => InvalidatePresentation();

    /// <summary>
    /// Preview-resolution diagnostic masks cannot truthfully describe the full-resolution patch.
    /// Keep the typed patch cached, but do not composite it until those diagnostics are hidden.
    /// </summary>
    internal static bool ShouldPresentSharpPatch(bool showClipping, bool showSprocketMask) =>
        !showClipping && !showSprocketMask;

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
    private sealed record RegionSlot(
        string Path,
        ColorPipelineVersion PipelineVersion,
        TiffInputAssumption TiffInputAssumption,
        WorkingFrame Working,
        int X0,
        int Y0,
        int X1,
        int Y1);
    private RegionSlot? _regionSlot;

    /// <summary>
    /// How much bigger than the strictly-needed rectangle to decode, as a fraction of its size
    /// on each side — the FLOOR, before <see cref="RegionSlotMaxPixels"/> grows it further. Pure
    /// pan buffer: the decode's unpack stage is whole-file and irreducible (~1.07 s of a 1.14 s
    /// region decode on a 60 MP ARW), so re-decoding for every nudge is what would make panning
    /// unusable — whereas the extra pixels cost almost nothing.
    /// </summary>
    private const double RegionPanMargin = 0.35;

    /// <summary>
    /// Ceiling on the decoded slice, in source pixels — and, because of where the time goes, the
    /// thing that decides how often pixel-peeping has to wait at all.
    ///
    /// A region decode is ~94% unpack, which is whole-file and cannot be narrowed: the crop box
    /// only saves demosaic and output. So the box costs almost the same whatever size it is, and
    /// decoding the SMALLEST box that satisfies the request is the worst of both — the same
    /// second of unpack, and a cache that the next pan or zoom-out immediately misses, buying
    /// another second. Decoding the LARGEST box the memory budget allows costs the same second
    /// once and then answers every subsequent request in the area for free.
    ///
    /// 16 MP is ~190 MB as float RGB, on the order of the full-resolution slot the export path
    /// already holds, and it only exists while the user is zoomed in. On a frame at or below it
    /// the whole file is taken in one go and nothing in that frame ever decodes twice.
    /// </summary>
    private const long RegionSlotMaxPixels = 16_000_000;

    /// <summary>
    /// Grow <paramref name="need"/> about its own centre to fill <see cref="RegionSlotMaxPixels"/>,
    /// clamped to the frame. Keeps the need's aspect so the slack is spread the way a pan is
    /// likely to use it, and never shrinks the request.
    /// </summary>
    internal static (int X0, int Y0, int X1, int Y1) ExpandToSlotBudget(
        (int X0, int Y0, int X1, int Y1) need, int frameW, int frameH)
    {
        long frame = (long)frameW * frameH;
        if (frame <= RegionSlotMaxPixels) return (0, 0, frameW, frameH);   // take it whole

        double w = Math.Max(1, need.X1 - need.X0), h = Math.Max(1, need.Y1 - need.Y0);
        // Area scales with the square of a linear factor applied to both sides.
        double grow = Math.Sqrt(RegionSlotMaxPixels / (w * h));
        if (grow <= 1.0) return need;

        double cx = (need.X0 + need.X1) / 2.0, cy = (need.Y0 + need.Y1) / 2.0;
        double hw = w * grow / 2.0, hh = h * grow / 2.0;
        int x0 = (int)Math.Max(0, Math.Floor(cx - hw)), y0 = (int)Math.Max(0, Math.Floor(cy - hh));
        int x1 = (int)Math.Min(frameW, Math.Ceiling(cx + hw)), y1 = (int)Math.Min(frameH, Math.Ceiling(cy + hh));
        // Clamping at one edge frees budget the other side can use, which is what keeps a patch
        // near a border as well cached as one in the middle.
        return (Math.Min(x0, need.X0), Math.Min(y0, need.Y0),
                Math.Max(x1, need.X1), Math.Max(y1, need.Y1));
    }

    /// <summary>
    /// The decoded slice covering <paramref name="need"/>, from the cache when it already does.
    /// Null means this source cannot be region-decoded and the caller should fall back.
    /// Runs on the patch worker thread; the slot is only touched from there and from the
    /// UI-thread invalidation points, which never overlap because patches are single-flight.
    /// </summary>
    private WorkingFrame? RegionSliceFor(
        string path,
        (int X0, int Y0, int X1, int Y1) need,
        int frameW,
        int frameH,
        ColorPipelineVersion pipelineVersion,
        TiffInputAssumption tiffInputAssumption)
    {
        if (_regionSlot is { } s && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
            && s.PipelineVersion == pipelineVersion
            && s.TiffInputAssumption == tiffInputAssumption
            && s.X0 <= need.X0 && s.Y0 <= need.Y0 && s.X1 >= need.X1 && s.Y1 >= need.Y1)
            return s.Working;

        // A full-resolution decode of this very file may already be resident — the export path
        // leaves one behind, and the fallback below puts one there whenever a source cannot be
        // region-decoded. It covers every possible request, so taking it costs nothing where the
        // alternative is a second whole-file decode of the same pixels.
        FullSlot? full;
        lock (_fullSlotGate) full = _fullSlot;
        if (full is not null
            && string.Equals(full.Path, path, StringComparison.OrdinalIgnoreCase)
            && full.PipelineVersion == pipelineVersion
            && full.TiffInputAssumption == tiffInputAssumption
            // Only when it really is THIS frame's grid. A split roll's full slot holds the whole
            // scan while frameW/frameH are derived by scaling the region entry back up, and the
            // two can land a pixel or two apart — enough to offset every patch on the frame.
            && full.Working.Pixels.Width == frameW
            && full.Working.Pixels.Height == frameH)
        {
            _regionSlot = new RegionSlot(
                path, pipelineVersion, tiffInputAssumption, full.Working,
                0, 0, full.Working.Pixels.Width, full.Working.Pixels.Height);
            return full.Working;
        }

        int mw = (int)((need.X1 - need.X0) * RegionPanMargin);
        int mh = (int)((need.Y1 - need.Y0) * RegionPanMargin);
        int x0 = Math.Max(0, need.X0 - mw), y0 = Math.Max(0, need.Y0 - mh);
        int x1 = Math.Min(frameW, need.X1 + mw), y1 = Math.Min(frameH, need.Y1 + mh);
        // Then out to the memory budget: see RegionSlotMaxPixels for why bigger is cheaper here.
        (x0, y0, x1, y1) = ExpandToSlotBudget((x0, y0, x1, y1), frameW, frameH);

        var decoded = ImageIo.LoadWorkingRegion(
            path,
            x0,
            y0,
            x1 - x0,
            y1 - y0,
            frameW,
            frameH,
            pipelineVersion,
            ColorManagement,
            tiffInputAssumption);
        if (decoded is not ({ } working, int gx, int gy)) return null;
        _regionSlot = new RegionSlot(
            path,
            pipelineVersion,
            tiffInputAssumption,
            working,
            gx,
            gy,
            gx + working.Pixels.Width,
            gy + working.Pixels.Height);
        return working;
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

        ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
        TiffInputAssumption tiffInputAssumption = _tiffInputAssumption;
        bool needsDecode = _fullSlot is null
                           || _fullSlot.PipelineVersion != pipelineVersion
                           || _fullSlot.TiffInputAssumption != tiffInputAssumption
                           || !string.Equals(_fullSlot.Path, srcPath, StringComparison.OrdinalIgnoreCase);
        if (needsDecode) ReportBackground(Loc.T("载入全分辨率 …"));
        try
        {
            var result = await Task.Run(() =>
            {
                RenderedRegion region;
                // Same bounds either way — the two views now share a geometry chain, so they
                // read the same rectangle of the same file. Kept as the named call because it
                // states which view is being asked for, and because getting this wrong is silent:
                // a region decode sized off the other view hands back a patch of somewhere else.
                var need = negative
                    ? RegionRender.RequiredSourceBoundsNegative(frameW, frameH, p, roi)
                    : RegionRender.RequiredSourceBounds(frameW, frameH, p, roi);
                WorkingFrame? slice = RegionSliceFor(
                    srcPath,
                    need,
                    frameW,
                    frameH,
                    pipelineVersion,
                    tiffInputAssumption);
                cts.Token.ThrowIfCancellationRequested();
                if (slice is not null)
                {
                    RegionSlot s = _regionSlot!;
                    region = RegionRender.RenderFromSlice(
                        slice,
                        s.X0,
                        s.Y0,
                        frameW,
                        frameH,
                        p,
                        roi,
                        pipelineVersion,
                        ColorManagement,
                        negative,
                        negativeWb);
                }
                else
                {
                    // The DNG-Converter backend cannot region-decode. Fall back to the whole
                    // frame, which is what this path always used to do.
                    WorkingFrame full = LoadFullWorking(
                        srcPath,
                        pipelineVersion,
                        tiffInputAssumption);
                    cts.Token.ThrowIfCancellationRequested();
                    region = RegionRender.Render(
                        full,
                        p,
                        roi,
                        pipelineVersion,
                        ColorManagement,
                        negative,
                        negativeWb);
                }
                cts.Token.ThrowIfCancellationRequested();
                PresentationScene scene = ConvertPreviewScene(region.Frame);
                RegionRender.Roi realised = region.Realised;
                return new SharpPatch(
                    BuildFallbackBitmap(region.Frame, scene),
                    region.Frame,
                    scene,
                    realised.X,
                    realised.Y,
                    realised.W,
                    realised.H);
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

}
