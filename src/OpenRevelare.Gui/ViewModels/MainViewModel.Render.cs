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
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> —— 预览渲染：空闲时防抖，拖动时低延迟。
/// </summary>
public partial class MainViewModel
{
    // ── Preview rendering: debounced when idle, low-latency while dragging ──────
    //
    // The debounce is a "wait for quiet" rule, and a slider drag is never quiet: every move
    // cancelled the pending render and started the 50 ms wait again, so the picture did not move
    // at all until the user stopped — the control was live but the image was not following it.
    //
    // A drag therefore switches strategy entirely, the way the source does
    // (main_window.py::_on_interaction_started):
    //
    //   • No debounce. A move requests a render immediately.
    //   • Half the preview's long edge, i.e. a quarter of the pixels. The heavy density inversion
    //     re-runs on every move because FilmBase/WB edits invalidate everything upstream of it.
    //   • The downsampled source is computed ONCE per drag and reused. It cannot change mid-drag
    //     (the frame, its decode and the photometric chain are all fixed), and re-deriving it per
    //     move costs more than the render it feeds.
    //   • One background render at a time. Moves arriving during a pass are coalesced into
    //     the next pass's latest parameters, keeping input responsive without queuing old frames.
    //
    // Release restores full resolution with one immediate, un-debounced pass.
    private bool _interacting;
    private Stopwatch? _frameSwitchTrace;
    private bool _interactiveRunning;
    private bool _interactivePending;
    private ImageBuffer? _dragSmall;
    private WorkingFrame? _dragSource;
    internal Task InteractiveRenderTask { get; private set; } = Task.CompletedTask;
    private const int DragMaxEdge = PreviewMaxEdge / 2;

    /// <summary>A slider thumb or curve point was grabbed — enter low-latency drag mode.</summary>
    public void BeginInteractive()
    {
        CancelPreviewWarmup();
        CommitUndo();          // the grab closes the previous edit; the drag itself is one step
        _renderCts?.Cancel();  // whatever the last change queued is about to be superseded
        _interacting = true;
        _interactivePending = false;
        _dragSmall = null;
        _dragSource = null;
    }

    /// <summary>Released — back to full resolution, immediately.</summary>
    public void EndInteractive()
    {
        if (!_interacting) return;
        _interacting = false;
        _interactivePending = false;
        _dragSmall = null;
        _dragSource = null;
        _renderCts?.Cancel();
        RenderNow();
    }

    // Backstop for the whole render path, and the reason all three entry points below are
    // wrapped rather than just the async ones.
    //
    // ScheduleRender and RenderNow are `async void`: they are driven straight off property
    // setters, so there is no Task for anyone to observe and an escaping exception unwinds
    // past the message loop and TERMINATES the process, taking every unsaved edit in the roll
    // with it. The interactive pump needs the same exception backstop.
    //
    // The sampling path already learned this (MainWindow's pointer-released backstop and
    // TrySample); a render is exactly as user-triggered and exactly as fatal. Cancellation
    // stays silent — it is the normal outcome of superseding a queued render.
    private void ReportRenderFailure(Exception ex)
    {
        StatusText = Loc.T("渲染失败：") + ex.Message;
        NotifyProjectDependencies();
    }

