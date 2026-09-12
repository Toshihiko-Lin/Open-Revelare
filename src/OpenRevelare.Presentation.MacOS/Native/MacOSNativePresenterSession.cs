using System.Text;
using OpenRevelare.Presentation;

namespace OpenRevelare.Presentation.MacOS.Native;

public sealed record MacOSNativePresenterDiagnostics(
    int AbiVersion,
    int LastResult,
    bool ExtendedRangeRequested,
    PixelSize Size,
    uint PixelFormat,
    bool ColorSpaceWasSet,
    bool LayerIsExtendedRange,
    ulong SuccessfulPresentCount,
    ulong RejectedPresentCount,
    ulong DroppedDrawableCount,
    long LastContractRevision,
    string LastDisplayId,
    bool DisplayIdWasTruncated);

public sealed class MacOSNativePresenterException : Exception
{
    public int ResultCode { get; }
    public MacOSNativePresenterDiagnostics? Diagnostics { get; }

    internal MacOSNativePresenterException(
        string operation,
        NativePresenterResult result,
        MacOSNativePresenterDiagnostics? diagnostics = null)
        : base($"Native macOS presenter {operation} failed with {(int)result} ({result}).")
    {
        ResultCode = (int)result;
        Diagnostics = diagnostics;
    }
}

internal interface IMacOSNativePresenterFactory
{
    IMacOSNativePresenterSession Create(nint parentView, bool requestExtendedRange, PixelSize size);
}

/// <summary>
/// The seam the presenter talks through. Production wraps the <c>orwm_*</c> shim; tests supply a
/// fake and never touch AppKit — the same arrangement as the Win32 session, and the reason the
/// contract logic can be proven on a machine without a Mac.
/// </summary>
internal interface IMacOSNativePresenterSession : IDisposable
{
    bool ExtendedRangeRequested { get; }
    void Resize(PixelSize size, double scale);
    void Present(
        ReadOnlySpan<byte> bytes,
        uint rowPitch,
        PixelSize size,
        string frameDisplayId,
        long frameRevision,
        string currentDisplayId,
        long currentRevision);
    MacOSNativePresenterDiagnostics QueryDiagnostics();
}

internal sealed class MacOSNativePresenterFactory : IMacOSNativePresenterFactory
{
    internal static MacOSNativePresenterFactory Instance { get; } = new();

    private MacOSNativePresenterFactory() { }

    public IMacOSNativePresenterSession Create(nint parentView, bool requestExtendedRange, PixelSize size)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The native preview presenter is macOS-only.");
        if (parentView == nint.Zero) throw new ArgumentException("Parent NSView is null.", nameof(parentView));
        ValidateSize(size);

        NativePresenterResult result = NativeMethods.Create(
            parentView,
            requestExtendedRange ? 1u : 0u,
            checked((uint)size.Width),
            checked((uint)size.Height),
            out nint value);
        if (result != NativePresenterResult.Ok)
        {
            if (value != nint.Zero) NativeMethods.Destroy(value);
            throw new MacOSNativePresenterException("create", result);
        }
        if (value == nint.Zero)
            throw new MacOSNativePresenterException("create", NativePresenterResult.InternalFailure);
        return new MacOSNativePresenterSession(new SafeOrwmPresenterHandle(value), requestExtendedRange);
    }

    internal static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Presentation dimensions must be positive.");
    }
}

internal sealed class MacOSNativePresenterSession : IMacOSNativePresenterSession
{
    private readonly object _gate = new();
    private readonly SafeOrwmPresenterHandle _handle;
    private bool _disposed;

    internal MacOSNativePresenterSession(SafeOrwmPresenterHandle handle, bool extendedRangeRequested)
    {
        _handle = handle;
        ExtendedRangeRequested = extendedRangeRequested;
    }

    public bool ExtendedRangeRequested { get; }

    public void Resize(PixelSize size, double scale)
    {
        MacOSNativePresenterFactory.ValidateSize(size);
        if (!double.IsFinite(scale) || scale <= 0d)
            throw new ArgumentOutOfRangeException(nameof(scale), "Backing scale must be finite and positive.");
        lock (_gate)
        {
            ThrowIfDisposed();
            NativePresenterResult result = NativeMethods.Resize(
                _handle, checked((uint)size.Width), checked((uint)size.Height), scale);
            ThrowIfFailed("resize", result);
        }
    }

    public unsafe void Present(
        ReadOnlySpan<byte> bytes,
        uint rowPitch,
        PixelSize size,
        string frameDisplayId,
        long frameRevision,
        string currentDisplayId,
        long currentRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameDisplayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayId);
        if (frameRevision < 0) throw new ArgumentOutOfRangeException(nameof(frameRevision));
        if (currentRevision < 0) throw new ArgumentOutOfRangeException(nameof(currentRevision));
        MacOSNativePresenterFactory.ValidateSize(size);

