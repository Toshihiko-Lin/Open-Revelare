using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.Presentation.Win32.Native;

internal enum NativePresenterMode : uint
{
    Invalid = 0,
    AdvancedColor = 1,
    Legacy = 2,
}

internal enum NativePresenterResult : int
{
    Ok = 0,
    InvalidArgument = -1,
    InvalidMode = -2,
    InvalidSize = -3,
    InvalidRowPitch = -4,
    BufferTooSmall = -5,
    ContractModeMismatch = -6,
    ContractDisplayMismatch = -7,
    StaleRevision = -8,
    WindowFailure = -9,
    D3dFailure = -10,
    ColorSpaceFailure = -11,
    DeviceRemoved = -12,
    NotInitialized = -13,
    InternalFailure = -14,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePresentationContract
{
    internal readonly uint StructSize;
    internal readonly NativePresenterMode Mode;
    internal readonly nint DisplayIdUtf8;
    internal readonly ulong Revision;

    internal NativePresentationContract(NativePresenterMode mode, nint displayIdUtf8, ulong revision)
    {
        StructSize = checked((uint)Marshal.SizeOf<NativePresentationContract>());
        Mode = mode;
        DisplayIdUtf8 = displayIdUtf8;
        Revision = revision;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDiagnostics
{
    internal const int DisplayIdCapacity = 256;

    internal uint StructSize;
    internal uint AbiVersion;
    internal NativePresenterResult LastResult;
    internal NativePresenterMode Mode;
    internal uint Width;
    internal uint Height;
    internal uint DxgiFormat;
    internal uint DxgiColorSpace;
    internal uint ColorSpaceSupport;
    internal uint ColorSpaceWasSet;
    internal uint FeatureLevel;
    internal uint UsingWarp;
    internal int CreateDeviceHResult;
    internal int CreateSwapChainHResult;
    internal int CheckColorSpaceHResult;
    internal int SetColorSpaceHResult;
    internal int ResizeBuffersHResult;
    internal int MapHResult;
    internal int PresentHResult;
    internal int DeviceRemovedReason;
    internal uint AdapterLuidLow;
    internal int AdapterLuidHigh;
    internal ulong ChildHwnd;
    internal ulong SuccessfulPresentCount;
    internal ulong RejectedPresentCount;
    internal ulong LastContractRevision;
    internal uint DisplayIdWasTruncated;
    internal fixed byte LastDisplayIdUtf8[DisplayIdCapacity];

    internal static NativeDiagnostics Create() => new()
    {
        StructSize = checked((uint)sizeof(NativeDiagnostics)),
    };

    internal string GetDisplayId()
    {
        fixed (byte* value = LastDisplayIdUtf8)
        {
            int length = 0;
            while (length < DisplayIdCapacity && value[length] != 0) length++;
            return Encoding.UTF8.GetString(value, length);
        }
    }
}

internal sealed class SafeOrwpPresenterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeOrwpPresenterHandle() : base(ownsHandle: true) { }

    internal SafeOrwpPresenterHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        try
        {
            NativeMethods.Destroy(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeMethods
{
    static NativeMethods() => Win32NativeLibraryResolver.Install();

    [DllImport(Win32NativeLibraryResolver.LibraryName, EntryPoint = "orwp_create",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativePresenterResult Create(
        nint parentHwnd,
        NativePresenterMode mode,
        uint width,
        uint height,
        out nint presenter);

    [DllImport(Win32NativeLibraryResolver.LibraryName, EntryPoint = "orwp_resize",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativePresenterResult Resize(
        SafeOrwpPresenterHandle presenter,
        uint width,
        uint height);

    [DllImport(Win32NativeLibraryResolver.LibraryName, EntryPoint = "orwp_present",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativePresenterResult Present(
        SafeOrwpPresenterHandle presenter,
        nint bytes,
        nuint byteCount,
        uint rowPitch,
        uint width,
        uint height,
        in NativePresentationContract frameContract,
        in NativePresentationContract currentContract);

    [DllImport(Win32NativeLibraryResolver.LibraryName, EntryPoint = "orwp_query_diagnostics",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativePresenterResult QueryDiagnostics(
        SafeOrwpPresenterHandle presenter,
        ref NativeDiagnostics diagnostics);

    [DllImport(Win32NativeLibraryResolver.LibraryName, EntryPoint = "orwp_destroy",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint presenter);
}
