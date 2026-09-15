using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.Win32;
using OpenRevelare.Presentation.Win32.Native;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// Windows-only Avalonia airspace host for the color-critical native presenter.
///
/// <para>
/// The HWND returned by <see cref="NativeControlHost"/>'s base implementation remains Avalonia's
/// attachable container. The D3D presenter creates its own child beneath that container; display
/// probing deliberately uses this color-critical surface HWND so the contract and adapter follow
/// the preview rectangle when a top-level window spans monitors.
/// </para>
///
/// <para>
/// The presenter child returns <c>HTTRANSPARENT</c>. The base NativeControlHost container remains
/// an HWND airspace island, so an installed, lifetime-paired WndProc bridge forwards its mouse and
/// wheel messages to Avalonia's real top-level HWND. Runtime creation fails closed if that bridge
/// cannot be installed.
/// </para>
/// </summary>
public sealed class WindowsPreviewHost : NativeControlHost, IPreviewHost
{
    public const string PointerInputLimitation =
        "Pointer input is routable only while the NativeControlHost container WndProc bridge is installed.";

    public static readonly DirectProperty<WindowsPreviewHost, DisplayContract?> CurrentContractProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, DisplayContract?>(
            nameof(CurrentContract), host => host.CurrentContract);

    public static readonly DirectProperty<WindowsPreviewHost, WindowsDisplayDiagnostics?>
        DisplayDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, WindowsDisplayDiagnostics?>(
            nameof(DisplayDiagnostics), host => host.DisplayDiagnostics);

    public static readonly DirectProperty<WindowsPreviewHost, WindowsNativePresenterDiagnostics?>
        PresenterDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, WindowsNativePresenterDiagnostics?>(
            nameof(PresenterDiagnostics), host => host.PresenterDiagnostics);

    public static readonly DirectProperty<WindowsPreviewHost, string?> DiagnosticNameProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, string?>(
            nameof(DiagnosticName), host => host.DiagnosticName);

    public static readonly DirectProperty<WindowsPreviewHost, string?> VisibleWarningProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, string?>(
            nameof(VisibleWarning), host => host.VisibleWarning);

    public static readonly DirectProperty<WindowsPreviewHost, string?> LastPresentationErrorProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, string?>(
            nameof(LastPresentationError), host => host.LastPresentationError);

    public static readonly DirectProperty<WindowsPreviewHost, bool> IsWindowsPresentationActiveProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, bool>(
            nameof(IsWindowsPresentationActive), host => host.IsWindowsPresentationActive);

    public static readonly DirectProperty<WindowsPreviewHost, bool> IsPresenterAvailableProperty =
        AvaloniaProperty.RegisterDirect<WindowsPreviewHost, bool>(
            nameof(IsPresenterAvailable), host => host.IsPresenterAvailable);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly object _lifecycleGate = new();
    private readonly IWindowsPreviewHostBackendFactory _backendFactory;
    private readonly IPreviewDispatcher _dispatcher;
    private readonly WindowsPreviewAirspaceState _airspaceState;
    private IColorManagementEngine? _colorManagement;
    private Runtime? _runtime;
    private IPlatformHandle? _containerHandle;
    private TopLevel? _hookedTopLevel;
    private DispatcherTimer? _refreshTimer;
    private bool _updateQueued;
    private bool _metricsPending;
    private bool _refreshPending;
    private bool _disposed;

    private DisplayContract? _currentContract;
    private WindowsDisplayDiagnostics? _displayDiagnostics;
    private WindowsNativePresenterDiagnostics? _presenterDiagnostics;
    private string? _diagnosticName;
    private string? _visibleWarning;
    private string? _lastPresentationError;
    private bool _isWindowsPresentationActive;
    private bool _isPresenterAvailable;

    public WindowsPreviewHost()
        : this(
            WindowsPreviewHostBackendFactory.Instance,
            InlinePreviewDispatcher.Instance,
            WindowsPreviewNativeAirspace.Instance)
    {
    }

    internal WindowsPreviewHost(
        IWindowsPreviewHostBackendFactory backendFactory,
        IPreviewDispatcher dispatcher)
        : this(backendFactory, dispatcher, WindowsPreviewNativeAirspace.Instance)
    {
    }

    internal WindowsPreviewHost(
        IWindowsPreviewHostBackendFactory backendFactory,
        IPreviewDispatcher dispatcher,
        IWindowsPreviewNativeVisibility nativeVisibility)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(nativeVisibility);
        _backendFactory = backendFactory;
        _dispatcher = dispatcher;
        _airspaceState = new WindowsPreviewAirspaceState(nativeVisibility);

