using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Presentation;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation.Win32;
using OpenRevelare.Presentation.Win32.Native;
using PresentationPixelRect = OpenRevelare.Presentation.PixelRect;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// Windows composition root for the color-critical preview. All image, mask and edit geometry is
/// flattened by the shared CPU compositor before the platform adapter sees it; the native child
/// receives only a final contract-bound buffer.
/// </summary>
public partial class MainWindow
{
    private readonly object _windowsPresentationGate = new();
    private WindowsPresentationSnapshot? _pendingWindowsPresentation;
    // The most recently CAPTURED snapshot, kept after the worker takes it: the worker compares a
    // finished frame's size against it before presenting (see PublishWindowsPresentation).
    private WindowsPresentationSnapshot? _newestWindowsSnapshot;
    private bool _windowsPresentationWorkerActive;
    private bool _windowsPresentationClosed;
    private long _windowsPresentationEpoch;
    // Owned by the single presentation worker (ProcessWindowsPresentationQueue): the picture
    // fitted to the viewport is kept between snapshots, so a crop-handle or straighten-line move
    // re-rasterises only the primitives on top of it. See ComposedBaseCache.
    private readonly ComposedBaseCache _composedBase = new();
    // The packed frame's bytes, reused across frames (PresentationBufferBuilder.Build's
    // reusableOutput). Safe because the present is synchronous and nothing retains the frame.
    private byte[]? _packedFrame;
    private string _lastWindowsColorDiagnostics = "Native color presentation has not initialized.";

    /// <summary>
    /// The native preview host for THIS platform, or null where there is none (Linux today). Both
    /// hosts present the same contract-bound FP16 buffer the shared compositor produces; choosing
    /// one here is the only place the composition root knows which OS it is on.
    /// </summary>
    private IPreviewHost? ActivePreview =>
        OperatingSystem.IsWindows() ? WindowsPreview
        : OperatingSystem.IsMacOS() ? MacOSPreview
        : null;

    private void InitializeWindowsPresentation()
    {
        IPreviewHost? preview = ActivePreview;
        bool enabled = preview is not null;
        // Each host hides itself off its own platform; the active one starts visible and then
        // gates itself on presenter availability.
        WindowsPreview.IsVisible = ReferenceEquals(preview, WindowsPreview);
        MacOSPreview.IsVisible = ReferenceEquals(preview, MacOSPreview);
        UpdateNoticeStripPlacement();
        // 徽章和【帮助 → 复制色彩诊断】原本跟着 WindowsPreview 一起关掉。但它们承载的是两件事，
        // 只有「显示链路契约」是 Windows 概念；「这一卷走的是哪条色彩管线、TIFF 输入按什么假设」
        // 三平台同样成立，而这两个出口是它**唯一**的去处 —— ColorPipelineDiagnostic 在整个 GUI
        // 里没有第二个消费者。于是 mac/Linux 用户既看不到自己这一卷按什么渲染，出了色彩问题也
        // 没有诊断可交。测试断言的是 VM 属性而不是 UI，所以三平台全绿也照不出这一条。
        CopyColorDiagnosticsMenuItem.IsVisible = true;
        if (!enabled)
        {
            UpdateColorPipelineOnlyStatus();
            return;
        }

        ColorDiagnosticStatus.IsVisible = true;

        preview!.ContractChanged += OnWindowsDisplayContractChanged;
        preview.PresentationFailed += OnWindowsPresentationFailed;
        preview.PresentationRecoveryRequested += OnWindowsPresenterRecoveryRequested;
        // Both hosts are AvaloniaObjects with the same six status properties; any of them changing
        // means the badge must be recomputed, and presenter availability also moves the notice strip.
        ((AvaloniaObject)preview).PropertyChanged += (_, args) =>
        {
            switch (args.Property.Name)
            {
                case nameof(IPreviewHost.CurrentContract):
                case "DisplayDiagnostics":
                case "PresenterDiagnostics":
                case nameof(IPreviewHost.VisibleWarning):
                case nameof(IPreviewHost.LastPresentationError):
                case nameof(IPreviewHost.IsPresenterAvailable):
                    UpdateWindowsColorStatus();
                    break;
            }

            if (args.Property.Name == nameof(IPreviewHost.IsPresenterAvailable))
                UpdateNoticeStripPlacement();
        };
        Opened += (_, _) => QueueWindowsPresentation();
    }