        byte[] frameId = Encoding.UTF8.GetBytes(frameDisplayId + '\0');
        byte[] currentId = Encoding.UTF8.GetBytes(currentDisplayId + '\0');

        lock (_gate)
        {
            ThrowIfDisposed();
            fixed (byte* bytesPointer = bytes)
            fixed (byte* frameIdPointer = frameId)
            fixed (byte* currentIdPointer = currentId)
            {
                var frameContract = new NativePresentationContract(
                    ExtendedRangeRequested, (nint)frameIdPointer, checked((ulong)frameRevision));
                var currentContract = new NativePresentationContract(
                    ExtendedRangeRequested, (nint)currentIdPointer, checked((ulong)currentRevision));
                NativePresenterResult result = NativeMethods.Present(
                    _handle,
                    (nint)bytesPointer,
                    checked((nuint)bytes.Length),
                    rowPitch,
                    checked((uint)size.Width),
                    checked((uint)size.Height),
                    in frameContract,
                    in currentContract);
                ThrowIfFailed("present", result);
            }
        }
    }

    public MacOSNativePresenterDiagnostics QueryDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeDiagnostics diagnostics = NativeDiagnostics.Create();
            NativePresenterResult result = NativeMethods.QueryDiagnostics(_handle, ref diagnostics);
            if (result != NativePresenterResult.Ok)
                throw new MacOSNativePresenterException("query diagnostics", result);
            return Map(diagnostics);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }

    private void ThrowIfFailed(string operation, NativePresenterResult result)
    {
        if (result == NativePresenterResult.Ok) return;
        MacOSNativePresenterDiagnostics? diagnostics = null;
        try
        {
            NativeDiagnostics native = NativeDiagnostics.Create();
            if (NativeMethods.QueryDiagnostics(_handle, ref native) == NativePresenterResult.Ok)
                diagnostics = Map(native);
        }
        catch
        {
            // Preserve the original native failure if diagnostics are unavailable.
        }
        throw new MacOSNativePresenterException(operation, result, diagnostics);
    }

    private static MacOSNativePresenterDiagnostics Map(NativeDiagnostics value) => new(
        checked((int)value.AbiVersion),
        value.LastResult,
        value.ExtendedRangeRequested != 0,
        new PixelSize(checked((int)value.Width), checked((int)value.Height)),
        value.PixelFormat,
        value.ColorSpaceWasSet != 0,
        value.LayerIsExtendedRange != 0,
        value.SuccessfulPresentCount,
        value.RejectedPresentCount,
        value.DroppedDrawableCount,
        checked((long)value.LastContractRevision),
        value.GetDisplayId(),
        value.DisplayIdWasTruncated != 0);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>The production probe: <c>orwm_probe</c> against the view's window screen.</summary>
internal sealed class MacOSNativeDisplayProbe : IMacOSDisplayProbe
{
    internal static MacOSNativeDisplayProbe Instance { get; } = new();

    private MacOSNativeDisplayProbe() { }

    public MacOSDisplayProbeSnapshot Probe(nint viewHandle)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The macOS display probe is macOS-only.");
        if (viewHandle == nint.Zero)
            throw new ArgumentException("NSView handle is null.", nameof(viewHandle));

        NativeDisplayProbe native = NativeDisplayProbe.Create();
        NativePresenterResult result = NativeMethods.Probe(viewHandle, ref native);
        if (result != NativePresenterResult.Ok)
            throw new MacOSNativePresenterException("probe", result);
        if (native.AbiVersion != MacOSNativeLibraryResolver.NativeAbiVersion)
        {
            throw new InvalidOperationException(
                $"Native macOS presenter ABI {native.AbiVersion} does not match the managed ABI {MacOSNativeLibraryResolver.NativeAbiVersion}.");
        }

        // The display id must survive a screen swap between two identical monitors, so it is the
        // CGDirectDisplayID plus the name — neither alone is stable enough across hot-plug.
        string displayId = $"macos:display-{native.DirectDisplayId}|{native.LocalizedName}";
        return new MacOSDisplayProbeSnapshot(
            displayId,
            native.DirectDisplayId,
            NullIfEmpty(native.LocalizedName),
            NullIfEmpty(native.ColorSpaceName),
            native.BackingScaleFactor,
            native.MaximumEdrValue,
            native.MaximumPotentialEdrValue,
            native.MaximumReferenceEdrValue,
            IsReliable: native.DirectDisplayId != 0,
            native.DirectDisplayId != 0 ? null : "AppKit reported no display for the preview view's window.");
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
