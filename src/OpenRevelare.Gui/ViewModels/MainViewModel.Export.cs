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
                // Same bounds either way — the two views now share a geometry chain, so they
                // read the same rectangle of the same file. Kept as the named call because it
                // states which view is being asked for, and because getting this wrong is silent:
                // a region decode sized off the other view hands back a patch of somewhere else.
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
                return new SharpPatch((Bitmap)BitmapConvert.ToBitmap(img, p.ResolvedOutputSpace),
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

}
