using OpenRevelare.Presentation;
using OpenRevelare.Presentation.MacOS;
using OpenRevelare.Presentation.MacOS.Native;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Controls;

internal interface IMacOSPreviewHostBackend : IPreviewFrameSink, IDisposable
{
    MacOSDisplayDiagnostics DisplayDiagnostics { get; }
    MacOSNativePresenterDiagnostics? PresenterDiagnostics { get; }
    string? PresenterFailure { get; }
    bool IsPresenterAvailable { get; }
    event EventHandler<DisplayContract>? ContractChanged;
    event EventHandler? PresenterRecoveryRequested;
    void Refresh();
    void Resize(PresentationPixelSize size, double scale);
}

internal interface IMacOSPreviewHostBackendFactory
{
    IMacOSPreviewHostBackend Create(nint containerView, PresentationPixelSize initialSize, double initialScale);
}

internal interface IMacOSPreviewPresenterFactory
{
    IMacOSPreviewPresenter Create(IDisplayEnvironment environment, nint parentView, PresentationPixelSize initialSize);
}

internal interface IMacOSDisplayEnvironmentFactory
{
    IMacOSDisplayEnvironment Create(nint surfaceView);
}

internal sealed class MacOSDisplayEnvironmentFactory : IMacOSDisplayEnvironmentFactory
{
    internal static MacOSDisplayEnvironmentFactory Instance { get; } = new();
    private MacOSDisplayEnvironmentFactory() { }
    public IMacOSDisplayEnvironment Create(nint surfaceView) => new MacOSDisplayEnvironment(surfaceView);
}

internal sealed class MacOSPreviewPresenterFactory : IMacOSPreviewPresenterFactory
{
    internal static MacOSPreviewPresenterFactory Instance { get; } = new();
    private MacOSPreviewPresenterFactory() { }
    public IMacOSPreviewPresenter Create(IDisplayEnvironment environment, nint parentView, PresentationPixelSize initialSize) =>
        new MacOSPreviewPresenter(environment, parentView, initialSize);
}

internal sealed class MacOSPreviewHostBackendFactory : IMacOSPreviewHostBackendFactory
{
    internal static MacOSPreviewHostBackendFactory Instance { get; } = new();
    private MacOSPreviewHostBackendFactory() { }
    public IMacOSPreviewHostBackend Create(nint containerView, PresentationPixelSize initialSize, double initialScale) =>
        new MacOSPreviewHostBackend(containerView, initialSize, initialScale);
}

/// <summary>
/// Owns the macOS display environment and the native presenter, and keeps the two reconciled:
/// the presenter is recreated when the contract's screen, encoding or EDR request changes, and
/// after any native failure — on the next refresh with a usable contract.
///
/// <para>
/// THE SAME MACHINE AS THE WIN32 BACKEND, MINUS TWO BRIDGES. There is no input bridge (the
/// presenter's NSView returns nil from hitTest, so AppKit routes pointer events to the Avalonia
/// view underneath without any forwarding) and no display-change message bridge (AppKit screen
/// notifications are not hooked; the host's one-second poll plus Avalonia's scaling/position
/// events cover the same cases, which is the fallback the Win32 host already relies on when its
/// messages are late).
/// </para>
/// </summary>
internal sealed class MacOSPreviewHostBackend : IMacOSPreviewHostBackend
{
    private readonly object _gate = new();
    private readonly nint _containerView;
    private readonly IMacOSDisplayEnvironment _environment;
    private readonly IMacOSPreviewPresenterFactory _presenterFactory;
    private IMacOSPreviewPresenter? _presenter;
    private MacOSNativePresenterDiagnostics? _presenterDiagnostics;
    private string? _presenterFailure;
    private string? _presenterDisplayId;
    private float _presenterHeadroom;
    private PresentationPixelSize _size;
    private double _scale;
    private bool _recoveryPending;
    private bool _disposed;

    internal MacOSPreviewHostBackend(nint containerView, PresentationPixelSize initialSize, double initialScale)
        : this(containerView, initialSize, initialScale,
               CreateEnvironment(containerView, MacOSDisplayEnvironmentFactory.Instance),
               MacOSPreviewPresenterFactory.Instance)
    {
    }

    internal MacOSPreviewHostBackend(
        nint containerView,
        PresentationPixelSize initialSize,
        double initialScale,
        IMacOSDisplayEnvironment environment,
        IMacOSPreviewPresenterFactory presenterFactory)
    {
        if (containerView == nint.Zero)
            throw new ArgumentException("Native host container NSView is null.", nameof(containerView));
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(presenterFactory);
        RequireScale(initialScale);

        _containerView = containerView;
        _size = initialSize;
        _scale = initialScale;
        _environment = environment;
        _presenterFactory = presenterFactory;
        _environment.ContractChanged += OnEnvironmentContractChanged;

        lock (_gate) ReconcilePresenter(_environment.Current);
    }

