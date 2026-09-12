using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.Presentation.MacOS.Native;

/// <summary>
/// Result codes of the <c>orwm_*</c> C ABI. Same numbering scheme as the Win32 shim so the two
/// diagnostics read alike; the macOS-only conditions take the tail.
/// </summary>
internal enum NativePresenterResult : int
{
    Ok = 0,
    InvalidArgument = -1,
    InvalidSize = -3,
    InvalidRowPitch = -4,
    BufferTooSmall = -5,
    ContractDisplayMismatch = -7,
    StaleRevision = -8,
    ViewFailure = -9,
    MetalFailure = -10,
    ColorSpaceFailure = -11,
    NotInitialized = -13,
    InternalFailure = -14,
    DrawableUnavailable = -15,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePresentationContract
{
    internal readonly uint StructSize;
    internal readonly uint RequestExtendedRange;
    internal readonly nint DisplayIdUtf8;
    internal readonly ulong Revision;

    internal NativePresentationContract(bool requestExtendedRange, nint displayIdUtf8, ulong revision)
    {
        StructSize = checked((uint)Marshal.SizeOf<NativePresentationContract>());
        RequestExtendedRange = requestExtendedRange ? 1u : 0u;
        DisplayIdUtf8 = displayIdUtf8;
        Revision = revision;
    }
}

/// <summary>What <c>orwm_probe</c> fills. Doubles because AppKit hands these out as CGFloat.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayProbe
{
    internal const int NameCapacity = 128;

    internal uint StructSize;
    internal uint AbiVersion;
    internal int LastResult;
    internal uint DirectDisplayId;
    internal double BackingScaleFactor;
    internal double MaximumEdrValue;
    internal double MaximumPotentialEdrValue;
    internal double MaximumReferenceEdrValue;
    internal fixed byte LocalizedNameUtf8[NameCapacity];
    internal fixed byte ColorSpaceNameUtf8[NameCapacity];

    internal static NativeDisplayProbe Create() => new()
    {
        StructSize = checked((uint)sizeof(NativeDisplayProbe)),
    };

    internal string LocalizedName
    {
        get { fixed (byte* p = LocalizedNameUtf8) return ReadUtf8(p, NameCapacity); }
    }

    internal string ColorSpaceName
    {
        get { fixed (byte* p = ColorSpaceNameUtf8) return ReadUtf8(p, NameCapacity); }
    }

    private static string ReadUtf8(byte* value, int capacity)
    {
        int length = 0;
        while (length < capacity && value[length] != 0) length++;
        return Encoding.UTF8.GetString(value, length);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDiagnostics
{
    internal const int DisplayIdCapacity = 256;

    internal uint StructSize;
    internal uint AbiVersion;
    internal int LastResult;
    internal uint ExtendedRangeRequested;
    internal uint Width;
    internal uint Height;
    internal uint PixelFormat;
    internal uint ColorSpaceWasSet;
    internal uint LayerIsExtendedRange;
    internal ulong SuccessfulPresentCount;
    internal ulong RejectedPresentCount;
    internal ulong DroppedDrawableCount;
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

internal sealed class SafeOrwmPresenterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeOrwmPresenterHandle() : base(ownsHandle: true) { }

    internal SafeOrwmPresenterHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

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

/// <summary>
/// The <c>orwm_*</c> exports. Same five-call shape as the Win32 shim plus <c>orwm_probe</c>,
/// because on macOS the display facts (EDR headroom, colour space) come from AppKit objects
/// rather than a system API the managed side can reach on its own.
/// </summary>
internal static class NativeMethods
{
    static NativeMethods() => MacOSNativeLibraryResolver.Install();

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_probe", ExactSpelling = true)]
    internal static extern NativePresenterResult Probe(nint viewHandle, ref NativeDisplayProbe probe);

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_create", ExactSpelling = true)]
    internal static extern NativePresenterResult Create(
        nint parentView,
        uint requestExtendedRange,
        uint width,
        uint height,
        out nint presenter);

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_resize", ExactSpelling = true)]
    internal static extern NativePresenterResult Resize(
        SafeOrwmPresenterHandle presenter,
        uint width,
        uint height,
        double scale);

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_present", ExactSpelling = true)]
    internal static extern NativePresenterResult Present(
        SafeOrwmPresenterHandle presenter,
        nint bytes,
        nuint byteCount,
        uint rowPitch,
        uint width,
        uint height,
        in NativePresentationContract frameContract,
        in NativePresentationContract currentContract);

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_query_diagnostics", ExactSpelling = true)]
    internal static extern NativePresenterResult QueryDiagnostics(
        SafeOrwmPresenterHandle presenter,
        ref NativeDiagnostics diagnostics);

    [DllImport(MacOSNativeLibraryResolver.LibraryName, EntryPoint = "orwm_destroy", ExactSpelling = true)]
    internal static extern void Destroy(nint presenter);
}

/// <summary>
/// Locates the shim beside the application, never by bare name search.
///
/// <para>
/// DELIBERATELY SMALLER THAN ITS WIN32 SIBLING. The Windows resolver also verifies a build
/// manifest (hash, exports, source identity) because that DLL is produced by a bespoke MSBuild
/// step outside the solution. The macOS dylib rides <c>packaging/macos</c>'s existing copy /
/// install-name / codesign flow, and code signature is the integrity check on that platform; a
/// second, application-level hash would verify the file and not the image dyld actually mapped
/// (the same limit §16 records for lcms2). If a manifest is wanted later it slots in here.
/// </para>
/// </summary>
internal static class MacOSNativeLibraryResolver
{
    internal const string LibraryName = "libOpenRevelare.Presentation.MacOS.Native.dylib";
    internal const int NativeAbiVersion = 1;

    private static int _installed;

    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(MacOSNativeLibraryResolver).Assembly, Resolve);
    }

    internal static IReadOnlyList<string> CandidatePaths(string appBaseDirectory, string assemblyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);

        var paths = new List<string>(6);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Add(appBaseDirectory, LibraryName);
        Add(appBaseDirectory, "runtimes", "osx-arm64", "native", LibraryName);
        Add(appBaseDirectory, "runtimes", "osx-x64", "native", LibraryName);
        Add(assemblyDirectory, LibraryName);
        Add(assemblyDirectory, "runtimes", "osx-arm64", "native", LibraryName);
        Add(assemblyDirectory, "runtimes", "osx-x64", "native", LibraryName);
        return paths;

        void Add(params string[] components)
        {
            string candidate = Path.GetFullPath(Path.Combine(components));
            if (!Path.IsPathRooted(candidate) ||
                !string.Equals(Path.GetFileName(candidate), LibraryName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native presenter candidate escaped the strict filename policy.");
            }
            if (seen.Add(candidate)) paths.Add(candidate);
        }
    }

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return nint.Zero;

        string assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        foreach (string candidate in CandidatePaths(AppContext.BaseDirectory, assemblyDirectory))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
                return handle;
        }

        // Returning zero lets the runtime raise DllNotFoundException with the bare name, which the
        // presenter factory turns into "presenter unavailable" — the same path the GUI takes on a
        // platform with no native shim at all.
        return nint.Zero;
    }
}
