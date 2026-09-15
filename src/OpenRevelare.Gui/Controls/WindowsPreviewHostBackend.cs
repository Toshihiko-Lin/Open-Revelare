using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.Win32;
using OpenRevelare.Presentation.Win32.Native;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Controls;

internal interface IWindowsPreviewHostBackend : IPreviewFrameSink, IDisposable
{
    WindowsDisplayDiagnostics DisplayDiagnostics { get; }
    WindowsNativePresenterDiagnostics? PresenterDiagnostics { get; }
    string? PresenterFailure { get; }
    bool IsPresenterAvailable { get; }
    event EventHandler<DisplayContract>? ContractChanged;
    event EventHandler? PresenterRecoveryRequested;
    void Refresh();
    void Resize(PresentationPixelSize size, double scale);
}

internal interface IWindowsPreviewHostBackendFactory
{
    IWindowsPreviewHostBackend Create(
        nint containerHwnd,
        nint topLevelHwnd,
        PresentationPixelSize initialSize,
        double initialScale,
        IColorManagementEngine colorManagement);
}

internal interface IWindowsPreviewPresenterFactory
{
    IWindowsPreviewPresenter Create(
        IDisplayEnvironment environment,
        nint parentHwnd,
        PresentationPixelSize initialSize);
}

internal interface IWindowsDisplayEnvironmentFactory
{
    IWindowsDisplayEnvironment Create(
        nint surfaceHwnd,
        IColorManagementEngine colorManagement);
}

internal sealed class WindowsDisplayEnvironmentFactory : IWindowsDisplayEnvironmentFactory
{
    internal static WindowsDisplayEnvironmentFactory Instance { get; } = new();

    private WindowsDisplayEnvironmentFactory() { }

    public IWindowsDisplayEnvironment Create(
        nint surfaceHwnd,
        IColorManagementEngine colorManagement) =>
        new WindowsDisplayEnvironment(surfaceHwnd, colorManagement);
}

internal sealed class WindowsPreviewPresenterFactory : IWindowsPreviewPresenterFactory
{
    internal static WindowsPreviewPresenterFactory Instance { get; } = new();

    private WindowsPreviewPresenterFactory() { }

    public IWindowsPreviewPresenter Create(
        IDisplayEnvironment environment,
        nint parentHwnd,
        PresentationPixelSize initialSize) =>
        new WindowsPreviewPresenter(environment, parentHwnd, initialSize);
}

internal sealed class WindowsPreviewHostBackendFactory : IWindowsPreviewHostBackendFactory
{
    internal static WindowsPreviewHostBackendFactory Instance { get; } = new();

    private WindowsPreviewHostBackendFactory() { }

    public IWindowsPreviewHostBackend Create(
        nint containerHwnd,
        nint topLevelHwnd,
        PresentationPixelSize initialSize,
        double initialScale,
        IColorManagementEngine colorManagement) =>
        new WindowsPreviewHostBackend(
            containerHwnd,
            topLevelHwnd,
            initialSize,
            initialScale,
            colorManagement);
}

/// <summary>
/// Owns the existing Win32 environment and presenter as one replaceable GUI lifetime. A display
/// or presentation-mode change recreates only the presenter; the environment remains the single
/// revision authority.
/// </summary>
internal sealed class WindowsPreviewHostBackend : IWindowsPreviewHostBackend
{
    private readonly object _gate = new();
    private readonly nint _containerHwnd;
    private readonly IWindowsDisplayEnvironment _environment;
    private readonly IWindowsPreviewPresenterFactory _presenterFactory;
    private IWindowsPreviewPresenter? _presenter;
    private WindowsNativePresenterDiagnostics? _presenterDiagnostics;
    private string? _presenterFailure;
    private string? _presenterDisplayId;
    private PresentationEncoding? _presenterEncoding;
    private PresentationPixelSize _size;
    private double _scale;
    private bool _recoveryPending;
    private bool _disposed;

    internal WindowsPreviewHostBackend(
        nint containerHwnd,
        nint topLevelHwnd,
        PresentationPixelSize initialSize,
        double initialScale,
        IColorManagementEngine colorManagement)
        : this(
            containerHwnd,
            topLevelHwnd,
            initialSize,
            initialScale,
            colorManagement,
            WindowsDisplayEnvironmentFactory.Instance,
            WindowsPreviewPresenterFactory.Instance)
    {
    }

