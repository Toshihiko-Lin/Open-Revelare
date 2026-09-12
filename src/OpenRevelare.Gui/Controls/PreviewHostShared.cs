using Avalonia;
using Avalonia.Threading;
using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// What the composition root needs from a platform preview host, whichever platform it is.
///
/// <para>
/// Both hosts (Win32 D3D child HWND, macOS CAMetalLayer NSView) present the SAME contract-bound
/// FP16 buffer the shared CPU compositor produced; they differ only in how the surface is created
/// and what platform diagnostics they can add. Everything the composition queue and the status
/// badge read goes through here, so the queue is written once.
/// </para>
/// </summary>
internal interface IPreviewHost : IDisposable
{
    DisplayContract? CurrentContract { get; }
    string? DiagnosticName { get; }
    string? VisibleWarning { get; }
    string? LastPresentationError { get; }
    bool IsPresenterAvailable { get; }

    event EventHandler<DisplayContract>? ContractChanged;
    event EventHandler<PreviewPresentationFailedEventArgs>? PresentationFailed;
    event EventHandler? PresentationRecoveryRequested;

    void ConfigureColorManagement(IColorManagementEngine colorManagement);
    void PresentNewest(PresentationBuffer frame);
    void RefreshDisplayState();

    /// <summary>Platform-specific lines for the copied diagnostics; empty when there is nothing to add.</summary>
    string DescribePlatformDiagnostics();
}

public sealed class PreviewPresentationFailedEventArgs : EventArgs
{
    public PreviewPresentationFailedEventArgs(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public Exception Error { get; }
    public bool IsStaleContract => Error is PresentationContractException;
}

internal static class PreviewPhysicalSize
{
    internal static PresentationPixelSize FromBounds(Size logicalBounds, double scale)
    {
        if (!double.IsFinite(logicalBounds.Width) || logicalBounds.Width < 0d)
            throw new ArgumentOutOfRangeException(nameof(logicalBounds), "Logical width must be finite and non-negative.");
        if (!double.IsFinite(logicalBounds.Height) || logicalBounds.Height < 0d)
            throw new ArgumentOutOfRangeException(nameof(logicalBounds), "Logical height must be finite and non-negative.");
        if (!double.IsFinite(scale) || scale <= 0d)
            throw new ArgumentOutOfRangeException(nameof(scale), "Render scaling must be finite and positive.");

        return new PresentationPixelSize(
            RoundDimension(logicalBounds.Width, scale, nameof(logicalBounds)),
            RoundDimension(logicalBounds.Height, scale, nameof(logicalBounds)));
    }

    private static int RoundDimension(double logical, double scale, string parameterName)
    {
        double physical = logical * scale;
        if (!double.IsFinite(physical) || physical > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Scaled preview dimension must fit in a positive Int32 pixel extent.");
        }

        double rounded = Math.Round(physical, MidpointRounding.AwayFromZero);
        return Math.Max(1, checked((int)rounded));
    }
}

/// <summary>
/// Controls whether the native HWND airspace island occludes Avalonia's managed emergency
/// preview. The container is exposed only while a native presenter actually owns visible output.
/// </summary>
internal static class PresentationGuarantee
{
    internal static bool IsEffective(DisplayContract? contract, bool presenterAvailable) =>
        presenterAvailable && contract?.IsWysiwygGuaranteed == true;
}

/// <summary>
/// <see cref="HighlightSoftProof.Fit"/>, remembered for one source scene. A composition is
/// captured on every pan, zoom and overlay toggle; the frame's pixels change far less often
/// than that, and the fit is a full pass over them. Keyed by scene identity: the view model
/// publishes a new scene object per render, and re-tagging the reference white happens after
/// the fit, so identity is the right key.
/// </summary>
internal sealed class SoftProofCache
{
    private PresentationScene? _source;
    private float _contentHeadroom;
    private float _displayHeadroom;
    private PresentationScene? _fitted;

    internal PresentationScene Fit(PresentationScene source, float contentHeadroom, float displayHeadroom)
    {
        if (_fitted is not null && ReferenceEquals(source, _source) &&
            contentHeadroom == _contentHeadroom && displayHeadroom == _displayHeadroom)
            return _fitted;

        _fitted = HighlightSoftProof.Fit(source, contentHeadroom, displayHeadroom);
        _source = source;
        _contentHeadroom = contentHeadroom;
        _displayHeadroom = displayHeadroom;
        return _fitted;
    }
}

internal interface IPreviewFrameSink
{
    DisplayContract Current { get; }
    void Present(PresentationBuffer frame);
}

internal interface IPreviewDispatcher
{
    void Post(Action action);
}

/// <summary>
/// A one-slot mailbox. Producers may be on any thread; only the newest not-yet-dispatched frame
/// is retained, and both enqueue and UI-thread drain validate against the live display contract.
/// </summary>
internal sealed class PreviewFrameMailbox : IDisposable
{
    private readonly object _gate = new();
    private readonly IPreviewFrameSink _sink;
    private readonly IPreviewDispatcher _dispatcher;
    private readonly Action<Exception> _onRejected;
    private PresentationBuffer? _pending;
    private bool _scheduled;
    private bool _disposed;

    internal PreviewFrameMailbox(
        IPreviewFrameSink sink,
        IPreviewDispatcher dispatcher,
        Action<Exception> onRejected)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(onRejected);
        _sink = sink;
        _dispatcher = dispatcher;
        _onRejected = onRejected;
    }

    internal void Enqueue(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool post = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            _sink.Current.Validate(frame);
            _pending = frame;
            if (!_scheduled)
            {
                _scheduled = true;
                post = true;
            }
        }

        if (!post) return;
        try
        {
            _dispatcher.Post(Drain);
        }
        catch
        {
            lock (_gate)
            {
                _scheduled = false;
                _pending = null;
            }
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
        }
    }

    private void Drain()
    {
        PresentationBuffer? frame;
        lock (_gate)
        {
            if (_disposed)
            {
                _scheduled = false;
                _pending = null;
                return;
            }

            frame = _pending;
            _pending = null;
            _scheduled = false;
        }

        if (frame is null) return;
        try
        {
            _sink.Current.Validate(frame);
            _sink.Present(frame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _onRejected(ex);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class AvaloniaPreviewDispatcher : IPreviewDispatcher
{
    internal static AvaloniaPreviewDispatcher Instance { get; } = new();

    private AvaloniaPreviewDispatcher() { }

    public void Post(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}
