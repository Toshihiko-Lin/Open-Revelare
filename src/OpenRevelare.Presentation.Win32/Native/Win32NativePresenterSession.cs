using System.Text;
using OpenRevelare.Presentation;

namespace OpenRevelare.Presentation.Win32.Native;

public sealed record WindowsNativePresenterDiagnostics(
    int AbiVersion,
    int LastResult,
    string Mode,
    PixelSize Size,
    uint DxgiFormat,
    uint DxgiColorSpace,
    uint ColorSpaceSupport,
    bool ColorSpaceWasSet,
    uint FeatureLevel,
    bool UsingWarp,
    int CreateDeviceHResult,
    int CreateSwapChainHResult,
    int CheckColorSpaceHResult,
    int SetColorSpaceHResult,
    int ResizeBuffersHResult,
    int MapHResult,
    int PresentHResult,
    int DeviceRemovedReason,
    string AdapterLuid,
    ulong ChildHwnd,
    ulong SuccessfulPresentCount,
    ulong RejectedPresentCount,
    long LastContractRevision,
    string LastDisplayId,
    bool DisplayIdWasTruncated);

public sealed class Win32NativePresenterException : Exception
{
    public int ResultCode { get; }
    public WindowsNativePresenterDiagnostics? Diagnostics { get; }

    internal Win32NativePresenterException(
        string operation,
        NativePresenterResult result,
        WindowsNativePresenterDiagnostics? diagnostics = null)
        : base($"Native Win32 presenter {operation} failed with {(int)result} ({result}).")
    {
        ResultCode = (int)result;
        Diagnostics = diagnostics;
    }
}

internal interface IWin32NativePresenterFactory
{
    IWin32NativePresenterSession Create(nint parentHwnd, NativePresenterMode mode, PixelSize size);
}

internal interface IWin32NativePresenterSession : IDisposable
{
    NativePresenterMode Mode { get; }
    void Resize(PixelSize size);
    void Present(
        ReadOnlySpan<byte> bytes,
        uint rowPitch,
        PixelSize size,
        string frameDisplayId,
        long frameRevision,
        string currentDisplayId,
        long currentRevision);
    WindowsNativePresenterDiagnostics QueryDiagnostics();
}

internal sealed class Win32NativePresenterFactory : IWin32NativePresenterFactory
{
    internal static Win32NativePresenterFactory Instance { get; } = new();

    private Win32NativePresenterFactory() { }

    public IWin32NativePresenterSession Create(nint parentHwnd, NativePresenterMode mode, PixelSize size)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native preview presenter is Windows-only.");
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The native preview presenter requires an x64 process.");
        if (parentHwnd == nint.Zero) throw new ArgumentException("Parent HWND is null.", nameof(parentHwnd));
        ValidateSize(size);
        if (mode is not (NativePresenterMode.AdvancedColor or NativePresenterMode.Legacy))
            throw new ArgumentOutOfRangeException(nameof(mode));

        NativePresenterResult result = NativeMethods.Create(
            parentHwnd, mode, checked((uint)size.Width), checked((uint)size.Height), out nint value);
        if (result != NativePresenterResult.Ok)
        {
            if (value != nint.Zero) NativeMethods.Destroy(value);
            throw new Win32NativePresenterException("create", result);
        }
        if (value == nint.Zero)
            throw new Win32NativePresenterException("create", NativePresenterResult.InternalFailure);
        return new Win32NativePresenterSession(new SafeOrwpPresenterHandle(value), mode);
    }

    internal static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Presentation dimensions must be positive.");
    }
}

internal sealed class Win32NativePresenterSession : IWin32NativePresenterSession
{
    private readonly object _gate = new();
    private readonly SafeOrwpPresenterHandle _handle;
    private bool _disposed;

    internal Win32NativePresenterSession(SafeOrwpPresenterHandle handle, NativePresenterMode mode)
    {
        _handle = handle;
        Mode = mode;
    }

    public NativePresenterMode Mode { get; }

    public void Resize(PixelSize size)
    {
        Win32NativePresenterFactory.ValidateSize(size);
        lock (_gate)
        {
            ThrowIfDisposed();
            NativePresenterResult result = NativeMethods.Resize(
                _handle, checked((uint)size.Width), checked((uint)size.Height));
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
        Win32NativePresenterFactory.ValidateSize(size);

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
                    Mode, (nint)frameIdPointer, checked((ulong)frameRevision));
                var currentContract = new NativePresentationContract(
                    Mode, (nint)currentIdPointer, checked((ulong)currentRevision));
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

    public WindowsNativePresenterDiagnostics QueryDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeDiagnostics diagnostics = NativeDiagnostics.Create();
            NativePresenterResult result = NativeMethods.QueryDiagnostics(_handle, ref diagnostics);
            if (result != NativePresenterResult.Ok)
                throw new Win32NativePresenterException("query diagnostics", result);
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
        WindowsNativePresenterDiagnostics? diagnostics = null;
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
        throw new Win32NativePresenterException(operation, result, diagnostics);
    }

    private static WindowsNativePresenterDiagnostics Map(NativeDiagnostics value) => new(
        checked((int)value.AbiVersion),
        (int)value.LastResult,
        value.Mode.ToString(),
        new PixelSize(checked((int)value.Width), checked((int)value.Height)),
        value.DxgiFormat,
        value.DxgiColorSpace,
        value.ColorSpaceSupport,
        value.ColorSpaceWasSet != 0,
        value.FeatureLevel,
        value.UsingWarp != 0,
        value.CreateDeviceHResult,
        value.CreateSwapChainHResult,
        value.CheckColorSpaceHResult,
        value.SetColorSpaceHResult,
        value.ResizeBuffersHResult,
        value.MapHResult,
        value.PresentHResult,
        value.DeviceRemovedReason,
        $"0x{unchecked((uint)value.AdapterLuidHigh):X8}:0x{value.AdapterLuidLow:X8}",
        value.ChildHwnd,
        value.SuccessfulPresentCount,
        value.RejectedPresentCount,
        checked((long)value.LastContractRevision),
        value.GetDisplayId(),
        value.DisplayIdWasTruncated != 0);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
