using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Presentation;
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
    private bool _windowsPresentationWorkerActive;
    private bool _windowsPresentationClosed;
    private long _windowsPresentationEpoch;
    private string _lastWindowsColorDiagnostics = "Windows color presentation has not initialized.";

    private void InitializeWindowsPresentation()
    {
        bool enabled = OperatingSystem.IsWindows();
        WindowsPreview.IsVisible = enabled;
        ColorDiagnosticStatus.IsVisible = enabled;
        if (!enabled) return;

        WindowsPreview.ContractChanged += OnWindowsDisplayContractChanged;
        WindowsPreview.PresentationFailed += OnWindowsPresentationFailed;
        WindowsPreview.PresentationRecoveryRequested += OnWindowsPresenterRecoveryRequested;
        WindowsPreview.PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowsPreviewHost.CurrentContractProperty ||
                args.Property == WindowsPreviewHost.DisplayDiagnosticsProperty ||
                args.Property == WindowsPreviewHost.PresenterDiagnosticsProperty ||
                args.Property == WindowsPreviewHost.VisibleWarningProperty ||
                args.Property == WindowsPreviewHost.LastPresentationErrorProperty ||
                args.Property == WindowsPreviewHost.IsPresenterAvailableProperty)
            {
                UpdateWindowsColorStatus();
            }
        };
        Opened += (_, _) => QueueWindowsPresentation();
    }

    private void OnWindowsPresentationViewModelPropertyChanged(
        MainViewModel viewModel,
        string? propertyName)
    {
        if (!OperatingSystem.IsWindows()) return;
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
        WindowsPreviewPresentationFailedEventArgs args)
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
        if (!OperatingSystem.IsWindows() || _windowsPresentationClosed) return;
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
                PresentationScene finalScene = CpuPresentationCompositor.Compose(
                    snapshot.Background,
                    snapshot.ViewportSize,
                    snapshot.Overlays,
                    snapshot.Primitives);
                PresentationBuffer buffer = snapshot.ViewModel.BuildPresentationBuffer(
                    finalScene,
                    snapshot.Contract);

                Dispatcher.UIThread.Post(
                    () => PublishWindowsPresentation(snapshot.Epoch, buffer),
                    DispatcherPriority.Render);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(
                    () => ReportWindowsCompositionFailure(snapshot.Epoch, ex),
                    DispatcherPriority.Background);
            }
        }
    }

    private void PublishWindowsPresentation(long epoch, PresentationBuffer buffer)
    {
        if (_windowsPresentationClosed || epoch != _windowsPresentationEpoch) return;
        try
        {
            WindowsPreview.PresentNewest(buffer);
            UpdateWindowsColorStatus();
        }
        catch (PresentationContractException)
        {
            QueueWindowsPresentation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportWindowsCompositionFailure(epoch, ex);
        }
    }

    private void ReportWindowsCompositionFailure(long epoch, Exception error)
    {
        if (_windowsPresentationClosed || epoch != _windowsPresentationEpoch) return;
        _lastWindowsColorDiagnostics = BuildWindowsColorDiagnostics(error);
        ColorDiagnosticText.Text = $"Windows preview unavailable · {error.Message}";
        ColorDiagnosticText.Foreground = Brushes.OrangeRed;
        ToolTip.SetTip(ColorDiagnosticStatus, _lastWindowsColorDiagnostics);
    }

    private WindowsPresentationSnapshot? CaptureWindowsPresentationSnapshot()
    {
        if (Vm is not { } vm || WindowsPreview.CurrentContract is not { } contract) return null;
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
        if (vm.PreviewScene is { } baseScene)
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
                    patch.Scene,
                    RoundUnclipped(geometry.NormalizedToPhysical(normalized))));
            }

            if (vm.ShowSprocketMask && vm.SprocketMaskScene is { } sprocket)
                overlays.Add(new PresentationOverlay(sprocket, imageDestination));
            if (vm.ShowClipping && vm.ClippingScene is { } clipping)
                overlays.Add(new PresentationOverlay(clipping, imageDestination));

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
        PreviewRect image = geometry.DisplayedImagePhysical;
        PreviewRect frame = geometry.NormalizedToPhysical(
            new PreviewRect(crop.X, crop.Y, crop.W, crop.H));

        PremultipliedLinearRgba dim = SrgbPremultiplied("#99101214");
        AddSolid(primitives, image.X, image.Y, image.Width, frame.Y - image.Y, dim);
        AddSolid(primitives, image.X, frame.Bottom, image.Width, image.Bottom - frame.Bottom, dim);
        AddSolid(primitives, image.X, frame.Y, frame.X - image.X, frame.Height, dim);
        AddSolid(primitives, frame.Right, frame.Y, image.Right - frame.Right, frame.Height, dim);

        double frameStroke = 1.5d * renderScaling;
        primitives.Add(new PresentationRectOutline(
            frame,
            frameStroke,
            SrgbPremultiplied("#E6E9EC")));

        PremultipliedLinearRgba guide = SrgbPremultiplied("#66E6E9EC");
        double guideStroke = renderScaling;
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
            primitives.Add(new PresentationSolidRect(bounds, SrgbPremultiplied("#F2F5F7")));
            primitives.Add(new PresentationRectOutline(
                bounds,
                renderScaling,
                SrgbPremultiplied("#1C1E20")));
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
            primitives.Add(new PresentationSolidRect(bounds, SrgbPremultiplied("#22E6E9EC")));
            primitives.Add(new PresentationRectOutline(
                bounds,
                1.5d * renderScaling,
                SrgbPremultiplied("#E6E9EC")));
        }

        if (SelLine.IsVisible)
        {
            primitives.Add(new PresentationLine(
                TransformOverlayPoint(SelLine.StartPoint, renderScaling),
                TransformOverlayPoint(SelLine.EndPoint, renderScaling),
                2d * renderScaling,
                SrgbPremultiplied("#FF5A50")));
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

    private static PremultipliedLinearRgba SrgbPremultiplied(string value)
    {
        Color color = Color.Parse(value);
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

    private void UpdateWindowsColorStatus()
    {
        if (!OperatingSystem.IsWindows()) return;
        DisplayContract? contract = WindowsPreview.CurrentContract;
        if (contract is null)
        {
            ColorDiagnosticText.Text = "Windows preview · probing display contract…";
            ColorDiagnosticText.Foreground = Brushes.Orange;
            return;
        }

        WindowsDisplayDiagnostics? display = WindowsPreview.DisplayDiagnostics;
        string monitor = display?.MonitorFriendlyName ?? display?.GdiDeviceName ?? contract.DisplayId;
        string warning = WindowsPreview.VisibleWarning ?? WindowsPreview.LastPresentationError ?? string.Empty;
        string guarantee = WindowsPresentationGuarantee.IsEffective(
            contract,
            WindowsPreview.IsPresenterAvailable)
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
        ColorDiagnosticText.Text = string.IsNullOrWhiteSpace(warning)
            ? $"{contract.DiagnosticName} · {contract.Encoding} · {monitor} · {guarantee}"
            : $"{contract.DiagnosticName} · {contract.Encoding} · WARNING: {warning}";
        ColorDiagnosticText.Foreground = string.IsNullOrWhiteSpace(warning)
            ? Brushes.Gray
            : Brushes.OrangeRed;
        _lastWindowsColorDiagnostics = BuildWindowsColorDiagnostics();
        ToolTip.SetTip(ColorDiagnosticStatus, _lastWindowsColorDiagnostics);
    }

    private string BuildWindowsColorDiagnostics(Exception? compositionError = null)
    {
        var text = new StringBuilder();
        text.AppendLine("OpenRevelare color diagnostics");
        text.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        text.AppendLine(Vm?.BuildRenderColorDiagnostics() ?? "Rendered frame: unavailable");

        DisplayContract? contract = WindowsPreview.CurrentContract;
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
            bool effectiveWysiwyg = WindowsPresentationGuarantee.IsEffective(
                contract,
                WindowsPreview.IsPresenterAvailable);
            text.AppendLine($"Contract WYSIWYG-capable: {contract.IsWysiwygGuaranteed}");
            text.AppendLine($"Native presenter available: {WindowsPreview.IsPresenterAvailable}");
            text.AppendLine($"Effective WYSIWYG guaranteed: {effectiveWysiwyg}");
            if (!string.IsNullOrWhiteSpace(contract.VisibleWarning))
                text.AppendLine($"Fallback warning: {contract.VisibleWarning}");
        }

        if (WindowsPreview.DisplayDiagnostics is { } display)
        {
            text.AppendLine(
                $"Display probe: api={display.ProbeApi}; GDI={display.GdiDeviceName}; " +
                $"path={display.MonitorDevicePath}; name={display.MonitorFriendlyName}");
            text.AppendLine(
                $"Advanced Color: active={display.AdvancedColorActive}; mode={display.ActiveColorMode}; " +
                $"encoding={display.ColorEncoding}; bitsPerChannel={display.BitsPerColorChannel}; " +
                $"rawFlags={display.AdvancedColorRawFlags}");
            text.AppendLine(
                $"Display profile probe: status={display.ProfileStatus}; scope={display.ProfileScope}; " +
                $"file={display.ProfileFileName}; sha256={display.ProfileSha256}");
            text.AppendLine(
                $"Display fallback: {display.FallbackReason ?? "none"}; " +
                $"contractCapableWYSIWYG={display.WysiwygGuaranteed}; " +
                $"presenterAvailable={WindowsPreview.IsPresenterAvailable}; " +
                $"effectiveWYSIWYG={WindowsPresentationGuarantee.IsEffective(contract, WindowsPreview.IsPresenterAvailable)}; " +
                $"explicitRefresh={display.RequiresExplicitRefresh}");
        }

        if (WindowsPreview.PresenterDiagnostics is { } native)
        {
            text.AppendLine(
                $"Native presenter: ABI={native.AbiVersion}; mode={native.Mode}; " +
                $"size={native.Size.Width}x{native.Size.Height}; DXGI format=0x{native.DxgiFormat:X}; " +
                $"colorSpace=0x{native.DxgiColorSpace:X}; colorSpaceSet={native.ColorSpaceWasSet}");
            text.AppendLine(
                $"Native adapter: LUID={native.AdapterLuid}; featureLevel=0x{native.FeatureLevel:X}; " +
                $"WARP={native.UsingWarp}; childHwnd=0x{native.ChildHwnd:X}");
            text.AppendLine(
                $"Native presents: ok={native.SuccessfulPresentCount}; rejected={native.RejectedPresentCount}; " +
                $"lastResult={native.LastResult}; lastRevision={native.LastContractRevision}; " +
                $"lastDisplay={native.LastDisplayId}");
        }

        if (!string.IsNullOrWhiteSpace(WindowsPreview.LastPresentationError))
            text.AppendLine($"Last presentation error: {WindowsPreview.LastPresentationError}");
        if (compositionError is not null)
            text.AppendLine($"Composition error: {compositionError.GetType().Name}: {compositionError.Message}");
        text.AppendLine($"Pointer routing: {WindowsPreviewHost.PointerInputLimitation}");
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
        if (!OperatingSystem.IsWindows()) return;
        lock (_windowsPresentationGate)
        {
            _windowsPresentationClosed = true;
            _pendingWindowsPresentation = null;
        }
        WindowsPreview.Dispose();
    }

    private sealed record WindowsPresentationSnapshot(
        long Epoch,
        MainViewModel ViewModel,
        DisplayContract Contract,
        PresentationPixelSize ViewportSize,
        PresentationScene Background,
        IReadOnlyList<PresentationOverlay> Overlays,
        IReadOnlyList<PresentationRasterPrimitive> Primitives);
}