        // Forwarded top-level messages must hit-test through this visual to the shared viewport
        // controller behind it; the HWND airspace itself is handled by WindowsPreviewInputBridge.
        Focusable = false;
        IsHitTestVisible = false;

        // NativeControlHost only materialises its native container while effectively visible.
        // Off Windows this host has no runtime and would otherwise leave an empty native view
        // sitting over the viewport, intercepting pointer input and hiding the managed fallback.
        if (!OperatingSystem.IsWindows()) IsVisible = false;
    }

    public DisplayContract? CurrentContract
    {
        get => _currentContract;
        private set => SetAndRaise(CurrentContractProperty, ref _currentContract, value);
    }

    public WindowsDisplayDiagnostics? DisplayDiagnostics
    {
        get => _displayDiagnostics;
        private set => SetAndRaise(DisplayDiagnosticsProperty, ref _displayDiagnostics, value);
    }

    public WindowsNativePresenterDiagnostics? PresenterDiagnostics
    {
        get => _presenterDiagnostics;
        private set => SetAndRaise(PresenterDiagnosticsProperty, ref _presenterDiagnostics, value);
    }

    public string? DiagnosticName
    {
        get => _diagnosticName;
        private set => SetAndRaise(DiagnosticNameProperty, ref _diagnosticName, value);
    }

    public string? VisibleWarning
    {
        get => _visibleWarning;
        private set => SetAndRaise(VisibleWarningProperty, ref _visibleWarning, value);
    }

    public string? LastPresentationError
    {
        get => _lastPresentationError;
        private set => SetAndRaise(LastPresentationErrorProperty, ref _lastPresentationError, value);
    }

    public bool IsWindowsPresentationActive
    {
        get => _isWindowsPresentationActive;
        private set => SetAndRaise(
            IsWindowsPresentationActiveProperty, ref _isWindowsPresentationActive, value);
    }

    public bool IsPresenterAvailable
    {
        get => _isPresenterAvailable;
        private set => SetAndRaise(IsPresenterAvailableProperty, ref _isPresenterAvailable, value);
    }

    /// <summary>True only while the container WndProc bridge is successfully installed.</summary>
    public bool CanRoutePointerInputToAvalonia
    {
        get
        {
            lock (_lifecycleGate) return _runtime?.InputBridge.IsInstalled == true;
        }
    }

    public event EventHandler<DisplayContract>? ContractChanged;
    public event EventHandler<PreviewPresentationFailedEventArgs>? PresentationFailed;
    public event EventHandler? PresentationRecoveryRequested;

    public void ConfigureColorManagement(IColorManagementEngine colorManagement)
    {
        ArgumentNullException.ThrowIfNull(colorManagement);
        Dispatcher.UIThread.VerifyAccess();

        IPlatformHandle? container;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_colorManagement is not null &&
                !ReferenceEquals(_colorManagement, colorManagement))
            {
                throw new InvalidOperationException(
                    "The Windows preview host cannot replace its application-level color-management engine.");
            }
            _colorManagement = colorManagement;
            container = _containerHandle;
        }

        if (OperatingSystem.IsWindows() && container is not null)
            EnsureWindowsRuntime(container);
    }

    /// <summary>
    /// Thread-safe, one-slot presentation API. A stale or mismatched frame is rejected
    /// synchronously; valid producers replace any older frame not yet dispatched to the UI thread.
    /// The current contract is checked again immediately before the native call.
    /// </summary>
    public void PresentNewest(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native preview host is Windows-only.");

        Runtime runtime;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            runtime = _runtime ?? throw new InvalidOperationException(
                "The Windows preview host is not attached to an Avalonia top level.");
        }

        runtime.Mailbox.Enqueue(frame);
    }

    /// <summary>
    /// Requests an asynchronous UI-thread probe. The host already invokes this for movement,
    /// scaling, bounds changes, and its lightweight polling timer.
    /// </summary>
    public void RefreshDisplayState()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Dispatcher.UIThread.CheckAccess())
            RequestUpdate(metrics: false);
        else
            Dispatcher.UIThread.Post(() => RequestUpdate(metrics: false), DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess())
            DisposeOnUiThread();
        else
            Dispatcher.UIThread.Invoke(DisposeOnUiThread, DispatcherPriority.Send);
        GC.SuppressFinalize(this);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle container = base.CreateNativeControlCore(parent);
        if (!OperatingSystem.IsWindows() || _disposed) return container;

        try
        {
            RequireHwnd(container, "Avalonia NativeControlHost container");
            _containerHandle = container;
            SetPresenterAvailability(container.Handle, presenterAvailable: false);
            EnsureWindowsRuntime(container);
            return container;
        }
        catch
        {
            StopWindowsRuntime();
            _containerHandle = null;
            base.DestroyNativeControlCore(container);
            throw;
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            if (_containerHandle?.Handle == control.Handle)
            {
                StopWindowsRuntime();
                _containerHandle = null;
            }
        }
        finally
        {
            base.DestroyNativeControlCore(control);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // NativeControlHost may preserve its base container across a quick detach/reparent. In
        // that case CreateNativeControlCore is not called again, so rebuild our child explicitly.
        if (OperatingSystem.IsWindows() && !_disposed && _containerHandle is not null)
            EnsureWindowsRuntime(_containerHandle);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Dispose D3D/environment state while the base container is still a valid HWND. Avalonia
        // may defer destroying that container to support reparenting, but none of our native state
        // survives this detach.
        try
        {
            StopWindowsRuntime();
        }
        finally
        {
            base.OnDetachedFromVisualTree(e);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!OperatingSystem.IsWindows() || _disposed) return;

        // NativeControlHost can run ShowInBounds after any visibility/layout-affecting property
        // (including an ancestor's effective visibility). Re-apply our presenter gate after the
        // base handler so an unavailable native child cannot re-occlude the managed fallback.
        ReapplyAirspaceAfterFrameworkUpdate();
        if (change.Property == BoundsProperty) RequestUpdate(metrics: true);
    }

    private void EnsureWindowsRuntime(IPlatformHandle container)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;

        lock (_lifecycleGate)
        {
            if (_runtime is not null) return;
        }

        IColorManagementEngine? colorManagement;
        lock (_lifecycleGate) colorManagement = _colorManagement;
        if (colorManagement is null)
        {
            SetPresenterAvailability(container.Handle, presenterAvailable: false);
            return;
        }

        TopLevel topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException(
            "WindowsPreviewHost must be attached before its native presenter is created.");
        IPlatformHandle topLevelHandle = topLevel.TryGetPlatformHandle() ??
            throw new InvalidOperationException("The Avalonia top level has no platform handle.");
        nint topLevelHwnd = RequireHwnd(topLevelHandle, "Avalonia top level");
        nint containerHwnd = RequireHwnd(container, "Avalonia NativeControlHost container");
        double scale = topLevel.RenderScaling;
        PresentationPixelSize size = PreviewPhysicalSize.FromBounds(Bounds.Size, scale);

        // Install before creating the presenter child. A bridge failure leaves no active native
        // preview rectangle that could silently consume the viewport's mouse/wheel input.
        WindowsPreviewInputBridge inputBridge =
            WindowsPreviewInputBridge.Install(containerHwnd, topLevelHwnd);
        WindowsPreviewDisplayChangeBridge displayChangeBridge;
        IWindowsPreviewHostBackend backend;
        try
        {
            displayChangeBridge = WindowsPreviewDisplayChangeBridge.Install(
                topLevelHwnd,
                OnNativeDisplayRefreshRequested);
            try
            {
                backend = _backendFactory.Create(
                    containerHwnd,
                    topLevelHwnd,
                    size,
                    scale,
                    colorManagement);
            }
            catch
            {
                displayChangeBridge.Dispose();
                throw;
            }
        }
        catch
        {
            inputBridge.Dispose();
            throw;
        }
        var mailbox = new PreviewFrameMailbox(backend, _dispatcher, ReportPresentationFailure);
        var runtime = new Runtime(
            backend,
            mailbox,
            inputBridge,
            displayChangeBridge,
            topLevel,
            containerHwnd,
            size,
            scale);
        backend.ContractChanged += OnBackendContractChanged;
        backend.PresenterRecoveryRequested += OnBackendPresenterRecoveryRequested;

        try
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_runtime is not null)
                    throw new InvalidOperationException("A Windows preview runtime already exists.");
                _runtime = runtime;
            }

            StartRefreshHooks(topLevel);
            ApplyStatus(runtime);
        }
        catch
        {
            StopRefreshHooks();
            try
            {
                displayChangeBridge.Dispose();
            }
            finally
            {
                try
                {
                    inputBridge.Dispose();
                }
                finally
                {
                    backend.ContractChanged -= OnBackendContractChanged;
                    backend.PresenterRecoveryRequested -= OnBackendPresenterRecoveryRequested;
                    mailbox.Dispose();
                    backend.Dispose();
                    lock (_lifecycleGate)
                    {
                        if (ReferenceEquals(_runtime, runtime)) _runtime = null;
                    }
                }
            }
            throw;
        }
    }

    private void StartRefreshHooks(TopLevel topLevel)
    {
        StopRefreshHooks();
        _hookedTopLevel = topLevel;
        topLevel.ScalingChanged += OnTopLevelScalingChanged;
        if (topLevel is WindowBase window)
            window.PositionChanged += OnTopLevelPositionChanged;

        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    private void StopRefreshHooks()
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer = null;
        }

        if (_hookedTopLevel is not null)
        {
            _hookedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
            if (_hookedTopLevel is WindowBase window)
                window.PositionChanged -= OnTopLevelPositionChanged;
            _hookedTopLevel = null;
        }
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e) => RequestUpdate(metrics: true);

    private void OnTopLevelPositionChanged(object? sender, PixelPointEventArgs e) =>
        RequestUpdate(metrics: false);

    private void OnRefreshTimerTick(object? sender, EventArgs e) => RequestUpdate(metrics: false);

    private void OnNativeDisplayRefreshRequested(WindowsPreviewDisplayRefreshKind refreshKind)
    {
        bool metrics = (refreshKind & WindowsPreviewDisplayRefreshKind.Metrics) != 0;
        if (Dispatcher.UIThread.CheckAccess())
        {
            RequestUpdate(metrics);
            return;
        }

        Dispatcher.UIThread.Post(() => RequestUpdate(metrics), DispatcherPriority.Background);
    }

    private void RequestUpdate(bool metrics)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;

        lock (_lifecycleGate)
        {
            if (_runtime is null) return;
        }

        _refreshPending = true;
        _metricsPending |= metrics;
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.UIThread.Post(DrainUpdate, DispatcherPriority.Background);
    }

    private void DrainUpdate()
    {
        Dispatcher.UIThread.VerifyAccess();
        _updateQueued = false;
        bool resize = _metricsPending;
        bool refresh = _refreshPending;
        _metricsPending = false;
        _refreshPending = false;

        Runtime? runtime;
        lock (_lifecycleGate) runtime = _runtime;
        if (runtime is null || _disposed) return;

        try
        {
            if (resize)
            {
                double scale = runtime.TopLevel.RenderScaling;
                PresentationPixelSize size = PreviewPhysicalSize.FromBounds(Bounds.Size, scale);
                if (size != runtime.Size || scale != runtime.Scale)
                {
                    runtime.Backend.Resize(size, scale);
                    runtime.Size = size;
                    runtime.Scale = scale;
                    // The backend drops frames of any other size (see its Present), so a frame
                    // the window composed before this resize will not be shown; ask for one that
                    // fits. Same event the window already answers with a recomposition.
                    PresentationRecoveryRequested?.Invoke(this, EventArgs.Empty);
                }
            }

            if (refresh) runtime.Backend.Refresh();
            ApplyStatus(runtime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportPresentationFailure(ex);
            ApplyStatusIfAlive(runtime);
        }
    }

    private void OnBackendContractChanged(object? sender, DisplayContract contract)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => OnBackendContractChanged(sender, contract),
                DispatcherPriority.Background);
            return;
        }

        Runtime? runtime;
        lock (_lifecycleGate) runtime = _runtime;
        if (runtime is null || !ReferenceEquals(sender, runtime.Backend)) return;
        ApplyStatus(runtime);
        ContractChanged?.Invoke(this, contract);
    }

    private void OnBackendPresenterRecoveryRequested(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => OnBackendPresenterRecoveryRequested(sender, e),
                DispatcherPriority.Background);
            return;
        }

        Runtime? runtime;
        lock (_lifecycleGate) runtime = _runtime;
        if (runtime is null || !ReferenceEquals(sender, runtime.Backend)) return;
        LastPresentationError = null;
        ApplyStatus(runtime);
        PresentationRecoveryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStatusIfAlive(Runtime runtime)
    {
        lock (_lifecycleGate)
        {
            if (!ReferenceEquals(_runtime, runtime)) return;
        }
        ApplyStatus(runtime);
    }

    private void ApplyStatus(Runtime runtime)
    {
        DisplayContract contract = runtime.Backend.Current;
        bool presenterAvailable = runtime.Backend.IsPresenterAvailable;
        SetPresenterAvailability(runtime.ContainerHwnd, presenterAvailable);
        CurrentContract = contract;
        DisplayDiagnostics = runtime.Backend.DisplayDiagnostics;
        PresenterDiagnostics = runtime.Backend.PresenterDiagnostics;
        DiagnosticName = contract.DiagnosticName;
        VisibleWarning = contract.VisibleWarning ?? runtime.Backend.PresenterFailure;
        IsPresenterAvailable = presenterAvailable;
        IsWindowsPresentationActive = presenterAvailable;
    }

    private void ReportPresentationFailure(Exception error)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => ReportPresentationFailure(error),
                DispatcherPriority.Background);
            return;
        }

        LastPresentationError = $"{error.GetType().Name}: {error.Message}";
        Runtime? runtime;
        lock (_lifecycleGate) runtime = _runtime;
        if (runtime is not null)
        {
            ApplyStatusIfAlive(runtime);
            // A failed presenter was invalidated by the backend. Force an immediate same-contract
            // rebuild attempt (and a metrics retry after a failed Resize); the one-second timer
            // remains the backstop while the DLL/device is unavailable.
            RequestUpdate(metrics: true);
        }
        PresentationFailed?.Invoke(this, new PreviewPresentationFailedEventArgs(error));
    }

    private void StopWindowsRuntime()
    {
        Dispatcher.UIThread.VerifyAccess();
        StopRefreshHooks();
        _updateQueued = false;
        _metricsPending = false;
        _refreshPending = false;

        Runtime? runtime;
        lock (_lifecycleGate)
        {
            runtime = _runtime;
            _runtime = null;
        }

        if (_containerHandle is { } container && OperatingSystem.IsWindows())
            SetPresenterAvailability(container.Handle, presenterAvailable: false);

        try
        {
            if (runtime is not null)
            {
                // Restore both exact WndProc chains while their HWNDs are still alive, before
                // stopping presenter state or allowing NativeControlHost to destroy its child.
                try
                {
                    runtime.DisplayChangeBridge.Dispose();
                }
                finally
                {
                    try
                    {
                        runtime.InputBridge.Dispose();
                    }
                    finally
                    {
                        runtime.Backend.ContractChanged -= OnBackendContractChanged;
                        runtime.Backend.PresenterRecoveryRequested -= OnBackendPresenterRecoveryRequested;
                        runtime.Mailbox.Dispose();
                        runtime.Backend.Dispose();
                    }
                }
            }
        }
        finally
        {
            CurrentContract = null;
            DisplayDiagnostics = null;
            PresenterDiagnostics = null;
            DiagnosticName = null;
            VisibleWarning = null;
            IsPresenterAvailable = false;
            IsWindowsPresentationActive = false;
        }
    }

    /// <summary>The Windows-only lines of the copied colour diagnostics (probe, native presenter).</summary>
    public string DescribePlatformDiagnostics()
    {
        var text = new System.Text.StringBuilder();
        if (DisplayDiagnostics is { } display)
        {
            text.AppendLine(
                $"Display probe: api={display.ProbeApi}; GDI={display.GdiDeviceName}; " +
                $"path={display.MonitorDevicePath}; name={display.MonitorFriendlyName}");
            text.AppendLine(
                $"Advanced Color: active={display.AdvancedColorActive}; mode={display.ActiveColorMode}; " +
                $"encoding={display.ColorEncoding}; bitsPerChannel={display.BitsPerColorChannel}; " +
                $"rawFlags={display.AdvancedColorRawFlags}");
            text.AppendLine(
                $"SDR white: raw={display.SdrWhiteRaw}; nits={display.SdrWhiteNits}; " +
                $"failure={display.SdrWhiteFailureReason ?? "none"}");
            text.AppendLine(
                $"Panel luminance: min={display.PanelMinNits}; max={display.PanelMaxNits}; " +
                $"maxFullFrame={display.PanelMaxFullFrameNits}; failure={display.PanelLuminanceFailureReason ?? "none"}");
            text.AppendLine(
                $"Display profile probe: status={display.ProfileStatus}; scope={display.ProfileScope}; " +
                $"file={display.ProfileFileName}; sha256={display.ProfileSha256}");
            text.AppendLine(
                $"Display fallback: {display.FallbackReason ?? "none"}; " +
                $"contractCapableWYSIWYG={display.WysiwygGuaranteed}; " +
                $"presenterAvailable={IsPresenterAvailable}; " +
                $"effectiveWYSIWYG={PresentationGuarantee.IsEffective(CurrentContract, IsPresenterAvailable)}; " +
                $"explicitRefresh={display.RequiresExplicitRefresh}");
        }

        if (PresenterDiagnostics is { } native)
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

        text.AppendLine($"Pointer routing: {PointerInputLimitation}");
        return text.ToString().TrimEnd();
    }

    private void DisposeOnUiThread()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        StopWindowsRuntime();
    }

    private static nint RequireHwnd(IPlatformHandle handle, string role)
    {
        if (handle.Handle == nint.Zero)
            throw new InvalidOperationException($"{role} HWND is null.");
        if (!string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                $"{role} is '{handle.HandleDescriptor}', not a Win32 HWND.");
        }
        return handle.Handle;
    }

    private void SetPresenterAvailability(nint containerHwnd, bool presenterAvailable)
    {
        _airspaceState.SetPresenterAvailable(presenterAvailable);

        // NativeControlHost's attachment chooses HideWithSize/ShowInBounds from IsVisible.
        // Updating it immediately closes the interval before the next AfterRender pass; the
        // direct container HWND call is only a second line of defense.
        if (IsVisible != presenterAvailable) IsVisible = presenterAvailable;
        TryUpdateNativeControlPosition();
        _airspaceState.Reapply(containerHwnd, IsEffectivelyVisible);
    }

    private void ReapplyAirspaceAfterFrameworkUpdate()
    {
        IPlatformHandle? container = _containerHandle;
        if (container is null) return;

        if (!_airspaceState.PresenterAvailable && IsVisible)
        {
            IsVisible = false;
            return;
        }

        TryUpdateNativeControlPosition();
        _airspaceState.Reapply(container.Handle, IsEffectivelyVisible);
    }

    private sealed class Runtime
    {
        internal Runtime(
            IWindowsPreviewHostBackend backend,
            PreviewFrameMailbox mailbox,
            WindowsPreviewInputBridge inputBridge,
            WindowsPreviewDisplayChangeBridge displayChangeBridge,
            TopLevel topLevel,
            nint containerHwnd,
            PresentationPixelSize size,
            double scale)
        {
            Backend = backend;
            Mailbox = mailbox;
            InputBridge = inputBridge;
            DisplayChangeBridge = displayChangeBridge;
            TopLevel = topLevel;
            ContainerHwnd = containerHwnd;
            Size = size;
            Scale = scale;
        }

        internal IWindowsPreviewHostBackend Backend { get; }
        internal PreviewFrameMailbox Mailbox { get; }
        internal WindowsPreviewInputBridge InputBridge { get; }
        internal WindowsPreviewDisplayChangeBridge DisplayChangeBridge { get; }
        internal TopLevel TopLevel { get; }
        internal nint ContainerHwnd { get; }
        internal PresentationPixelSize Size { get; set; }
        internal double Scale { get; set; }
    }

}