    public DisplayContract Current
    {
        get { lock (_gate) { ThrowIfDisposed(); return _environment.Current; } }
    }

    public MacOSDisplayDiagnostics DisplayDiagnostics
    {
        get { lock (_gate) { ThrowIfDisposed(); return _environment.Diagnostics; } }
    }

    public MacOSNativePresenterDiagnostics? PresenterDiagnostics
    {
        get { lock (_gate) { ThrowIfDisposed(); return _presenterDiagnostics; } }
    }

    public string? PresenterFailure
    {
        get { lock (_gate) { ThrowIfDisposed(); return _presenterFailure; } }
    }

    public bool IsPresenterAvailable
    {
        get { lock (_gate) { ThrowIfDisposed(); return _presenter is not null; } }
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
            if (ReconcilePresenter(_environment.Current)) recovered = PresenterRecoveryRequested;
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
            DisplayContract current = _environment.Current;
            current.Validate(frame);
            if (_presenter is null)
            {
                throw new InvalidOperationException(
                    _presenterFailure ?? current.VisibleWarning ??
                    "The current macOS display contract has no native presenter.");
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
        IMacOSPreviewPresenter? presenter;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _environment.ContractChanged -= OnEnvironmentContractChanged;
            presenter = _presenter;
            _presenter = null;
            _presenterDiagnostics = null;
        }
        try { presenter?.Dispose(); }
        finally { _environment.Dispose(); }
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

    /// <summary>
    /// True when a presenter was (re)created after a failure — the signal the host uses to ask the
    /// composition root for a fresh frame, since the one that failed is gone.
    /// </summary>
    private bool ReconcilePresenter(DisplayContract contract)
    {
        bool supported = contract.Encoding == PresentationEncoding.LinearExtendedSrgbRgba16F;
        bool edrNow = contract.ExtendedHeadroom > 1f;
        bool alreadyMatches =
            _presenter is not null &&
            string.Equals(_presenterDisplayId, contract.DisplayId, StringComparison.Ordinal) &&
            (_presenterHeadroom > 1f) == edrNow;
        if (alreadyMatches) return false;

        DisposePresenterForReplacement();
        _presenter = null;
        _presenterDisplayId = null;
        _presenterHeadroom = 1f;

        if (!supported)
        {
            // An emergency contract has nothing a tagged surface could show; the managed
            // fallback bitmap takes over, exactly as on Windows.
            _recoveryPending = false;
            return false;
        }

        bool wasRecovery = _recoveryPending;
        try
        {
            _presenter = _presenterFactory.Create(_environment, _containerView, _size);
            _presenter.Resize(_size, _scale);
            _presenterDisplayId = contract.DisplayId;
            _presenterHeadroom = contract.ExtendedHeadroom;
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
            _presenterHeadroom = 1f;
            _recoveryPending = true;
            CapturePresenterFailure("create", ex);
            return false;
        }
    }

    private void InvalidatePresenter(string operation, Exception error)
    {
        CapturePresenterFailure(operation, error);
        IMacOSPreviewPresenter? failed = _presenter;
        _presenter = null;
        _presenterDisplayId = null;
        _presenterHeadroom = 1f;
        _recoveryPending = true;
        try { failed?.Dispose(); }
        catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
        {
            _presenterFailure += $" Presenter disposal also failed: {disposeError.GetType().Name}: {disposeError.Message}";
        }
    }

    private void DisposePresenterForReplacement()
    {
        IMacOSPreviewPresenter? presenter = _presenter;
        if (presenter is null) return;
        try { presenter.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _presenterFailure = $"macOS native preview presenter disposal failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void TryCapturePresenterDiagnostics()
    {
        if (_presenter is null) return;
        try { _presenterDiagnostics = _presenter.QueryNativeDiagnostics(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _presenterDiagnostics = ex is MacOSNativePresenterException native ? native.Diagnostics : null;
        }
    }

    private void CapturePresenterFailure(string operation, Exception error)
    {
        if (error is MacOSNativePresenterException native && native.Diagnostics is not null)
            _presenterDiagnostics = native.Diagnostics;
        _presenterFailure = $"macOS native preview presenter {operation} failed: {error.GetType().Name}: {error.Message}";
    }

    private static IMacOSDisplayEnvironment CreateEnvironment(nint containerView, IMacOSDisplayEnvironmentFactory factory)
    {
        if (containerView == nint.Zero)
            throw new ArgumentException("Native host container NSView is null.", nameof(containerView));
        return factory.Create(containerView);
    }

    private static void RequireScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0d)
            throw new ArgumentOutOfRangeException(nameof(scale), "Render scaling must be finite and positive.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