    internal WindowsPreviewHostBackend(
        nint containerHwnd,
        nint topLevelHwnd,
        PresentationPixelSize initialSize,
        double initialScale,
        IColorManagementEngine colorManagement,
        IWindowsDisplayEnvironmentFactory environmentFactory,
        IWindowsPreviewPresenterFactory presenterFactory)
        : this(
            containerHwnd,
            initialSize,
            initialScale,
            CreateDisplayEnvironment(
                containerHwnd,
                topLevelHwnd,
                colorManagement,
                environmentFactory),
            presenterFactory)
    { }

    internal WindowsPreviewHostBackend(
        nint containerHwnd,
        PresentationPixelSize initialSize,
        double initialScale,
        IWindowsDisplayEnvironment environment,
        IWindowsPreviewPresenterFactory presenterFactory)
    {
        if (containerHwnd == nint.Zero)
            throw new ArgumentException("Native host container HWND is null.", nameof(containerHwnd));
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(presenterFactory);
        RequireScale(initialScale);

        _containerHwnd = containerHwnd;
        _size = initialSize;
        _scale = initialScale;
        _environment = environment;
        _presenterFactory = presenterFactory;
        _environment.ContractChanged += OnEnvironmentContractChanged;

        lock (_gate)
        {
            ReconcilePresenter(_environment.Current);
        }
    }

