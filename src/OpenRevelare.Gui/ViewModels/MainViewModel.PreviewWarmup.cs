using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private readonly PreparedPreviewCache _preparedPreviews = new();
    private CancellationTokenSource? _previewWarmCts;
    private readonly SemaphoreSlim _previewRenderLane = new(1, 1);

    private void CancelPreviewWarmup() => _previewWarmCts?.Cancel();

    private PreparedPreviewKey? PreparedKey(RollFrame? frame, SourceStamp? source, string? region, FrameParams p)
    {
        CalibrationSources.RequireCurrent(p);
        PrintLuts.RequireCurrent(p.PrintLut);
        if (frame is null || source is null || region is null) return null;
        SourceStamp? lut = null;
        if (!string.IsNullOrWhiteSpace(p.PrintLut) && !PrintLuts.IsBuiltin(p.PrintLut))
        {
            lut = SourceStamp.TryCapture(p.PrintLut);
        }
        return new(frame, source, region, PreviewParametersKey.Create(p), _colorPipelineVersion, lut);
    }

    private CachedPreviewData PrepareDisplayData(WorkingFrame source, FrameParams p,
        ColorPipelineVersion pipeline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RenderedFrame rendered = RenderTrace.Measure("preview.pipeline",
            () => Pipeline.Render(source, p, pipeline, ColorManagement));
        ct.ThrowIfCancellationRequested();
        PresentationScene scene = RenderTrace.Measure("preview.scene", () => ConvertPreviewScene(rendered));
        ct.ThrowIfCancellationRequested();
        HistogramData histogram = RenderTrace.Measure("preview.histogram", () =>
            HistogramData.FromFrame(rendered, p.ResolvedOutputTarget.HighlightHeadroom));
        return new(rendered, scene, histogram);
    }

    private static readonly bool PreviewWarmupEnabled = Environment.GetEnvironmentVariable("OPENREVELARE_PREVIEW_WARMUP") != "0";
    private bool MayWarmPreview => PreviewWarmupEnabled && !_disposed && !_interacting && !_showingNegative && !_showingBeforeEdits
        && !_cropEditing && !RollAnalysisPending && !ShowProgress && _paramsLoaded
        && (!SystemMemory.TryGetAvailableBytes(out long free) || free >= (1L << 30));

    private async void SchedulePreviewWarmup()
    {
        CancelPreviewWarmup();
        if (!MayWarmPreview || CurrentFrame is not { } active) return;
        var cts = new CancellationTokenSource();
        _previewWarmCts = cts;
        long generation = _preparedPreviews.Generation;
        int switchToken = _switchToken;
        try
        {
            await Task.Delay(350, cts.Token);
            if (!MayWarmPreview || !ReferenceEquals(CurrentFrame, active)) return;
            int index = Frames.IndexOf(active);
            foreach (int adjacent in new[] { index + 1, index - 1 })
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!MayWarmPreview || adjacent < 0 || adjacent >= Frames.Count) continue;
                RollFrame frame = Frames[adjacent];
                var margin = SplitCropOf(frame);
                string region = PreviewKey(frame.Path, margin);
                // Never launch a RAW decode for speculative work or wait behind import jobs.
                if (_previews.Get(region) is not { Source: { } stamp } entry) continue;
                FrameParams p = ForRegion(frame.Params.Clone(), frame, margin);
                PreparedPreviewKey? key = PreparedKey(frame, stamp, region, p);
                if (key is null || _preparedPreviews.Get(key) is not null) continue;
                ColorPipelineVersion pipeline = _colorPipelineVersion;
                // Foreground owns the lane; background only enters when it is immediately free.
                if (!await _previewRenderLane.WaitAsync(0, cts.Token)) return;
                CachedPreviewData prepared;
                try
                {
                    prepared = await Task.Run(() =>
                    {
                        using var warmTrace = RenderTrace.Scope("preview.warm.prepare");
                        return PrepareDisplayData(entry.Working, p, pipeline, cts.Token);
                    }, cts.Token);
                }
                finally { _previewRenderLane.Release(); }
                cts.Token.ThrowIfCancellationRequested();
                if (!MayWarmPreview || switchToken != _switchToken || !ReferenceEquals(active, CurrentFrame)
                    || PreparedKey(frame, SourceStamp.TryCapture(frame.Path), PreviewKey(frame.Path, SplitCropOf(frame)),
                        ForRegion(frame.Params.Clone(), frame, SplitCropOf(frame))) != key)
                { RenderTrace.Count("preview.warm.stale"); continue; }
                if (_preparedPreviews.Put(key, prepared, generation)) RenderTrace.Count("preview.warm.stored");
            }
        }
        catch (OperationCanceledException) { RenderTrace.Count("preview.warm.cancelled"); }
        catch (Exception) { RenderTrace.Count("preview.warm.failed"); } // speculative work never reports a foreground error
    }
}
