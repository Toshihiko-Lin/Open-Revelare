using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.ColorManagement;

internal static class LittleCmsNative
{
    internal const string LibraryName = "lcms2";
    internal const int RequiredEncodedVersion = 2190;
    internal const uint RgbSignature = 0x52474220; // 'RGB '
    internal const uint XyzSignature = 0x58595A20; // 'XYZ '
    internal const uint LabSignature = 0x4C616220; // 'Lab '

    private static readonly object ResolverGate = new();
    private static string? _configuredNativePath;
    private static IntPtr _nativeHandle;

    /// <summary>
    /// Binds every P/Invoke in this assembly to the one manifest-verified application artifact.
    /// Loading by absolute path before installing the resolver is intentional: the resolver never
    /// delegates to the platform's name search and therefore cannot silently pick up system lcms.
    /// </summary>
    internal static void Configure(string nativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativePath);
        string exactPath = Path.GetFullPath(nativePath);

        lock (ResolverGate)
        {
            if (_configuredNativePath is not null)
            {
                if (!string.Equals(_configuredNativePath, exactPath, PathComparison))
                {
                    throw new ColorManagementException(
                        $"LittleCMS is already bound to app-owned artifact " +
                        $"'{Path.GetFileName(_configuredNativePath)}'; refusing to rebind to " +
                        $"'{Path.GetFileName(exactPath)}'.");
                }
                return;
            }

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(exactPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new ColorManagementException(
                    $"Could not load the manifest-verified app-owned LittleCMS artifact " +
                    $"'{Path.GetFileName(exactPath)}': {ex.Message}", ex);
            }

            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(LittleCmsNative).Assembly,
                    (libraryName, _, _) => libraryName.Equals(LibraryName, StringComparison.Ordinal)
                        ? handle
                        : IntPtr.Zero);
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }

            _nativeHandle = handle;
            _configuredNativePath = exactPath;
        }
    }

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void LogErrorHandler(IntPtr context, uint errorCode, IntPtr text);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int cmsGetEncodedCMMversion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern IntPtr cmsCreateContext(IntPtr plugin, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void cmsDeleteContext(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern IntPtr cmsGetContextUserData(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void cmsSetLogErrorHandlerTHR(IntPtr context, LogErrorHandler handler);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern double cmsSetAdaptationStateTHR(IntPtr context, double adaptationState);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern unsafe IntPtr cmsOpenProfileFromMemTHR(IntPtr context, byte* memory, uint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool cmsCloseProfile(IntPtr profile);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern uint cmsGetColorSpace(IntPtr profile);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern uint cmsGetPCS(IntPtr profile);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern uint cmsGetEncodedICCversion(IntPtr profile);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int cmsChannelsOfColorSpace(uint colorSpace);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern IntPtr cmsCreateTransformTHR(
        IntPtr context,
        IntPtr inputProfile,
        uint inputFormat,
        IntPtr outputProfile,
        uint outputFormat,
        uint intent,
        uint flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void cmsDeleteTransform(IntPtr transform);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern unsafe void cmsDoTransform(
        IntPtr transform,
        float* inputBuffer,
        float* outputBuffer,
        uint pixelCount);

    /// <summary>The same entry point for a transform created 16-bit to 16-bit; the buffers
    /// are interpreted by the transform's own formats, not by this signature.</summary>
    [DllImport(LibraryName, EntryPoint = "cmsDoTransform", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern unsafe void cmsDoTransform16(
        IntPtr transform,
        ushort* inputBuffer,
        ushort* outputBuffer,
        uint pixelCount);
}

internal sealed class LittleCmsContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal LittleCmsContextHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        LittleCmsNative.cmsDeleteContext(handle);
        return true;
    }
}

internal sealed class LittleCmsTransformHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal LittleCmsTransformHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        LittleCmsNative.cmsDeleteTransform(handle);
        return true;
    }
}
