using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.MacOS;
using OpenRevelare.Presentation.MacOS.Native;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// macOS Avalonia host for the colour-critical native presenter.
///
/// <para>
/// <see cref="NativeControlHost"/> on macOS hands back an <c>NSView</c> that lives inside
/// Avalonia's own view. The native presenter adds its <c>CAMetalLayer</c>-backed child to that
/// view; display probing uses the same view, so the contract follows the preview rectangle when
/// the window straddles two screens.
/// </para>
///
/// <para>
/// NO AIRSPACE MACHINERY. The Win32 host needs an input bridge (its child HWND is an island) and a
/// visibility gate (a bare container would occlude the fallback). Neither applies here: the
/// presenter view returns nil from hitTest so pointer events fall through to Avalonia, and an
/// unavailable presenter simply hides this control, at which point NativeControlHost releases the
/// container. Refresh is the one-second poll plus Avalonia's scaling/position events — the same
/// fallback path the Win32 host has under its message bridge.
/// </para>
/// </summary>
public sealed class MacOSPreviewHost : NativeControlHost, IPreviewHost
{
    public static readonly DirectProperty<MacOSPreviewHost, DisplayContract?> CurrentContractProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, DisplayContract?>(nameof(CurrentContract), host => host.CurrentContract);

    public static readonly DirectProperty<MacOSPreviewHost, MacOSDisplayDiagnostics?> DisplayDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, MacOSDisplayDiagnostics?>(nameof(DisplayDiagnostics), host => host.DisplayDiagnostics);

    public static readonly DirectProperty<MacOSPreviewHost, MacOSNativePresenterDiagnostics?> PresenterDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, MacOSNativePresenterDiagnostics?>(nameof(PresenterDiagnostics), host => host.PresenterDiagnostics);

    public static readonly DirectProperty<MacOSPreviewHost, string?> DiagnosticNameProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, string?>(nameof(DiagnosticName), host => host.DiagnosticName);

    public static readonly DirectProperty<MacOSPreviewHost, string?> VisibleWarningProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, string?>(nameof(VisibleWarning), host => host.VisibleWarning);

    public static readonly DirectProperty<MacOSPreviewHost, string?> LastPresentationErrorProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, string?>(nameof(LastPresentationError), host => host.LastPresentationError);

    public static readonly DirectProperty<MacOSPreviewHost, bool> IsPresenterAvailableProperty =
        AvaloniaProperty.RegisterDirect<MacOSPreviewHost, bool>(nameof(IsPresenterAvailable), host => host.IsPresenterAvailable);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly object _lifecycleGate = new();
    private readonly IMacOSPreviewHostBackendFactory _backendFactory;
    private readonly IPreviewDispatcher _dispatcher;
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
    private MacOSDisplayDiagnostics? _displayDiagnostics;
    private MacOSNativePresenterDiagnostics? _presenterDiagnostics;
    private string? _diagnosticName;
    private string? _visibleWarning;
    private string? _lastPresentationError;
    private bool _isPresenterAvailable;

    public MacOSPreviewHost()
        : this(MacOSPreviewHostBackendFactory.Instance, AvaloniaPreviewDispatcher.Instance)
    {
    }

    internal MacOSPreviewHost(IMacOSPreviewHostBackendFactory backendFactory, IPreviewDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _backendFactory = backendFactory;
        _dispatcher = dispatcher;

        Focusable = false;
        IsHitTestVisible = false;

        // Off macOS this host has no runtime; keeping it invisible means NativeControlHost never
        // creates a container that would sit over the viewport (see WindowsPreviewHost).
        if (!OperatingSystem.IsMacOS()) IsVisible = false;
    }

    public DisplayContract? CurrentContract
    {
        get => _currentContract;
        private set => SetAndRaise(CurrentContractProperty, ref _currentContract, value);
    }

    public MacOSDisplayDiagnostics? DisplayDiagnostics
    {
        get => _displayDiagnostics;
        private set => SetAndRaise(DisplayDiagnosticsProperty, ref _displayDiagnostics, value);
    }

    public MacOSNativePresenterDiagnostics? PresenterDiagnostics
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

    public bool IsPresenterAvailable
    {
        get => _isPresenterAvailable;
        private set => SetAndRaise(IsPresenterAvailableProperty, ref _isPresenterAvailable, value);
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
            if (_colorManagement is not null && !ReferenceEquals(_colorManagement, colorManagement))
                throw new InvalidOperationException("The macOS preview host cannot replace its application-level color-management engine.");
            _colorManagement = colorManagement;
            container = _containerHandle;
        }