internal interface IWindowsPreviewNativeVisibility
{
    void SetVisible(nint hwnd, bool visible);
}

internal sealed class WindowsPreviewNativeAirspace : IWindowsPreviewNativeVisibility
{
    private const int SwHide = 0;
    private const int SwShowNoActivate = 8;

    internal static WindowsPreviewNativeAirspace Instance { get; } = new();

    private WindowsPreviewNativeAirspace() { }

    internal static bool ShouldBeVisible(bool presenterAvailable) => presenterAvailable;

    public void SetVisible(nint hwnd, bool visible)
    {
        if (!OperatingSystem.IsWindows() || hwnd == nint.Zero) return;
        _ = ShowWindow(hwnd, ShouldBeVisible(visible) ? SwShowNoActivate : SwHide);
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);
}

internal sealed class WindowsPreviewAirspaceState(IWindowsPreviewNativeVisibility nativeVisibility)
{
    private readonly IWindowsPreviewNativeVisibility _nativeVisibility =
        nativeVisibility ?? throw new ArgumentNullException(nameof(nativeVisibility));

    internal bool PresenterAvailable { get; private set; }

    internal void SetPresenterAvailable(bool presenterAvailable) =>
        PresenterAvailable = presenterAvailable;

    internal bool ShouldExposeNative(bool effectivelyVisible) =>
        PresenterAvailable && effectivelyVisible;

    internal void Reapply(nint containerHwnd, bool effectivelyVisible) =>
        _nativeVisibility.SetVisible(
            containerHwnd,
            ShouldExposeNative(effectivelyVisible));
}
