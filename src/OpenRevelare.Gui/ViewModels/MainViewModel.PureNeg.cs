using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    internal sealed record PureNegInput(SourceStamp Stamp, int Width, int Height,
        (double X, double Y, double W, double H) Bounds);

    [ObservableProperty] private Bitmap? _dustReviewOverlay;
    [ObservableProperty] private PresentationScene? _dustReviewScene;
    [ObservableProperty] private bool _isDustReviewActive;
    private DustMask? _dustReviewPreviewMask;
    private bool _previewWithoutDust;
    private bool HasDustPreviewOverride => _previewWithoutDust || _dustReviewPreviewMask is not null;

    partial void OnDustReviewOverlayChanging(Bitmap? oldValue, Bitmap? newValue) => Retire(oldValue);
    partial void OnDustReviewSceneChanged(PresentationScene? value) => InvalidatePresentation();
    partial void OnIsDustReviewActiveChanged(bool value) => InvalidatePresentation();

    internal DustReviewProjection.GeometryKey? CurrentDustReviewGeometry()
    {
        if (_previewLinear is not { } source) return null;
        FrameParams p = ForPreview(BuildParams());
        return new(source.Width, source.Height, _previewMargin, p.EmbeddedLens,
            p.LensfunEnabled ? p.Lensfun : null, p.DistortionK1, p.QuarterTurns,
            p.FlipH, p.FlipV, p.Rotation, p.CropRect);
    }

    internal void ShowDustReviewOverlay(DustReviewProjection? projection, DustMask? mask)
    {
        UpdatePresentation(() =>
        {
            if (projection is null || mask is null)
            {
                DustReviewOverlay = null;
                DustReviewScene = null;
                return;
            }
            byte[] rgba = projection.MakeOverlay(mask);
            DustReviewOverlay = BitmapConvert.ToRgbaOverlay(rgba, projection.Width, projection.Height);
            DustReviewScene = PresentationSceneFactory.FromSrgbRgba8(rgba,
                new PixelSize(projection.Width, projection.Height), CanonicalPreviewReferenceWhiteScale);
        });
    }

    internal (PureNegInput Input, DustMask Mask)? AppliedDustReview()
    {
        if (!HasImage || !CanExportProject() || CurrentFrame is not { } frame ||
            _automaticDustMask is not { } mask || _previewStamp is not { } stamp ||
            SourceStamp.TryCapture(frame.Path) != stamp) return null;
        return (new(stamp, mask.Width, mask.Height, mask.Bounds), mask);
    }

    internal void SetDustReviewPreview(DustMask? mask, bool refresh = true, bool before = false)
    {
        if (_dustReviewPreviewMask == mask && _previewWithoutDust == before) return;
        _dustReviewPreviewMask = mask;
        _previewWithoutDust = before;
        if (refresh) ScheduleRender(markEdit: false);
    }

    internal FrameParams WithDustReviewPreview(FrameParams parameters)
    {
        if (!HasDustPreviewOverride) return parameters;
        var preview = parameters.Clone();
        if (_previewWithoutDust)
        {
            preview.DustEnabled = false;
            return preview;
        }
        var mask = _dustReviewPreviewMask!;
        preview.AutomaticDustMask = mask.PixelCount > 0 ? mask : null;
        preview.DustEnabled |= mask.PixelCount > 0;
        return preview;
    }

    internal async Task<PureNegInput> WritePureNegInputAsync(string destination, CancellationToken ct)
    {
        if (CurrentFrame is not { } frame || !HasImage || !CanExportProject())
            throw new InvalidOperationException(Loc.T("当前照片无法分析。"));
        string source = frame.Path;
        var split = !RawDecode.IsRawExtension(source) ? SplitCropOf(frame) : null;
        var parameters = BuildParams();
        // Analyze an SDR positive on the original pixel grid. Preserve its grade while
        // leaving all spatial edits for the main pipeline after the mask has been applied.
        parameters.EmbeddedLens = null;
        parameters.LensfunEnabled = false;
        parameters.DistortionK1 = 0;
        parameters.QuarterTurns = 0;
        parameters.FlipH = parameters.FlipV = false;
        parameters.Rotation = 0;
        parameters.CropRect = null;
        parameters.DustEnabled = false;
        var version = _colorPipelineVersion;
        var assumption = _tiffInputAssumption;
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var stamp = SourceStamp.Capture(source);
                var bounds = split ?? (X: 0d, Y: 0d, W: 1d, H: 1d);
                WorkingFrame working = RenderTrace.Measure("dust.decode",
                    () => LoadFullWorking(source, version, assumption, split));
                ct.ThrowIfCancellationRequested();
                stamp.RequireCurrent();
                CalibrationSources.RequireCurrent(parameters);
                PrintLuts.RequireCurrent(parameters.PrintLut);
                RenderedFrame rendered = RenderTrace.Measure("dust.positive", () =>
                    Pipeline.RenderSdrRendition(working, parameters, version, ColorManagement));
                ct.ThrowIfCancellationRequested();
                stamp.RequireCurrent();
                using (RenderTrace.Scope("dust.write-input"))
                    TiffIO.ExportTiff16(rendered, destination, TiffIO.CompressionMode.None);
                ct.ThrowIfCancellationRequested();
                // The child needs several full-size work buffers. Under memory pressure,
                // reclaim the decode after preparing its immutable input instead of paging it.
                long detectorHeadroom = Math.Max(1L << 30, (long)working.Pixels.PixelCount * 128);
                if (_previewAvailableBytes() is long available && available < detectorHeadroom)
                {
                    lock (_fullSlotGate)
                        if (ReferenceEquals(_fullSlot?.Working, working)) _fullSlot = null;
                }
                return new PureNegInput(stamp, rendered.Pixels.Width, rendered.Pixels.Height, bounds);
            }, ct);
        }
        finally { ReleaseBulkBuffers(); }
    }

    internal void ApplyPureNegMask(PureNegInput input, DustMask mask)
    {
        if (CurrentFrame is not { } frame || !string.Equals(Path.GetFullPath(frame.Path), input.Stamp.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T("当前照片已切换，请重新检测。"));
        input.Stamp.RequireCurrent();
        if (mask.PixelCount == 0 && !HasAutomaticDust)
        {
            StatusText = Loc.T("没有选中的自动修复区域。");
            return;
        }
        _automaticDustMask = mask.PixelCount > 0 ? mask : null;
        if (HasAutomaticDust && !DustEnabled) DustEnabled = true;
        MarkEdit();
        DustChanged(render: true);
        StatusText = HasAutomaticDust ? Loc.T("已应用自动除尘结果") : Loc.T("已清除自动除尘结果");
    }
}