    /// <summary>
    /// Puts the notice strip back over the viewer wherever nothing occludes it.
    ///
    /// <para>
    /// Before the native presenter existed, the crop banner floated inside the viewer
    /// (<c>VerticalAlignment=Top</c>, no layout height of its own). Avalonia cannot draw over the
    /// Windows child HWND, so the strip was given a row of its own — but that row was applied
    /// unconditionally, and it costs real preview height: 58 px measured, enough to re-scale a
    /// portrait frame mid-edit, on macOS and Linux which have no airspace problem at all. The
    /// three cards are independent siblings, so the cost can be three rows.
    /// </para>
    /// </summary>
    private void UpdateNoticeStripPlacement()
    {
        // Row 1 is the strip's own Auto row; row 2 is the viewer. An empty Auto row collapses to
        // zero height, so moving the strip into the viewer's cell hands that height back to the
        // preview without any conditional markup. ZIndex is required there because a Grid paints
        // in child order and the viewer Border is declared after the strip.
        bool childWindowCoversViewport = ActivePreview?.IsPresenterAvailable == true;

        Grid.SetRow(NoticeStrip, childWindowCoversViewport ? 1 : 2);
        NoticeStrip.ZIndex = childWindowCoversViewport ? 0 : 1;
        // Matches the viewer Border's own padding so a floating card is not flush to the edge.
        NoticeStrip.Margin = childWindowCoversViewport
            ? default
            : new Thickness(10, 10, 10, 0);
    }

    private void OnWindowsPresentationViewModelPropertyChanged(
        MainViewModel viewModel,
        string? propertyName)
    {
        if (ActivePreview is null)
        {
            // 没有原生宿主的平台只关心两件事：徽章内容本身，以及「有没有打开卷」这个可见性条件。
            if (propertyName is nameof(MainViewModel.ColorPipelineDiagnostic)
                             or nameof(MainViewModel.HasImage))
            {
                UpdateColorPipelineOnlyStatus();
            }
            return;
        }
        if (propertyName == nameof(MainViewModel.PresentationRevision))
        {
            QueueWindowsPresentation();
            UpdateWindowsColorStatus();
        }
        else if (propertyName == nameof(MainViewModel.ColorPipelineDiagnostic))
        {
            // Diagnostics read the already-published typed frame. They must not recursively cause
            // another composition of the exact same presentation generation.
            UpdateWindowsColorStatus();
        }
    }

    private void OnWindowsDisplayContractChanged(object? sender, DisplayContract contract)
    {
        UpdateWindowsColorStatus();
        QueueWindowsPresentation();
    }

    private void OnWindowsPresentationFailed(
        object? sender,
        PreviewPresentationFailedEventArgs args)
    {
        if (args.Error is PresentationContractException)
            QueueWindowsPresentation();
        UpdateWindowsColorStatus();
    }

    private void OnWindowsPresenterRecoveryRequested(object? sender, EventArgs args)
    {
        UpdateWindowsColorStatus();
        // Recovery often keeps the same display contract. Recompose the current scene so the new
        // presenter is not left waiting for an unrelated view-model revision.
        QueueWindowsPresentation();
    }

    /// <summary>
    /// Coalescing worker: at most one expensive viewport compose is running, while the pending
    /// slot always contains the newest UI snapshot. Old results are discarded by epoch before
    /// they reach the host.
    /// </summary>
    private void QueueWindowsPresentation()
    {
        if (ActivePreview is null || _windowsPresentationClosed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(QueueWindowsPresentation, DispatcherPriority.Background);
            return;
        }

        WindowsPresentationSnapshot? snapshot = CaptureWindowsPresentationSnapshot();
        if (snapshot is null) return;

        bool startWorker = false;
        lock (_windowsPresentationGate)
        {
            if (_windowsPresentationClosed) return;
            _pendingWindowsPresentation = snapshot;
            _newestWindowsSnapshot = snapshot;
            if (!_windowsPresentationWorkerActive)
            {
                _windowsPresentationWorkerActive = true;
                startWorker = true;
            }
        }

        if (startWorker) _ = Task.Run(ProcessWindowsPresentationQueue);
    }

