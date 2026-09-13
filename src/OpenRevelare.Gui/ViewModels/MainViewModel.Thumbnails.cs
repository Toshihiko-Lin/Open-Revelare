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
/// <see cref="MainViewModel"/> —— 片夹缩略图。
/// </summary>
public partial class MainViewModel
{
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
                WorkingFrame source = TileFor(f)
                                      ?? _previews.Get(PreviewKey(f.Path, pre))?.Working
                                      ?? (await PreviewAsync(f.Path, pre).WaitAsync(ct)).Working;
                await RenderThumbnailAsync(f, source, pre, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* skip undecodable frame */ }
        }
    }

    /// <summary>Render one frame's thumbnail off an already-decoded preview. Never decodes: every
    /// caller resolves the preview through <see cref="PreviewAsync"/> first, so the strip and the
    /// main view are guaranteed to be looking at the same pixels.
    ///
    /// The strip is an SDR surface — an Avalonia bitmap, composited with no headroom on any
    /// platform — so on an HDR roll it shows the roll's SDR rendition (D-031), not the extended
    /// render clipped at white: that would blow every highlight the preview keeps.</summary>
    /// <param name="margin">The region of the file <paramref name="preview"/> was decoded from, or
    /// null if it is the whole file. The stored rect is normalised against the whole scan, so on a
    /// region decode it has to be re-expressed against the box — left alone it cuts a fraction of a
    /// fraction and the strip shows a sliver at the wrong aspect ratio.</param>
    private async Task RenderThumbnailAsync(RollFrame f, WorkingFrame preview,
                                            (double X, double Y, double W, double H)? margin,
                                            CancellationToken ct)
    {
        FrameParams p = ForRegion(f.Params, f, margin).SdrRendition();
        ColorPipelineVersion pipelineVersion = _colorPipelineVersion;
        Bitmap bmp = await Task.Run(() =>
        {
            WorkingFrame small = preview.WithPixels(Resample.Box(preview.Pixels, ThumbMaxEdge));
            RenderedFrame rendered = Pipeline.Render(small, p, pipelineVersion, ColorManagement);
            return BuildFallbackBitmap(rendered);
        }, ct);
        if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
        await Dispatcher.UIThread.InvokeAsync(() => SetThumbnail(f, bmp));
    }

    /// <summary>Regenerate a frame's thumbnail from the in-memory preview (no re-decode).</summary>
    private void RefreshThumbnail(RollFrame frame)
    {
        if (_previewWorking is null) return;
        // _previewLinear may be a region decode — same rule as RenderPreviewAsync.
        FrameParams p = ForRegion(frame.Params, frame, _previewMargin).SdrRendition();
        WorkingFrame preview = _previewWorking.WithPixels(
            Resample.Box(_previewWorking.Pixels, ThumbMaxEdge));
        RenderedFrame rendered = Pipeline.Render(
            preview,
            p,
            _colorPipelineVersion,
            ColorManagement);
        SetThumbnail(frame, BuildFallbackBitmap(rendered));
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
                    try { await RenderThumbnailAsync(f, entry.Working, pre, token); }
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

}