        // The engine is not consumed by the macOS environment (ColorSync owns the last hop), but
        // the composition root configures every host the same way, and holding the reference keeps
        // the two hosts' lifecycles identical: no runtime before configuration on either.
        if (OperatingSystem.IsMacOS() && container is not null) EnsureRuntime(container);
    }

    public void PresentNewest(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The native preview host is macOS-only.");

        Runtime runtime;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            runtime = _runtime ?? throw new InvalidOperationException("The macOS preview host is not attached to an Avalonia top level.");
        }
        runtime.Mailbox.Enqueue(frame);
    }

    public void RefreshDisplayState()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (Dispatcher.UIThread.CheckAccess()) RequestUpdate(metrics: false);
        else Dispatcher.UIThread.Post(() => RequestUpdate(metrics: false), DispatcherPriority.Background);
    }

    public string DescribePlatformDiagnostics()
    {
        var text = new System.Text.StringBuilder();
        if (DisplayDiagnostics is { } display)
        {
            text.AppendLine(
                $"Screen: id={display.DirectDisplayId}; name={display.LocalizedName}; " +
                $"colorSpace={display.ColorSpaceName}; backingScale={display.BackingScaleFactor}");
            text.AppendLine(
                $"EDR: current={display.MaximumEdrValue}; potential={display.MaximumPotentialEdrValue}; " +
                $"reference={display.MaximumReferenceEdrValue}; requested={display.EdrRequested}");
            text.AppendLine(
                $"Display fallback: {display.FallbackReason ?? "none"}; contractCapableWYSIWYG={display.WysiwygGuaranteed}; " +
                $"presenterAvailable={IsPresenterAvailable}; " +
                $"effectiveWYSIWYG={PresentationGuarantee.IsEffective(CurrentContract, IsPresenterAvailable)}");
        }

        if (PresenterDiagnostics is { } native)
        {
            text.AppendLine(
                $"Native presenter: ABI={native.AbiVersion}; edrRequested={native.ExtendedRangeRequested}; " +
                $"layerIsEDR={native.LayerIsExtendedRange}; size={native.Size.Width}x{native.Size.Height}; " +
                $"MTLPixelFormat={native.PixelFormat}; colorSpaceSet={native.ColorSpaceWasSet}");
            text.AppendLine(
                $"Native presents: ok={native.SuccessfulPresentCount}; rejected={native.RejectedPresentCount}; " +
                $"droppedDrawables={native.DroppedDrawableCount}; lastResult={native.LastResult}; " +
                $"lastRevision={native.LastContractRevision}; lastDisplay={native.LastDisplayId}");
        }

        return text.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess()) DisposeOnUiThread();
        else Dispatcher.UIThread.Invoke(DisposeOnUiThread, DispatcherPriority.Send);
        GC.SuppressFinalize(this);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle container = base.CreateNativeControlCore(parent);
        if (!OperatingSystem.IsMacOS() || _disposed) return container;

        try
        {
            RequireNsView(container, "Avalonia NativeControlHost container");
            _containerHandle = container;
            EnsureRuntime(container);
            return container;
        }
        catch
        {
            StopRuntime();
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
                StopRuntime();
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
        if (OperatingSystem.IsMacOS() && !_disposed && _containerHandle is not null)
            EnsureRuntime(_containerHandle);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        try { StopRuntime(); }
        finally { base.OnDetachedFromVisualTree(e); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!OperatingSystem.IsMacOS() || _disposed) return;
        if (change.Property == BoundsProperty) RequestUpdate(metrics: true);
    }

    private void EnsureRuntime(IPlatformHandle container)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        lock (_lifecycleGate) { if (_runtime is not null) return; }

        IColorManagementEngine? colorManagement;
        lock (_lifecycleGate) colorManagement = _colorManagement;
        if (colorManagement is null)
        {
            SetPresenterAvailability(false);
            return;
        }

        TopLevel topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException(
            "MacOSPreviewHost must be attached before its native presenter is created.");
        nint containerView = RequireNsView(container, "Avalonia NativeControlHost container");
        double scale = topLevel.RenderScaling;
        PresentationPixelSize size = PreviewPhysicalSize.FromBounds(Bounds.Size, scale);

        IMacOSPreviewHostBackend backend = _backendFactory.Create(containerView, size, scale);
        var mailbox = new PreviewFrameMailbox(backend, _dispatcher, ReportPresentationFailure);
        var runtime = new Runtime(backend, mailbox, topLevel, size, scale);
        backend.ContractChanged += OnBackendContractChanged;
        backend.PresenterRecoveryRequested += OnBackendPresenterRecoveryRequested;

        try
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_runtime is not null) throw new InvalidOperationException("A macOS preview runtime already exists.");
                _runtime = runtime;
            }
            StartRefreshHooks(topLevel);
            ApplyStatus(runtime);
        }
        catch
        {
            StopRefreshHooks();
            backend.ContractChanged -= OnBackendContractChanged;
            backend.PresenterRecoveryRequested -= OnBackendPresenterRecoveryRequested;
            mailbox.Dispose();
            backend.Dispose();
            lock (_lifecycleGate) { if (ReferenceEquals(_runtime, runtime)) _runtime = null; }
            throw;
        }
    }

    private void StartRefreshHooks(TopLevel topLevel)
    {
        StopRefreshHooks();
        _hookedTopLevel = topLevel;
        topLevel.ScalingChanged += OnTopLevelScalingChanged;
        if (topLevel is WindowBase window) window.PositionChanged += OnTopLevelPositionChanged;

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
            if (_hookedTopLevel is WindowBase window) window.PositionChanged -= OnTopLevelPositionChanged;
            _hookedTopLevel = null;
        }
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e) => RequestUpdate(metrics: true);
    private void OnTopLevelPositionChanged(object? sender, PixelPointEventArgs e) => RequestUpdate(metrics: false);
    private void OnRefreshTimerTick(object? sender, EventArgs e) => RequestUpdate(metrics: false);

    private void RequestUpdate(bool metrics)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        lock (_lifecycleGate) { if (_runtime is null) return; }

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
                    // The backend drops frames of any other size (see its Present); ask the
                    // window for one that fits, as the Windows host does.
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
            Dispatcher.UIThread.Post(() => OnBackendContractChanged(sender, contract), DispatcherPriority.Background);
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
            Dispatcher.UIThread.Post(() => OnBackendPresenterRecoveryRequested(sender, e), DispatcherPriority.Background);
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
        lock (_lifecycleGate) { if (!ReferenceEquals(_runtime, runtime)) return; }
        ApplyStatus(runtime);
    }

    private void ApplyStatus(Runtime runtime)
    {
        DisplayContract contract = runtime.Backend.Current;
        bool presenterAvailable = runtime.Backend.IsPresenterAvailable;
        SetPresenterAvailability(presenterAvailable);
        CurrentContract = contract;
        DisplayDiagnostics = runtime.Backend.DisplayDiagnostics;
        PresenterDiagnostics = runtime.Backend.PresenterDiagnostics;
        DiagnosticName = contract.DiagnosticName;
        VisibleWarning = contract.VisibleWarning ?? runtime.Backend.PresenterFailure;
        IsPresenterAvailable = presenterAvailable;
    }

    /// <summary>
    /// Hiding the control is the whole airspace policy on this platform: NativeControlHost then
    /// releases the container, and the managed fallback bitmap underneath is what the user sees.
    /// </summary>
    private void SetPresenterAvailability(bool presenterAvailable)
    {
        if (IsVisible != presenterAvailable) IsVisible = presenterAvailable;
    }

    private void ReportPresentationFailure(Exception error)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReportPresentationFailure(error), DispatcherPriority.Background);
            return;
        }
        LastPresentationError = $"{error.GetType().Name}: {error.Message}";
        Runtime? runtime;
        lock (_lifecycleGate) runtime = _runtime;
        if (runtime is not null)
        {
            ApplyStatusIfAlive(runtime);
            RequestUpdate(metrics: true);
        }
        PresentationFailed?.Invoke(this, new PreviewPresentationFailedEventArgs(error));
    }

    private void StopRuntime()
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

        try
        {
            if (runtime is not null)
            {
                runtime.Backend.ContractChanged -= OnBackendContractChanged;
                runtime.Backend.PresenterRecoveryRequested -= OnBackendPresenterRecoveryRequested;
                runtime.Mailbox.Dispose();
                runtime.Backend.Dispose();
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
        }
    }

    private void DisposeOnUiThread()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        StopRuntime();
    }

    private static nint RequireNsView(IPlatformHandle handle, string role)
    {
        if (handle.Handle == nint.Zero) throw new InvalidOperationException($"{role} NSView is null.");
        if (!string.Equals(handle.HandleDescriptor, "NSView", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException($"{role} is '{handle.HandleDescriptor}', not an NSView.");
        return handle.Handle;
    }

    private sealed class Runtime(
        IMacOSPreviewHostBackend backend,
        PreviewFrameMailbox mailbox,
        TopLevel topLevel,
        PresentationPixelSize size,
        double scale)
    {
        internal IMacOSPreviewHostBackend Backend { get; } = backend;
        internal PreviewFrameMailbox Mailbox { get; } = mailbox;
        internal TopLevel TopLevel { get; } = topLevel;
        internal PresentationPixelSize Size { get; set; } = size;
        internal double Scale { get; set; } = scale;
    }
}
