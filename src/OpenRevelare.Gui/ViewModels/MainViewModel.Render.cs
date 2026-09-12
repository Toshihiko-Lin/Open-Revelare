using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
        // The sprocket overlay is drawn in the FINISHED preview's geometry, so a rotation, a flip
        // or a crop moves it too. Refreshing here catches all of them at once — they each land in
        // this method, and hooking them individually is how one gets missed.
        if (!_restoring) MarkEdit();   // a real, user-driven param change → undo-committable
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
        if (_suppressRender || _previewLinear is null) return;
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

    /// <summary>One drag frame, inline on the UI thread at drag resolution.</summary>
    private void RenderInteractive()
    {
        if (_previewWorking is null) return;
        try
        {
            _dragSmall ??= Resample.Box(_previewWorking.Pixels, DragMaxEdge);
            // _dragSmall comes off _previewLinear, so it inherits its pre-cropped-ness.
            FrameParams parameters = ForPreview(BuildParams());
            RenderedFrame rendered = Pipeline.Render(
                _previewWorking.WithPixels(_dragSmall),
                parameters,
                _colorPipelineVersion,
                ColorManagement);
            ImageBuffer outImg = rendered.Pixels;
            PresentationScene scene = ConvertPreviewScene(rendered);
            Bitmap fallback = BuildFallbackBitmap(rendered, scene);
            // Histograms stay live: at a quarter of the pixels the pass is noise next to the
            // render, and a histogram that freezes mid-drag is exactly when it is being read.
            HistogramData histogram = HistogramData.FromFrame(rendered, parameters.ResolvedOutputTarget.HighlightHeadroom);
            WriteableBitmap? clipping = ShowClipping ? BuildClippingOverlay(outImg) : null;
            PresentationScene? clippingScene = ShowClipping
                ? BuildClippingPresentationScene(outImg)
                : null;
            PublishCompletePreview(
                rendered,
                scene,
                fallback,
                histogram,
                clipping,
                clippingScene,
                refreshSprocketMask: true);
        }
        catch (Exception ex) { ReportRenderFailure(ex); }
    }

    private async Task RenderPreviewAsync(FrameParams p, CancellationToken ct)
    {
        WorkingFrame source = _previewWorking!;
        p = ForPreview(p);
        ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
        // Captured now, not read at apply time: if the user switches frames while this render is
        // in flight, the thumbnail it produces still belongs to the frame it was rendered from.
        RollFrame? frame = CurrentFrame;

        bool wantClipping = ShowClipping;

        (Bitmap bmp, HistogramData hist, Bitmap thumb, WriteableBitmap? clip,
         PresentationScene scene, PresentationScene? clipScene, RenderedFrame rendered) = await Task.Run(() =>
        {
            RenderedFrame rendered = Pipeline.Render(source, p, pipelineVersion, ColorManagement);
            ImageBuffer outImg = rendered.Pixels;
            PresentationScene scene = ConvertPreviewScene(rendered);
            ct.ThrowIfCancellationRequested();
            // Histogram on the same buffer that feeds the display (Basic = already sRGB-encoded).
            HistogramData h = HistogramData.FromFrame(rendered, p.ResolvedOutputTarget.HighlightHeadroom);
            WriteableBitmap? c = wantClipping ? BuildClippingOverlay(outImg) : null;
            PresentationScene? clipScene = wantClipping
                ? BuildClippingPresentationScene(outImg)
                : null;
            // The film strip gets a SCALED COPY of this same finished positive — it does not run
            // its own pipeline pass. Until now the current frame's thumbnail was only rebuilt when
            // you LEFT the frame, so the strip showed a stale version of whatever you were
            // actively adjusting. Reusing the render costs one box pass over an image that is
            // already in cache, which is why the source does it here too
            // (main_window.py::_on_process_done → _film_strip.update_thumbnail(result)) rather
            // than paying for a second inversion. outImg is already cropped and oriented, so the
            // thumbnail matches the frame as composed.
            RenderedFrame thumbnailFrame = rendered.WithPixels(Resample.Box(outImg, ThumbMaxEdge));
            var t = BuildFallbackBitmap(thumbnailFrame);
            return (BuildFallbackBitmap(rendered, scene), h, t, c,
                    scene, clipScene, rendered);
        }, ct);

        if (ct.IsCancellationRequested) { bmp.Dispose(); thumb.Dispose(); clip?.Dispose(); return; }
        void Apply()
        {
            PublishCompletePreview(
                rendered,
                scene,
                bmp,
                hist,
                clip,
                clipScene,
                refreshSprocketMask: true);
            if (frame is not null) SetThumbnail(frame, thumb); else thumb.Dispose();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else await Dispatcher.UIThread.InvokeAsync(Apply);
    }
}