    private void ProcessWindowsPresentationQueue()
    {
        while (true)
        {
            WindowsPresentationSnapshot? snapshot;
            lock (_windowsPresentationGate)
            {
                snapshot = _pendingWindowsPresentation;
                _pendingWindowsPresentation = null;
                if (snapshot is null || _windowsPresentationClosed)
                {
                    _windowsPresentationWorkerActive = false;
                    return;
                }
            }

            try
            {
                var trace = RenderTrace.Start();
                PresentationScene finalScene = _composedBase.Compose(
                    snapshot.Background,
                    snapshot.ViewportSize,
                    snapshot.Overlays,
                    snapshot.Primitives);
                long tCompose = trace?.ElapsedMilliseconds ?? 0;
                PresentationBuffer buffer = snapshot.ViewModel.BuildPresentationBuffer(
                    finalScene,
                    snapshot.Contract,
                    _packedFrame);
                _packedFrame = ImmutableCollectionsMarshal.AsArray(buffer.Bytes);
                if (trace is not null)
                    RenderTrace.Write($"present {snapshot.ViewportSize.Width}x{snapshot.ViewportSize.Height}: compose {tCompose} ({(_composedBase.LastComposeReusedBase ? "cached base" : "full")}, {snapshot.Overlays.Count} overlays, {snapshot.Primitives.Count} prims) | pack {trace.ElapsedMilliseconds - tCompose} | total {trace.ElapsedMilliseconds} ms");

                // Straight from this worker, not via the UI thread. PresentNewest is the host's
                // thread-safe one-slot entry (it drains inline — InlinePreviewDispatcher),
                // so the frame reaches the screen without waiting for a UI-thread turn that a drag
                // does not give. Only the status-badge refresh still needs the UI thread.
                PublishWindowsPresentation(snapshot.Epoch, buffer);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(
                    () => ReportWindowsCompositionFailure(snapshot.Epoch, ex),
                    DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Runs on the composition worker. Everything it touches on the UI is posted.</summary>
    private void PublishWindowsPresentation(long epoch, PresentationBuffer buffer)
    {
        // NO epoch check here — and this was the actual cause of "the picture only updates when
        // the mouse stops". Every pointer move captures a snapshot and bumps the epoch; composing
        // one takes ~20 ms, during which a moving pointer has already captured the next. So the
        // frame just finished was ALWAYS "stale" by the time it was compared, and was dropped; the
        // only frame that ever matched was the one composed after the last move. Compose speed,
        // dispatcher priority and which thread presented were all beside the point.
        //
        // A finished frame is the newest picture that EXISTS. Presenting it is right even when a
        // newer snapshot is pending: the pending one is composed next and replaces it within a
        // frame. Staleness that matters — a closed window, a changed display contract — is caught
        // by the flag below and by the host's own contract validation, not by comparing epochs.
        // The epoch still gates failure REPORTING (ReportWindowsCompositionFailure), where an old
        // error must not overwrite the badge for a newer, healthy frame.
        //
        // The one staleness that DOES invalidate a frame is the viewport having changed size: the
        // native presenter rejects a buffer of the wrong size (InvalidSize) and tears itself down,
        // which blanked the preview and lost the crop frame the first time a frame composed before
        // a resize reached it. A resize always captures a new snapshot, so compare against the
        // newest one captured and let that one carry the picture instead.
        if (_windowsPresentationClosed) return;
        if (Volatile.Read(ref _newestWindowsSnapshot) is { } newest && newest.ViewportSize != buffer.Size) return;
        try
        {
            ActivePreview!.PresentNewest(buffer);
            Dispatcher.UIThread.Post(UpdateWindowsColorStatus, DispatcherPriority.Background);
        }
        catch (PresentationContractException)
        {
            QueueWindowsPresentation();   // marshals itself to the UI thread
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dispatcher.UIThread.Post(
                () => ReportWindowsCompositionFailure(epoch, ex),
                DispatcherPriority.Background);
        }
    }

    private void ReportWindowsCompositionFailure(long epoch, Exception error)
    {
        if (_windowsPresentationClosed || epoch != _windowsPresentationEpoch) return;
        // The exception's own message is developer text; it stays in the diagnostics ("Composition
        // error: …") rather than becoming the sentence the user is handed.
        _lastWindowsColorDiagnostics = BuildWindowsColorDiagnostics(error);
        ColorDiagnosticText.Text = ColorDiagnosticBadge.Format(
            string.Empty,
            default,
            string.Empty,
            string.Empty,
            hasWarning: true,
            colorManagementUnavailable: false);
        ColorDiagnosticText.Foreground = Brushes.OrangeRed;
        ToolTip.SetTip(
            ColorDiagnosticStatus,
            ColorDiagnosticBadge.FormatTooltip(hasWarning: true, colorManagementUnavailable: false));
    }

    private WindowsPresentationSnapshot? CaptureWindowsPresentationSnapshot()
    {
        if (Vm is not { } vm || ActivePreview?.CurrentContract is not { } contract) return null;
        double logicalWidth = ViewPort.Bounds.Width;
        double logicalHeight = ViewPort.Bounds.Height;
        double renderScaling = RenderScaling;
        if (!double.IsFinite(logicalWidth) || !double.IsFinite(logicalHeight) ||
            logicalWidth <= 0d || logicalHeight <= 0d ||
            !double.IsFinite(renderScaling) || renderScaling <= 0d)
            return null;

        long epoch = ++_windowsPresentationEpoch;
        PresentationPixelSize viewportSize = new(
            RoundPhysical(logicalWidth, renderScaling),
            RoundPhysical(logicalHeight, renderScaling));
        Color backgroundColor = ParseViewerBackground();
        PresentationScene background = PresentationSceneFactory.SolidSrgb(
            backgroundColor.R,
            backgroundColor.G,
            backgroundColor.B,
            byte.MaxValue,
            contract.ReferenceWhiteScale);

        var overlays = new List<PresentationOverlay>();
        var primitives = new List<PresentationRasterPrimitive>();

        // The view model is platform-neutral (§11.1) and therefore tags every scene it builds with
        // the carrier's nominal white. The reference-white policy belongs to the DISPLAY, and this
        // is the first place that knows which display the frame is going to, so the scenes are
        // re-tagged here. Under D-020 the scale is no longer a constant: dragging the window from
        // a WCG display to an HDR one changes it without changing a rendered pixel. Re-tagging
        // shares the pixel storage; the compositor and PresentationBufferBuilder both reject a
        // composition whose layers disagree on the policy, so all of them must be re-tagged.
        float referenceWhiteScale = contract.ReferenceWhiteScale;
        // D-028: an extended render that out-reaches this display's headroom is fitted into it
        // here — presentation generation, the one place I5 lets the display in — rather than
        // clipped by the compositor. The fit precedes the re-tag so the cache keys on the view
        // model's own scene object. The sharp patch is the same render at full resolution and
        // gets the same fit, or it would sit brighter than the picture around it.
        float contentHeadroom = vm.PreviewHighlightHeadroom;
        float displayHeadroom = contract.ExtendedHeadroom;
        if (vm.PreviewScene is { } previewScene &&
            _frameSoftProof.Fit(previewScene, contentHeadroom, displayHeadroom)
                .WithReferenceWhiteScale(referenceWhiteScale) is { } baseScene)
        {
            var geometry = new PreviewViewportGeometry(
                baseScene.Size,
                new PreviewSize(logicalWidth, logicalHeight),
                renderScaling,
                _zoom,
                new PreviewPoint(_pan.X, _pan.Y));
            PresentationPixelRect imageDestination = RoundUnclipped(geometry.DisplayedImagePhysical);
            overlays.Add(new PresentationOverlay(baseScene, imageDestination));

            if (MainViewModel.ShouldPresentSharpPatch(vm.ShowClipping, vm.ShowSprocketMask) &&
                vm.Patch is { } patch)
            {
                var normalized = new PreviewRect(patch.X, patch.Y, patch.W, patch.H);
                overlays.Add(new PresentationOverlay(
                    _patchSoftProof.Fit(patch.Scene, contentHeadroom, displayHeadroom)
                        .WithReferenceWhiteScale(referenceWhiteScale),
                    RoundUnclipped(geometry.NormalizedToPhysical(normalized))));
            }

            if (vm.ShowSprocketMask && vm.SprocketMaskScene is { } sprocket)
            {
                overlays.Add(new PresentationOverlay(
                    sprocket.WithReferenceWhiteScale(referenceWhiteScale), imageDestination));
            }

            if (vm.ShowClipping && vm.ClippingScene is { } clipping)
            {
                overlays.Add(new PresentationOverlay(
                    clipping.WithReferenceWhiteScale(referenceWhiteScale), imageDestination));
            }

            AddCropPrimitives(primitives, geometry, renderScaling);
            AddTransientSelectionPrimitives(primitives, renderScaling);
        }

        return new WindowsPresentationSnapshot(
            epoch,
            vm,
            contract,
            viewportSize,
            background,
            overlays.ToArray(),
            primitives.ToArray());
    }

    private void AddCropPrimitives(
        List<PresentationRasterPrimitive> primitives,
        PreviewViewportGeometry geometry,
        double renderScaling)
    {
        if (_mode != SampleMode.Crop || _cropDraft is not { } crop) return;

        // A crop draft is legitimately degenerate for part of its life: pointer-down on a fresh
        // rectangle seeds it as (x, y, 0, 0), and ApplyCropDrag keeps a zero side for as long as
        // the drag stays on one axis — free aspect does not correct it, and the pointer is clamped
        // to [0,1], so dragging along an image edge holds it at zero. OnOverlayReleased discards
        // such a draft afterwards, which is the same admission.
        //
        // RenderCropFrame calls QueueWindowsPresentation unconditionally, so that state reaches
        // here mid-drag, and PreviewRect REJECTS a non-positive side. Nothing between this method
        // and Avalonia's pointer dispatch catches it — and per OnOverlayReleased's own comment,
        // what escapes pointer dispatch "unwinds past the message loop and kills the process".
        // The Avalonia renderer never had the problem: RenderCropFrame clamps with Math.Max(0, w)
        // and a zero-sized Rectangle simply draws nothing.
        //
        // AddTransientSelectionPrimitives already guards its own rect this way; this is the same
        // guard on the path that was missing it.
        if (crop.W <= 0d || crop.H <= 0d) return;

        PreviewRect image = geometry.DisplayedImagePhysical;
        PreviewRect frame = geometry.NormalizedToPhysical(
            new PreviewRect(crop.X, crop.Y, crop.W, crop.H));

        PremultipliedLinearRgba dim = SrgbPremultiplied(PreviewOverlayStyle.CropDim);
        AddSolid(primitives, image.X, image.Y, image.Width, frame.Y - image.Y, dim);
        AddSolid(primitives, image.X, frame.Bottom, image.Width, image.Bottom - frame.Bottom, dim);
        AddSolid(primitives, image.X, frame.Y, frame.X - image.X, frame.Height, dim);
        AddSolid(primitives, frame.Right, frame.Y, image.Right - frame.Right, frame.Height, dim);

        double frameStroke = PreviewOverlayStyle.FrameStrokeThickness * renderScaling;
        primitives.Add(new PresentationRectOutline(
            frame,
            frameStroke,
            SrgbPremultiplied(PreviewOverlayStyle.Marquee)));

        PremultipliedLinearRgba guide = SrgbPremultiplied(PreviewOverlayStyle.Guide);
        double guideStroke = PreviewOverlayStyle.GuideStrokeThickness * renderScaling;
        double x1 = frame.X + frame.Width / 3d;
        double x2 = frame.X + frame.Width * 2d / 3d;
        double y1 = frame.Y + frame.Height / 3d;
        double y2 = frame.Y + frame.Height * 2d / 3d;
        primitives.Add(new PresentationLine(
            new PreviewPoint(x1, frame.Y), new PreviewPoint(x1, frame.Bottom), guideStroke, guide));
        primitives.Add(new PresentationLine(
            new PreviewPoint(x2, frame.Y), new PreviewPoint(x2, frame.Bottom), guideStroke, guide));
        primitives.Add(new PresentationLine(
            new PreviewPoint(frame.X, y1), new PreviewPoint(frame.Right, y1), guideStroke, guide));
        primitives.Add(new PresentationLine(
            new PreviewPoint(frame.X, y2), new PreviewPoint(frame.Right, y2), guideStroke, guide));

        double handle = HandleScreenSize * renderScaling;
        double half = handle / 2d;
        foreach (PreviewPoint center in CropHandleCenters(frame))
        {
            var bounds = new PreviewRect(center.X - half, center.Y - half, handle, handle);
            primitives.Add(new PresentationSolidRect(bounds, SrgbPremultiplied(PreviewOverlayStyle.HandleFill)));
            primitives.Add(new PresentationRectOutline(
                bounds,
                PreviewOverlayStyle.HandleOutlineThickness * renderScaling,
                SrgbPremultiplied(PreviewOverlayStyle.HandleOutline)));
        }
    }

    private void AddTransientSelectionPrimitives(
        List<PresentationRasterPrimitive> primitives,
        double renderScaling)
    {
        if (SelRect.IsVisible && SelRect.Width > 0d && SelRect.Height > 0d)
        {
            PreviewRect bounds = TransformOverlayRect(
                Canvas.GetLeft(SelRect),
                Canvas.GetTop(SelRect),
                SelRect.Width,
                SelRect.Height,
                renderScaling);
            primitives.Add(new PresentationSolidRect(bounds, SrgbPremultiplied(PreviewOverlayStyle.MarqueeFill)));
            primitives.Add(new PresentationRectOutline(
                bounds,
                PreviewOverlayStyle.FrameStrokeThickness * renderScaling,
                SrgbPremultiplied(PreviewOverlayStyle.Marquee)));
        }

        if (SelLine.IsVisible)
        {
            primitives.Add(new PresentationLine(
                TransformOverlayPoint(SelLine.StartPoint, renderScaling),
                TransformOverlayPoint(SelLine.EndPoint, renderScaling),
                PreviewOverlayStyle.StraightenStrokeThickness * renderScaling,
                SrgbPremultiplied(PreviewOverlayStyle.Straighten)));
        }
    }

    private PreviewRect TransformOverlayRect(
        double x,
        double y,
        double width,
        double height,
        double renderScaling) => new(
            ((x * _zoom) + _pan.X) * renderScaling,
            ((y * _zoom) + _pan.Y) * renderScaling,
            width * _zoom * renderScaling,
            height * _zoom * renderScaling);

    private PreviewPoint TransformOverlayPoint(Point point, double renderScaling) => new(
        ((point.X * _zoom) + _pan.X) * renderScaling,
        ((point.Y * _zoom) + _pan.Y) * renderScaling);

    private static IEnumerable<PreviewPoint> CropHandleCenters(PreviewRect frame)
    {
        double centerX = frame.X + frame.Width / 2d;
        double centerY = frame.Y + frame.Height / 2d;
        yield return new PreviewPoint(frame.X, frame.Y);
        yield return new PreviewPoint(centerX, frame.Y);
        yield return new PreviewPoint(frame.Right, frame.Y);
        yield return new PreviewPoint(frame.Right, centerY);
        yield return new PreviewPoint(frame.Right, frame.Bottom);
        yield return new PreviewPoint(centerX, frame.Bottom);
        yield return new PreviewPoint(frame.X, frame.Bottom);
        yield return new PreviewPoint(frame.X, centerY);
    }

    private static void AddSolid(
        List<PresentationRasterPrimitive> primitives,
        double x,
        double y,
        double width,
        double height,
        PremultipliedLinearRgba color)
    {
        if (width <= 0d || height <= 0d) return;
        primitives.Add(new PresentationSolidRect(new PreviewRect(x, y, width, height), color));
    }

    private static PremultipliedLinearRgba SrgbPremultiplied(Color color)
    {
        float alpha = color.A / 255f;
        return new PremultipliedLinearRgba(
            DecodeSrgb(color.R / 255f) * alpha,
            DecodeSrgb(color.G / 255f) * alpha,
            DecodeSrgb(color.B / 255f) * alpha,
            alpha);

        static float DecodeSrgb(float encoded) => encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    private static PresentationPixelRect RoundUnclipped(PreviewRect rectangle)
    {
        int left = checked((int)Math.Round(rectangle.X, MidpointRounding.AwayFromZero));
        int top = checked((int)Math.Round(rectangle.Y, MidpointRounding.AwayFromZero));
        int right = checked((int)Math.Round(rectangle.Right, MidpointRounding.AwayFromZero));
        int bottom = checked((int)Math.Round(rectangle.Bottom, MidpointRounding.AwayFromZero));
        if (right <= left) right = checked(left + 1);
        if (bottom <= top) bottom = checked(top + 1);
        return new PresentationPixelRect(left, top, right - left, bottom - top);
    }

    private static int RoundPhysical(double logical, double scaling)
    {
        double scaled = logical * scaling;
        if (!double.IsFinite(scaled) || scaled > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(logical));
        return Math.Max(1, checked((int)Math.Round(scaled, MidpointRounding.AwayFromZero)));
    }

    private static Color ParseViewerBackground()
    {
        string configured = Services.Settings.Current.ViewerBackground;
        try { return Color.Parse(configured); }
        catch (FormatException) { return Color.Parse("#5E5E5E"); }
    }

    /// <summary>
    /// 非 Windows 平台的徽章内容：只有与平台无关的那一半 —— 色彩管线版本与 TIFF 输入假设。
    ///
    /// 显示链路那一半（contract / encoding / monitor / WYSIWYG）是 Windows 概念，这里没有，
    /// 也不该编一个出来：mac/Linux 的最后一跳目前封顶在 sRGB，声称 WYSIWYG 会是假话。
    /// 那半截属于能力对齐（M5/M6），不属于这条披露。
    ///
    /// 没有打开卷时整条徽章隐藏 —— 这条信息是按卷成立的，空着显示一个默认值只会误导。
    /// </summary>
    private void UpdateColorPipelineOnlyStatus()
    {
        if (ActivePreview is not null) return;
        if (Vm is not { HasImage: true } vm)
        {
            ColorDiagnosticStatus.IsVisible = false;
            return;
        }

        ColorDiagnosticStatus.IsVisible = true;
        ColorDiagnosticText.Text = vm.ColorPipelineDiagnostic;
        ColorDiagnosticText.Foreground = Brushes.Gray;
        // 这一半没有告警态：它报告的是「按什么渲染的」，不是「哪里坏了」。
        ToolTip.SetTip(ColorDiagnosticStatus, null);
    }

    private void UpdateWindowsColorStatus()
    {
        if (ActivePreview is not { } preview) return;
        DisplayContract? contract = preview.CurrentContract;
        if (contract is null)
        {
            ColorDiagnosticText.Text = "Native preview · probing display contract…";
            ColorDiagnosticText.Foreground = Brushes.Orange;
            // Transient, and not a fault: do not leave a stale hover behind.
            ToolTip.SetTip(ColorDiagnosticStatus, null);
            return;
        }

        Vm?.SetDisplayHdrCapability(DescribeDisplayCapability(preview, contract));
        string monitor = DescribeMonitorName(preview, contract);
        string warning = preview.VisibleWarning ?? preview.LastPresentationError ?? string.Empty;
        string guarantee = PresentationGuarantee.IsEffective(
            contract,
            preview.IsPresenterAvailable)
            ? "WYSIWYG"
            : "unmanaged";
        // The guarantee above is about the LAST hop only. On a roll whose input was never
        // characterized — RAW today — an unqualified "WYSIWYG" is read as a claim about the whole
        // chain, while the full diagnostics one click away say "Input encoding: Uncharacterized".
        // Both are true; only one of them is on screen. D-009 asks the visible one to be honest.
        // Kept SHORT on purpose. This TextBlock is capped at MaxWidth 560 with CharacterEllipsis,
        // and the badge's fixed prefix already spends most of it — a fuller sentence here gets
        // trimmed mid-qualifier, which reads worse than the unqualified word it was meant to fix.
        // Scoping the claim is what matters; "why" is one hover (the tooltip) or one click
        // (复制色彩诊断) away, where it says "Input encoding: Uncharacterized" in full.
        if (Vm?.InputIsUncharacterized == true)
            guarantee += Services.Loc.T("（显示端）");
        // Same honesty for the top of the range. WYSIWYG is a claim about the last hop's colour
        // transform; when the render reaches above what this panel can show, the highlights are
        // either soft-proofed into it (D-028) or, with no headroom to proof into, clipped. Either
        // way the picture on screen is not the render above diffuse white, and the badge says so.
        if (Vm is { PreviewHighlightHeadroom: > 1f } vm && guarantee.StartsWith("WYSIWYG", StringComparison.Ordinal))
        {
            if (HighlightSoftProof.IsNeeded(vm.PreviewHighlightHeadroom, contract.ExtendedHeadroom))
                guarantee += Services.Loc.T("（高光软校样）");
            else if (!(contract.ExtendedHeadroom > 1f))
                guarantee += Services.Loc.T("（高光裁切）");
        }
        bool hasWarning = !string.IsNullOrWhiteSpace(warning);
        // Which advice applies is decided by the typed owner, not by the warning text; see
        // ColorDiagnosticBadge.
        bool colorManagementUnavailable = contract.TransformOwner == FinalTransformOwner.None;
        ColorDiagnosticText.Text = ColorDiagnosticBadge.Format(
            contract.DiagnosticName,
            contract.Encoding,
            monitor,
            guarantee,
            hasWarning,
            colorManagementUnavailable);
        ColorDiagnosticText.Foreground = hasWarning ? Brushes.OrangeRed : Brushes.Gray;
        _lastWindowsColorDiagnostics = BuildWindowsColorDiagnostics();
        ToolTip.SetTip(
            ColorDiagnosticStatus,
            ColorDiagnosticBadge.FormatTooltip(
                hasWarning,
                colorManagementUnavailable,
                ColorDiagnosticBadge.DescribeDisplaySpace(contract, monitor)));
    }

    /// <summary>
    /// What the picker hint and the histogram need to know about the screen, from whichever host
    /// is active. Windows knows the panel's peak in nits (DXGI); macOS knows only the EDR ratio,
    /// which is all its carrier is defined in — so the peak is null there and the hint speaks in
    /// headroom alone.
    /// </summary>
    private static DisplayHdrCapability? DescribeDisplayCapability(IPreviewHost preview, DisplayContract contract)
    {
        switch (preview)
        {
            case WindowsPreviewHost windows:
                return windows.DisplayDiagnostics is { ActiveColorMode: WindowsAdvancedColorMode.HighDynamicRange } display
                    ? new DisplayHdrCapability(
                        contract.SdrReferenceWhite,
                        contract.ExtendedHeadroom,
                        display.PanelMaxNits,
                        display.PanelLuminanceFailureReason)
                    : null;
            case MacOSPreviewHost mac:
                return mac.DisplayDiagnostics is { } screen && contract.ExtendedHeadroom > 1f
                    ? new DisplayHdrCapability(
                        contract.SdrReferenceWhite,
                        contract.ExtendedHeadroom,
                        PanelPeakNits: null,
                        FailureReason: null)
                    : null;
            default:
                return null;
        }
    }

    private static string DescribeMonitorName(IPreviewHost preview, DisplayContract contract) => preview switch
    {
        WindowsPreviewHost windows =>
            windows.DisplayDiagnostics?.MonitorFriendlyName ?? windows.DisplayDiagnostics?.GdiDeviceName ?? contract.DisplayId,
        MacOSPreviewHost mac => mac.DisplayDiagnostics?.LocalizedName ?? contract.DisplayId,
        _ => contract.DisplayId,
    };

    private string BuildWindowsColorDiagnostics(Exception? compositionError = null)
    {
        var text = new StringBuilder();
        text.AppendLine("OpenRevelare color diagnostics");
        text.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        text.AppendLine(Vm?.BuildRenderColorDiagnostics() ?? "Rendered frame: unavailable");

        // 报告在三平台都能出，但只有 render/input/CMM 这一半是三平台共有的。下面每一段读的都是
        // Windows presenter 的状态，在别的平台上全是默认值 —— 打印出来不是「诊断信息不足」，
        // 而是一串看着像结论的空值。说清楚它为什么不在，比留一堆 unavailable 诚实。
        if (ActivePreview is not { } preview)
        {
            text.AppendLine(
                "Presenter contract: not applicable — this platform has no native preview host. " +
                "Everything above applies on every platform.");
            return text.ToString().TrimEnd();
        }

        DisplayContract? contract = preview.CurrentContract;
        if (contract is null)
        {
            text.AppendLine("Presenter contract: unavailable");
        }
        else
        {
            text.AppendLine($"Presenter mode: {contract.DiagnosticName}");
            text.AppendLine(
                $"Surface: encoding={contract.Encoding}; finalTransformOwner={contract.TransformOwner}; " +
                $"appMonitorTransformCount={contract.RequiredApplicationMonitorTransformCount}");
            text.AppendLine(
                $"Display contract: id={contract.DisplayId}; revision={contract.Revision}; " +
                $"SDRWhite={contract.SdrReferenceWhite.ToString("R", CultureInfo.InvariantCulture)} nits; " +
                $"referenceWhiteScale={contract.ReferenceWhiteScale.ToString("R", CultureInfo.InvariantCulture)}; " +
                $"headroom={contract.ExtendedHeadroom.ToString("R", CultureInfo.InvariantCulture)}");
            text.AppendLine(contract.DeviceProfile is { } profile
                ? $"Monitor profile: {profile.Description} [{profile.Identity.Sha256Hex}]"
                : "Monitor profile: owned by system or unavailable");
            bool effectiveWysiwyg = PresentationGuarantee.IsEffective(
                contract,
                preview.IsPresenterAvailable);
            text.AppendLine($"Contract WYSIWYG-capable: {contract.IsWysiwygGuaranteed}");
            text.AppendLine($"Native presenter available: {preview.IsPresenterAvailable}");
            text.AppendLine($"Effective WYSIWYG guaranteed: {effectiveWysiwyg}");
            if (!string.IsNullOrWhiteSpace(contract.VisibleWarning))
                text.AppendLine($"Fallback warning: {contract.VisibleWarning}");
        }

        string platform = preview.DescribePlatformDiagnostics();
        if (platform.Length > 0) text.AppendLine(platform);

        if (!string.IsNullOrWhiteSpace(preview.LastPresentationError))
            text.AppendLine($"Last presentation error: {preview.LastPresentationError}");
        if (compositionError is not null)
            text.AppendLine($"Composition error: {compositionError.GetType().Name}: {compositionError.Message}");
        return text.ToString().TrimEnd();
    }

    private async void OnCopyColorDiagnosticsClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        _lastWindowsColorDiagnostics = BuildWindowsColorDiagnostics();
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_lastWindowsColorDiagnostics);
        if (Vm is { } vm) vm.StatusText = "色彩诊断已复制";
    }

    private void StopWindowsPresentation()
    {
        if (ActivePreview is not { } preview) return;
        lock (_windowsPresentationGate)
        {
            _windowsPresentationClosed = true;
            _pendingWindowsPresentation = null;
        }
        preview.Dispose();
    }

    private readonly SoftProofCache _frameSoftProof = new();
    private readonly SoftProofCache _patchSoftProof = new();

    private sealed record WindowsPresentationSnapshot(
        long Epoch,
        MainViewModel ViewModel,
        DisplayContract Contract,
        PresentationPixelSize ViewportSize,
        PresentationScene Background,
        IReadOnlyList<PresentationOverlay> Overlays,
        IReadOnlyList<PresentationRasterPrimitive> Primitives);
}
