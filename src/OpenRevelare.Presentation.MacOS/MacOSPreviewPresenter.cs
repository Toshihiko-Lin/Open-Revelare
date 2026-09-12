using OpenRevelare.Presentation;
using OpenRevelare.Presentation.MacOS.Native;

namespace OpenRevelare.Presentation.MacOS;

public interface IMacOSPreviewPresenter : IPreviewPresenter
{
    MacOSNativePresenterDiagnostics QueryNativeDiagnostics();
}

/// <summary>
/// Thin managed adapter between the shared presentation contract and the native
/// <c>CAMetalLayer</c> presenter. It performs no colour conversion and owns no display policy —
/// the same shape and the same guarantees as its Win32 sibling:
///
/// <list type="bullet">
/// <item>a frame is validated against the CURRENT contract immediately before upload;</item>
/// <item>a stale revision, a foreign display id or a different encoding is refused;</item>
/// <item>a display or headroom-mode change marks the presenter for recreation rather than
/// quietly retargeting the surface.</item>
/// </list>
///
/// <para>
/// WHAT DIFFERS FROM WINDOWS. There is exactly one accepted encoding — the system-owned FP16
/// carrier — because ColorSync always runs the last hop (§8.1); and the only native mode switch
/// is whether extended-range content was requested (D-012's candidate rule), which is derived
/// from the contract's headroom and therefore also forces recreation when it flips.
/// </para>
/// </summary>
public sealed class MacOSPreviewPresenter : IMacOSPreviewPresenter
{
    private readonly object _gate = new();
    private readonly IDisplayEnvironment _environment;
    private readonly IMacOSNativePresenterSession _native;
    private readonly string _createdDisplayId;
    private bool _requiresRecreation;
    private bool _disposed;

    public MacOSPreviewPresenter(
        IDisplayEnvironment environment,
        nint parentView,
        PixelSize initialSize)
        : this(environment, CreateNative(environment, parentView, initialSize))
    {
    }

    internal MacOSPreviewPresenter(
        IDisplayEnvironment environment,
        IMacOSNativePresenterSession native)
        : this(environment, CaptureNative(environment, native))
    {
    }

    private MacOSPreviewPresenter(IDisplayEnvironment environment, NativeCreation creation)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _native = creation.Session;
        _createdDisplayId = creation.Contract.DisplayId;
        AcceptedEncoding = creation.Contract.Encoding;

        bool subscribed = false;
        try
        {
            if (AcceptedEncoding != PresentationEncoding.LinearExtendedSrgbRgba16F)
            {
                throw new ArgumentException(
                    "The native macOS presenter only accepts the system-owned FP16 carrier; an " +
                    "emergency contract must not create one.",
                    nameof(creation));
            }
            if (_native.ExtendedRangeRequested !=
                MacOSDisplayContractProvider.ShouldRequestExtendedRange(creation.Contract))
            {
                throw new ArgumentException(
                    "Native presenter extended-range mode does not match the display contract.",
                    nameof(creation));
            }

            _environment.ContractChanged += OnContractChanged;
            subscribed = true;

            DisplayContract afterSubscription = _environment.Current;
            if (RequiresRecreationFor(afterSubscription))
            {
                throw new MacOSPresenterRecreationRequiredException(
                    "The macOS screen or headroom changed while creating the native presenter.");
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
        MacOSNativePresenterFactory.ValidateSize(pixels);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfRecreationRequired();
            _native.Resize(pixels, scale);
        }
    }

    public void Present(PresentationBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfRecreationRequired();

            if (_environment is IMacOSDisplayContractLeaseProvider leaseProvider)
            {
                leaseProvider.WithStableContract(current => PresentValidated(frame, current));
                return;
            }

            PresentValidated(frame, _environment.Current);
        }
    }

    private void PresentValidated(PresentationBuffer frame, DisplayContract current)
    {
        if (RequiresRecreationFor(current))
        {
            _requiresRecreation = true;
            ThrowIfRecreationRequired();
        }
        current.Validate(frame);

        const int bytesPerPixel = 8;   // RGBA-half, the only accepted encoding
        int rowPitch = checked(frame.Size.Width * bytesPerPixel);
        int requiredBytes = checked(rowPitch * frame.Size.Height);
        if (frame.Bytes.Length != requiredBytes)
        {
            throw new ArgumentException(
                $"Presentation byte length {frame.Bytes.Length} does not equal the required {requiredBytes}.",
                nameof(frame));
        }

        if (_environment is not IMacOSDisplayContractLeaseProvider)
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

    public MacOSNativePresenterDiagnostics QueryNativeDiagnostics()
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
        nint parentView,
        PixelSize initialSize)
    {
        ArgumentNullException.ThrowIfNull(environment);
        DisplayContract contract = environment.Current;
        if (contract.Encoding != PresentationEncoding.LinearExtendedSrgbRgba16F)
        {
            throw new MacOSPresenterUnavailableException(
                contract.VisibleWarning ?? "The macOS display contract is not system-managed.");
        }
        IMacOSNativePresenterSession session = MacOSNativePresenterFactory.Instance.Create(
            parentView,
            MacOSDisplayContractProvider.ShouldRequestExtendedRange(contract),
            initialSize);
        return new NativeCreation(contract, session);
    }

    private static NativeCreation CaptureNative(
        IDisplayEnvironment environment,
        IMacOSNativePresenterSession native)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(native);
        return new NativeCreation(environment.Current, native);
    }

    private void OnContractChanged(object? sender, DisplayContract contract)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (RequiresRecreationFor(contract)) _requiresRecreation = true;
        }
    }

    /// <summary>
    /// A revision-only change (same screen, same mode) can reuse the surface. A different screen,
    /// a different encoding, or a flip of the extended-range request cannot: the layer's colour
    /// space and EDR flag are set at creation, and quietly retargeting would present the previous
    /// screen's headroom assumptions on the new one.
    /// </summary>
    private bool RequiresRecreationFor(DisplayContract contract) =>
        contract.Encoding != AcceptedEncoding ||
        !string.Equals(contract.DisplayId, _createdDisplayId, StringComparison.Ordinal) ||
        MacOSDisplayContractProvider.ShouldRequestExtendedRange(contract) != _native.ExtendedRangeRequested;

    private void ThrowIfRecreationRequired()
    {
        if (_requiresRecreation)
        {
            throw new MacOSPresenterRecreationRequiredException(
                "The macOS screen, encoding or headroom mode changed; recreate the native presenter.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record NativeCreation(DisplayContract Contract, IMacOSNativePresenterSession Session);
}

public sealed class MacOSPresenterRecreationRequiredException : InvalidOperationException
{
    public MacOSPresenterRecreationRequiredException(string message) : base(message) { }
}

/// <summary>Raised when the contract is an emergency: there is nothing a tagged surface could show.</summary>
public sealed class MacOSPresenterUnavailableException : InvalidOperationException
{
    public MacOSPresenterUnavailableException(string message) : base(message) { }
}
