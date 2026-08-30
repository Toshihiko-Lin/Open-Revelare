using OpenRevelare.Presentation;
using OpenRevelare.Presentation.Win32.Native;

namespace OpenRevelare.Presentation.Win32;

public interface IWindowsPreviewPresenter : IPreviewPresenter
{
    WindowsNativePresenterDiagnostics QueryNativeDiagnostics();
}

/// <summary>
/// Thin managed adapter between the shared presentation contract and the native fixed-copy
/// presenter. It performs no color conversion and owns no display-profile policy.
/// </summary>
public sealed class WindowsPreviewPresenter : IWindowsPreviewPresenter
{
    private readonly object _gate = new();
    private readonly IDisplayEnvironment _environment;
    private readonly IWin32NativePresenterSession _native;
    private readonly string _createdDisplayId;
    private bool _requiresRecreation;
    private bool _disposed;

    public WindowsPreviewPresenter(
        IDisplayEnvironment environment,
        nint parentHwnd,
        PixelSize initialSize)
        : this(environment, CreateNative(environment, parentHwnd, initialSize))
    {
    }

    internal WindowsPreviewPresenter(
        IDisplayEnvironment environment,
        IWin32NativePresenterSession native)
        : this(environment, CaptureNative(environment, native))
    {
    }

    private WindowsPreviewPresenter(
        IDisplayEnvironment environment,
        NativeCreation creation)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _native = creation.Session;
        _createdDisplayId = creation.Contract.DisplayId;
        AcceptedEncoding = creation.Contract.Encoding;