    private async void ScheduleRender(bool markEdit = true)
    {
        CancelPreviewWarmup();
        if (_suppressRender || _previewLinear is null) return;
        if (!EnsureCurrentPreviewSource()) return;
        // The sprocket overlay is drawn in the FINISHED preview's geometry, so a rotation, a flip
        // or a crop moves it too. Refreshing here catches all of them at once — they each land in
        // this method, and hooking them individually is how one gets missed.
        if (markEdit && !_restoring) MarkEdit();
        // The negative view owns the screen while it is up, so a render must not push a positive
        // into it — rotating mid-sampling did exactly that, replacing the negative being sampled
        // with the finished picture. It is re-derived rather than skipped because the view follows
        // the whole GEOMETRY chain: a turn, a straighten or a crop applied while it is up has to
        // move the negative with it, or the two views stop agreeing the moment one is toggled.
        if (_showingNegative) { RefreshNegativeView(); return; }
        if (_showingBeforeEdits) { ShowBeforeEdits(); return; }
        // Any edit invalidates the sharp patch — it was rendered under the OLD parameters, and
        // leaving it up would show a stale rectangle pasted over a freshly rendered preview.
        // The synchronous negative/before branches clear it inside their complete publication;
        // an asynchronous positive render must drop it immediately while the new pass is pending.
        ClearSharpPatch();
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
        CancelPreviewWarmup();
        if (_suppressRender || _previewLinear is null) return;
        if (!EnsureCurrentPreviewSource()) return;
        if (_showingNegative) { RefreshNegativeView(); return; }
        if (_showingBeforeEdits) { ShowBeforeEdits(); return; }
        ClearSharpPatch();
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        try { await RenderPreviewAsync(BuildParams(), cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    /// <summary>Render the latest drag parameters with bounded background work.</summary>
    private void RenderInteractive()
    {
        _interactivePending = true;
        if (_interactiveRunning || _previewWorking is null) return;
        InteractiveRenderTask = RunInteractiveAsync();
    }

    private async Task RunInteractiveAsync()
    {
        _interactiveRunning = true;
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        WorkingFrame source = _previewWorking!;
        ImageBuffer? cached = ReferenceEquals(_dragSource, source) ? _dragSmall : null;
        try
        {
            WorkingFrame small = cached is not null ? source.WithPixels(cached) : await Task.Run(() =>
                source.WithPixels(Resample.Box(source.Pixels, DragMaxEdge)), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _dragSmall = small.Pixels;
            _dragSource = source;
            while (_interacting && _interactivePending && !cts.IsCancellationRequested
                   && !_showingNegative && !_showingBeforeEdits)
            {
                _interactivePending = false;
                using var dragTrace = RenderTrace.Scope("drag.total");
                await RenderPreviewAsync(BuildParams(), cts.Token, small);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportRenderFailure(ex); }
        finally
        {
            _interactiveRunning = false;
            // A new drag may start while a cancelled pass is draining. It still gets its
            // own source and token; the old pass must never publish over the settling frame.
            if (_interacting && _interactivePending && !_disposed && !_showingNegative && !_showingBeforeEdits)
                RenderInteractive();
        }
    }

    private async Task RenderPreviewAsync(FrameParams p, CancellationToken ct, WorkingFrame? interactiveSource = null)
    {
        CancelPreviewWarmup();
        using var totalTrace = RenderTrace.Scope("preview.total");
        WorkingFrame source = interactiveSource ?? _previewWorking!;
        p = ForPreview(WithDustReviewPreview(p)).Clone();
        ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
        // Captured now, not read at apply time: if the user switches frames while this render is
        // in flight, the thumbnail it produces still belongs to the frame it was rendered from.
        RollFrame? frame = CurrentFrame;
        SourceStamp? sourceStamp = _previewStamp;
        PreparedPreviewKey? preparedKey = interactiveSource is null ? PreparedKey(frame, sourceStamp, _previewKey, p) : null;
        long preparedGeneration = _preparedPreviews.Generation;

        bool wantClipping = ShowClipping;

        bool wantThumbnail = interactiveSource is null && frame is not null && !RollAnalysisPending
            && !HasDustPreviewOverride;

        (Bitmap bmp, HistogramData hist, Bitmap? thumb, WriteableBitmap? clip,
         PresentationScene scene, PresentationScene? clipScene, RenderedFrame rendered) = await Task.Run(async () =>
        {
            await _previewRenderLane.WaitAsync(ct);
            Bitmap? bitmap = null;
            Bitmap? thumbnail = null;
            WriteableBitmap? clipping = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                CachedPreviewData? prepared = preparedKey is null ? null : _preparedPreviews.Get(preparedKey);
                RenderTrace.Count(prepared is null ? "preview.prepared.miss" : "preview.prepared.hit");
                prepared ??= PrepareDisplayData(source, p, pipelineVersion, ct, sourceStamp, reuseFilm: true);
                RenderedFrame rendered = prepared.Rendered;
                ImageBuffer outImg = rendered.Pixels;
                PresentationScene scene = prepared.Scene;
                ct.ThrowIfCancellationRequested();
                // Histogram on the same buffer that feeds the display (Basic = already sRGB-encoded).
                HistogramData h = prepared.Histogram;
                ct.ThrowIfCancellationRequested();
                ClippingMasks? masks = wantClipping ? DetectClipping(outImg) : null;
                clipping = masks is { } detected
                    ? BuildClippingOverlay(outImg, detected)
                    : null;
                PresentationScene? clipScene = masks is { } sceneMasks
                    ? BuildClippingPresentationScene(outImg, sceneMasks)
                    : null;
                ct.ThrowIfCancellationRequested();
                // The film strip gets a SCALED COPY of this same finished positive — it does not run
                // its own pipeline pass. Until now the current frame's thumbnail was only rebuilt when
                // you LEFT the frame, so the strip showed a stale version of whatever you were
                // actively adjusting. Reusing the render costs one box pass over an image that is
                // already in cache, which is why the source does it here too
                // (main_window.py::_on_process_done → _film_strip.update_thumbnail(result)) rather
                // than paying for a second inversion. outImg is already cropped and oriented, so the
                // thumbnail matches the frame as composed.
                //
                // Not on an HDR roll, though: the strip is an SDR surface and shows the SDR rendition
                // (D-031), which is a different shoulder, not a scaled copy of the extended render.
                // A second pass over a 256 px source is a few milliseconds; the SDR roll keeps the copy.
                if (wantThumbnail)
                {
                    RenderedFrame thumbnailFrame = rendered.Encoding.Range == NumericRange.Extended
                        ? Pipeline.RenderSdrRendition(
                            source.WithPixels(Resample.Box(source.Pixels, ThumbMaxEdge)),
                            p,
                            pipelineVersion,
                            ColorManagement)
                        : rendered.WithPixels(Resample.Box(outImg, ThumbMaxEdge));
                    ct.ThrowIfCancellationRequested();
                    thumbnail = BuildFallbackBitmap(thumbnailFrame);
                }
                bitmap = RenderTrace.Measure("preview.fallback", () => BuildFallbackBitmap(rendered, scene));
                sourceStamp?.RequireCurrent();
                ct.ThrowIfCancellationRequested();
                if (preparedKey is not null) _preparedPreviews.Put(preparedKey, prepared, preparedGeneration);
                return (bitmap, h, thumbnail, clipping,
                        scene, clipScene, rendered);
            }
            catch
            {
                bitmap?.Dispose();
                thumbnail?.Dispose();
                clipping?.Dispose();
                throw;
            }
            finally { _previewRenderLane.Release(); }
        }, ct);

        if (ct.IsCancellationRequested) { bmp.Dispose(); thumb?.Dispose(); clip?.Dispose(); return; }
        void Apply()
        {
            using var publishTrace = RenderTrace.Scope("preview.publish");
            if (ct.IsCancellationRequested || !ReferenceEquals(frame, CurrentFrame)
                || (interactiveSource is not null && (!_interacting || _showingNegative || _showingBeforeEdits))
                || sourceStamp is { IsCurrent: false })
            { RenderTrace.Count("preview.publish.stale"); bmp.Dispose(); thumb?.Dispose(); clip?.Dispose(); return; }
            PublishCompletePreview(
                rendered,
                scene,
                bmp,
                hist,
                clip,
                clipScene,
                refreshSprocketMask: true);
            if (frame is not null && thumb is not null) SetThumbnail(frame, thumb);
            else thumb?.Dispose();
            if (_frameSwitchTrace is { } switchTrace)
            {
                RenderTrace.Observe("frame.switch.first-publication", switchTrace.Elapsed.TotalMilliseconds);
                _frameSwitchTrace = null;
            }
            if (interactiveSource is null && !HasDustPreviewOverride) SchedulePreviewWarmup();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else await Dispatcher.UIThread.InvokeAsync(Apply);
    }
}