    public DisplayContract Current
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _environment.Current;
            }
        }
    }

    public WindowsDisplayDiagnostics DisplayDiagnostics
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _environment.Diagnostics;
            }
        }
    }

    public WindowsNativePresenterDiagnostics? PresenterDiagnostics
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _presenterDiagnostics;
            }
        }
    }

    public string? PresenterFailure
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _presenterFailure;
            }
        }
    }

    public bool IsPresenterAvailable
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _presenter is not null;
            }
        }
    }

    public event EventHandler<DisplayContract>? ContractChanged;
    public event EventHandler? PresenterRecoveryRequested;

    public void Refresh()
    {
        lock (_gate) ThrowIfDisposed();
        _environment.Refresh();

        EventHandler? recovered = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReconcilePresenter(_environment.Current))
                recovered = PresenterRecoveryRequested;
        }
        recovered?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(PresentationPixelSize size, double scale)
    {
        RequireScale(scale);
        lock (_gate)
        {
            ThrowIfDisposed();
            _size = size;
            _scale = scale;
            if (_presenter is null) return;

            try
            {
                _presenter.Resize(size, scale);
                _presenterFailure = null;
                TryCapturePresenterDiagnostics();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                InvalidatePresenter("resize", ex);
                throw;
            }
        }
    }

    public void Present(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ThrowIfDisposed();
            // A frame composed for a viewport size the presenter no longer has (or does not have
            // yet) is not an error, it is superseded: the resize that made it stale has already
            // asked the window for a fresh composition (WindowsPreviewHost raises
            // PresenterRecoveryRequested after every Resize), and that frame will fit. Handing
            // this one to the native presenter instead returned InvalidSize, which invalidated the
            // presenter and blanked the preview for a frame that was about to be replaced anyway.
            // The two sizes come from two different bounds observers (the window's viewport
            // snapshot and this host's own Bounds), updated at different dispatcher priorities,
            // so a brief disagreement during a resize is expected, not exceptional.
            if (frame.Size != _size) return;
            DisplayContract current = _environment.Current;
            current.Validate(frame);

            if (_presenter is null)
            {
                throw new InvalidOperationException(
                    _presenterFailure ?? current.VisibleWarning ??
                    "The current Windows display contract has no native presenter.");
            }

            try
            {
                _presenter.Present(frame);
                _presenterFailure = null;
                TryCapturePresenterDiagnostics();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                InvalidatePresenter("present", ex);
                throw;
            }
        }
    }

    public void Dispose()
    {
        IWindowsPreviewPresenter? presenter;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _environment.ContractChanged -= OnEnvironmentContractChanged;
            presenter = _presenter;
            _presenter = null;
            _presenterDiagnostics = null;
        }

        try
        {
            presenter?.Dispose();
        }
        finally
        {
            _environment.Dispose();
        }
    }

    private void OnEnvironmentContractChanged(object? sender, DisplayContract contract)
    {
        EventHandler<DisplayContract>? changed;
        EventHandler? recovered = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (ReconcilePresenter(contract)) recovered = PresenterRecoveryRequested;
            changed = ContractChanged;
        }

        changed?.Invoke(this, contract);
        recovered?.Invoke(this, EventArgs.Empty);
    }

    private bool ReconcilePresenter(DisplayContract contract)
    {
        bool supported = IsNativePresentationEncoding(contract.Encoding);
        bool alreadyMatches =
            _presenter is not null &&
            _presenterEncoding == contract.Encoding &&
            string.Equals(_presenterDisplayId, contract.DisplayId, StringComparison.Ordinal);
        if (alreadyMatches) return false;

        DisposePresenterForReplacement();
        _presenter = null;
        _presenterDisplayId = null;
        _presenterEncoding = null;

        if (!supported)
        {
            _recoveryPending = false;
            return false;
        }

        bool wasRecovery = _recoveryPending;
        try
        {
            _presenter = _presenterFactory.Create(_environment, _containerHwnd, _size);
            _presenter.Resize(_size, _scale);
            _presenterDisplayId = contract.DisplayId;
            _presenterEncoding = contract.Encoding;
            _presenterFailure = null;
            _recoveryPending = false;
            TryCapturePresenterDiagnostics();
            return wasRecovery;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DisposePresenterForReplacement();
            _presenter = null;
            _presenterDisplayId = null;
            _presenterEncoding = null;
            _recoveryPending = true;
            CapturePresenterFailure("create", ex);
            return false;
        }
    }

    private void InvalidatePresenter(string operation, Exception error)
    {
        CapturePresenterFailure(operation, error);
        IWindowsPreviewPresenter? failed = _presenter;
        _presenter = null;
        _presenterDisplayId = null;
        _presenterEncoding = null;
        _recoveryPending = true;
        try
        {
            failed?.Dispose();
        }
        catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
        {
            _presenterFailure +=
                $" Presenter disposal also failed: {disposeError.GetType().Name}: {disposeError.Message}";
        }
    }

    private void DisposePresenterForReplacement()
    {
        IWindowsPreviewPresenter? presenter = _presenter;
        if (presenter is null) return;
        try
        {
            presenter.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _presenterFailure =
                $"Windows native preview presenter disposal failed: " +
                $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void TryCapturePresenterDiagnostics()
    {
        if (_presenter is null) return;
        try
        {
            _presenterDiagnostics = _presenter.QueryNativeDiagnostics();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostics are observational. A successful create/resize/present is not converted
            // into a presentation failure solely because the optional query failed.
            _presenterDiagnostics = ex is Win32NativePresenterException native
                ? native.Diagnostics
                : null;
        }
    }

    private void CapturePresenterFailure(string operation, Exception error)
    {
        if (error is Win32NativePresenterException native && native.Diagnostics is not null)
            _presenterDiagnostics = native.Diagnostics;
        _presenterFailure =
            $"Windows native preview presenter {operation} failed: {error.GetType().Name}: {error.Message}";
    }

    private static IWindowsDisplayEnvironment CreateDisplayEnvironment(
        nint containerHwnd,
        nint topLevelHwnd,
        IColorManagementEngine colorManagement,
        IWindowsDisplayEnvironmentFactory environmentFactory)
    {
        if (containerHwnd == nint.Zero)
            throw new ArgumentException("Native host container HWND is null.", nameof(containerHwnd));
        if (topLevelHwnd == nint.Zero)
            throw new ArgumentException("Avalonia top-level HWND is null.", nameof(topLevelHwnd));
        ArgumentNullException.ThrowIfNull(colorManagement);
        ArgumentNullException.ThrowIfNull(environmentFactory);

        // The display contract belongs to the color-critical surface. The top-level HWND remains
        // only the target for display-message observation and forwarded Avalonia input.
        return environmentFactory.Create(containerHwnd, colorManagement);
    }

    private static void RequireScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0d)
            throw new ArgumentOutOfRangeException(nameof(scale), "Render scaling must be finite and positive.");
    }

    internal static bool IsNativePresentationEncoding(PresentationEncoding encoding) => encoding is
        PresentationEncoding.LinearExtendedSrgbRgba16F or
        PresentationEncoding.MonitorDeviceBgra8 or
        PresentationEncoding.UnmanagedEmergencySrgb8;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