        bool subscribed = false;
        try
        {
            NativePresenterMode expectedMode = MapMode(AcceptedEncoding);
            if (_native.Mode != expectedMode)
            {
                throw new ArgumentException(
                    "Native presenter mode does not match the display contract.",
                    nameof(creation));
            }

            _environment.ContractChanged += OnContractChanged;
            subscribed = true;

            // Close the interval between capturing Current for native creation and subscribing to
            // changes. A revision-only update can reuse the same surface; a display or mode change
            // cannot.
            DisplayContract afterSubscription = _environment.Current;
            if (afterSubscription.Encoding != AcceptedEncoding ||
                !string.Equals(
                    afterSubscription.DisplayId,
                    _createdDisplayId,
                    StringComparison.Ordinal))
            {
                throw new WindowsPresenterRecreationRequiredException(
                    "The Windows display or presentation mode changed while creating the native presenter.");
            }
        }
        catch
        {
            if (subscribed) _environment.ContractChanged -= OnContractChanged;
            _native.Dispose();
            throw;
        }
    }

    public PresentationEncoding AcceptedEncoding { get; }

    public void Resize(PixelSize pixels, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), "Display scale must be finite and positive.");
        Win32NativePresenterFactory.ValidateSize(pixels);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfRecreationRequired();
            _native.Resize(pixels);
        }
    }

    public void Present(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfRecreationRequired();

            if (_environment is IWindowsDisplayContractLeaseProvider leaseProvider)
            {
                // Keep Refresh from publishing a newer profile/display/DPI contract between
                // final validation and the fixed-copy native call.
                leaseProvider.WithStableContract(current => PresentValidated(frame, current));
                return;
            }

            DisplayContract current = _environment.Current;
            PresentValidated(frame, current);

            // A generic IDisplayEnvironment cannot provide the Win32 refresh lease. The native
            // ABI still receives both frame/current contracts, but production uses the lease
            // above to close the managed last-read race completely.
        }
    }

    private void PresentValidated(PresentationBuffer frame, DisplayContract current)
    {
        EnsurePresenterStillTargets(current);
        current.Validate(frame);

        int bytesPerPixel = AcceptedEncoding switch
        {
            PresentationEncoding.LinearExtendedSrgbRgba16F => 8,
            PresentationEncoding.MonitorDeviceBgra8 => 4,
            PresentationEncoding.UnmanagedEmergencySrgb8 => 4,
            _ => throw new NotSupportedException(
                $"The native Windows presenter cannot accept {AcceptedEncoding}.")
        };
        int rowPitch = checked(frame.Size.Width * bytesPerPixel);
        int requiredBytes = checked(rowPitch * frame.Size.Height);
        if (frame.Bytes.Length != requiredBytes)
            throw new ArgumentException(
                $"Presentation byte length {frame.Bytes.Length} does not equal the required {requiredBytes}.",
                nameof(frame));

        // Retain a second read for test/custom environments that don't expose the production
        // Windows refresh lease.
        if (_environment is not IWindowsDisplayContractLeaseProvider)
        {
            DisplayContract latest = _environment.Current;
            if (latest.Revision != current.Revision ||
                latest.Encoding != current.Encoding ||
                !string.Equals(latest.DisplayId, current.DisplayId, StringComparison.Ordinal))
                throw new InvalidOperationException("Display contract changed while preparing the presentation frame.");
        }

        _native.Present(
            frame.Bytes.AsSpan(),
            checked((uint)rowPitch),
            frame.Size,
            frame.TargetDisplayId,
            frame.ContractRevision,
            current.DisplayId,
            current.Revision);
    }

    public WindowsNativePresenterDiagnostics QueryNativeDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _native.QueryDiagnostics();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _environment.ContractChanged -= OnContractChanged;
            _native.Dispose();
        }
    }

    private static NativeCreation CreateNative(
        IDisplayEnvironment environment,
        nint parentHwnd,
        PixelSize initialSize)
    {
        ArgumentNullException.ThrowIfNull(environment);
        DisplayContract contract = environment.Current;
        NativePresenterMode mode = MapMode(contract.Encoding);
        IWin32NativePresenterSession session =
            Win32NativePresenterFactory.Instance.Create(parentHwnd, mode, initialSize);
        return new NativeCreation(contract, session);
    }

    private static NativeCreation CaptureNative(
        IDisplayEnvironment environment,
        IWin32NativePresenterSession native)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(native);
        return new NativeCreation(environment.Current, native);
    }

    private static NativePresenterMode MapMode(PresentationEncoding encoding) => encoding switch
    {
        PresentationEncoding.LinearExtendedSrgbRgba16F => NativePresenterMode.AdvancedColor,
        PresentationEncoding.MonitorDeviceBgra8 => NativePresenterMode.Legacy,
        // Emergency pixels are explicitly sRGB BGRA8 with no app monitor transform. Reuse the
        // fixed-copy 8-bit surface so the native child remains live (and display refresh keeps
        // working) while the UI exposes DisplayContract.VisibleWarning. The managed contract
        // still prevents these pixels from being confused with monitor-device BGRA8.
        PresentationEncoding.UnmanagedEmergencySrgb8 => NativePresenterMode.Legacy,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    private void OnContractChanged(object? sender, DisplayContract contract)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (contract.Encoding != AcceptedEncoding ||
                !string.Equals(contract.DisplayId, _createdDisplayId, StringComparison.Ordinal))
            {
                _requiresRecreation = true;
            }
        }
    }

    private void EnsurePresenterStillTargets(DisplayContract current)
    {
        if (current.Encoding != AcceptedEncoding ||
            !string.Equals(current.DisplayId, _createdDisplayId, StringComparison.Ordinal))
        {
            _requiresRecreation = true;
            ThrowIfRecreationRequired();
        }
    }

    private void ThrowIfRecreationRequired()
    {
        if (_requiresRecreation)
            throw new WindowsPresenterRecreationRequiredException(
                "The Windows display or presentation mode changed; recreate the native presenter.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record NativeCreation(
        DisplayContract Contract,
        IWin32NativePresenterSession Session);
}

public sealed class WindowsPresenterRecreationRequiredException : InvalidOperationException
{
    public WindowsPresenterRecreationRequiredException(string message) : base(message) { }
}
