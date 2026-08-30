using System.Runtime.InteropServices;
using System.Text;

namespace OpenRevelare.Presentation.Win32.Interop;

internal enum DisplayConfigDeviceInfoType : int
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetAdvancedColorInfo = 9,
    GetSdrWhiteLevel = 11,
    GetAdvancedColorInfo2 = 15,
}

internal enum DisplayConfigColorEncoding : uint
{
    Rgb = 0,
    YCbCr444 = 1,
    YCbCr422 = 2,
    YCbCr420 = 3,
    Intensity = 4,
}

internal enum DisplayConfigAdvancedColorMode : uint
{
    Sdr = 0,
    WideColorGamut = 1,
    HighDynamicRange = 2,
}

internal enum WcsProfileManagementScope : int
{
    SystemWide = 0,
    CurrentUser = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32Luid
{
    internal uint LowPart;
    internal int HighPart;
    internal string DiagnosticValue => $"0x{unchecked((uint)HighPart):X8}:0x{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32Rect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    internal uint Size;
    internal Win32Rect Monitor;
    internal Win32Rect Work;
    internal uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDeviceInfoHeader
{
    internal DisplayConfigDeviceInfoType Type;
    internal uint Size;
    internal Win32Luid AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSourceInfo
{
    internal Win32Luid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    internal uint Numerator;
    internal uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTargetInfo
{
    internal Win32Luid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint OutputTechnology;
    internal uint Rotation;
    internal uint Scaling;
    internal DisplayConfigRational RefreshRate;
    internal uint ScanLineOrdering;
    internal int TargetAvailable;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    internal DisplayConfigPathSourceInfo SourceInfo;
    internal DisplayConfigPathTargetInfo TargetInfo;
    internal uint Flags;
}

// Only QueryDisplayConfig writes this array; the probe does not inspect its union. The native
// DISPLAYCONFIG_MODE_INFO is 64 bytes in the Windows 11 SDK on x64 and all members are 8-byte or
// less aligned.
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct DisplayConfigModeInfo
{
    [FieldOffset(0)] internal uint InfoType;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigSourceDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string ViewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigTargetDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Flags;
    internal uint OutputTechnology;
    internal ushort EdidManufactureId;
    internal ushort EdidProductCodeId;
    internal uint ConnectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string MonitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string MonitorDevicePath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigAdvancedColorInfo2
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Value;
    internal DisplayConfigColorEncoding ColorEncoding;
    internal uint BitsPerColorChannel;
    internal DisplayConfigAdvancedColorMode ActiveColorMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigAdvancedColorInfo
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Value;
    internal DisplayConfigColorEncoding ColorEncoding;
    internal uint BitsPerColorChannel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSdrWhiteLevel
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint SdrWhiteLevel;
}

internal static class Win32DisplayNativeMethods
{
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint QueryOnlyActivePaths = 0x00000002;
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ColorProfileTypeIcc = 0;
    internal const int ColorProfileSubtypeStandardDisplay = 7;

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    internal static extern int DisplayConfigGetSourceName(ref DisplayConfigSourceDeviceName packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    internal static extern int DisplayConfigGetTargetName(ref DisplayConfigTargetDeviceName packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    internal static extern int DisplayConfigGetAdvancedColorInfo2(ref DisplayConfigAdvancedColorInfo2 packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    internal static extern int DisplayConfigGetAdvancedColorInfo(ref DisplayConfigAdvancedColorInfo packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    internal static extern int DisplayConfigGetSdrWhiteLevel(ref DisplayConfigSdrWhiteLevel packet);

    [DllImport("Mscms.dll", ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayUserScope(
        Win32Luid targetAdapterId,
        uint sourceId,
        out WcsProfileManagementScope scope);

    [DllImport("Mscms.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int ColorProfileGetDisplayDefault(
        WcsProfileManagementScope scope,
        Win32Luid targetAdapterId,
        uint sourceId,
        int profileType,
        int profileSubtype,
        out nint profileName);

    [DllImport("Mscms.dll", EntryPoint = "GetColorDirectoryW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetColorDirectory(
        string? machineName,
        StringBuilder? buffer,
        ref uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern nint LocalFree(nint memory);
}
