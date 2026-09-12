using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using OpenRevelare.Presentation.Win32.Interop;

namespace OpenRevelare.Presentation.Win32;

internal sealed class Win32DisplayProbe : IWindowsDisplayProbe
{
    // Guards the monitor-profile cache below. WindowsDisplayEnvironment already serializes probes
    // behind its refresh gate; the lock keeps that from being a load-bearing assumption of a type
    // whose interface says nothing about threading.
    private readonly object _monitorProfileGate = new();
    private MonitorProfileData? _cachedMonitorProfile;
    private string? _cachedMonitorProfilePath;
    private string? _cachedMonitorProfileScope;
    private long _cachedMonitorProfileLength;
    private DateTime _cachedMonitorProfileWrittenUtc;

    public WindowsDisplayProbeSnapshot Probe(nint windowHwnd)
    {
        if (windowHwnd == nint.Zero) throw new ArgumentException("Window HWND is null.", nameof(windowHwnd));

        nint monitor = Win32DisplayNativeMethods.MonitorFromWindow(
            windowHwnd, Win32DisplayNativeMethods.MonitorDefaultToNearest);
        if (monitor == nint.Zero) throw new Win32Exception("MonitorFromWindow returned no monitor.");

        var monitorInfo = new MonitorInfoEx
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInfoEx>()),
            DeviceName = string.Empty,
        };
        if (!Win32DisplayNativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfoW failed.");

        uint windowDpi = Win32DisplayNativeMethods.GetDpiForWindow(windowHwnd);
        if (windowDpi == 0)
            throw new InvalidOperationException("GetDpiForWindow returned zero for a valid window.");

        ActiveDisplayPath activePath = ResolveActivePath(monitorInfo.DeviceName);
        string stableId = BuildStableDisplayId(activePath);
        AdvancedColorState advanced = ReadAdvancedColor(activePath);
        (uint? sdrRaw, float sdrNits, string? sdrFailure) = ReadSdrWhite(activePath);

        MonitorProfileData? profile = null;
        string? profileFailure = null;
        if (advanced.IsReliable && !advanced.Active)
        {
            (profile, profileFailure) = ReadMonitorProfile(activePath);
        }

        return new WindowsDisplayProbeSnapshot(
            stableId,
            monitorInfo.DeviceName,
            NullIfWhiteSpace(activePath.MonitorDevicePath),
            NullIfWhiteSpace(activePath.MonitorFriendlyName),
            activePath.Source.AdapterId.DiagnosticValue,
            activePath.Source.Id,
            activePath.Target.Id,
            windowDpi,
            advanced,
            sdrRaw,
            sdrNits,
            sdrFailure,
            profile,
            profileFailure,
            null);
    }

    private static ActiveDisplayPath ResolveActivePath(string gdiDeviceName)
    {
        DisplayConfigPathInfo[] paths = QueryActivePaths();
        foreach (DisplayConfigPathInfo path in paths)
        {
            var sourceName = new DisplayConfigSourceDeviceName
            {
                Header = Header(
                    DisplayConfigDeviceInfoType.GetSourceName,
                    path.SourceInfo.AdapterId,
                    path.SourceInfo.Id,
                    Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                ViewGdiDeviceName = string.Empty,
            };
            int sourceResult = Win32DisplayNativeMethods.DisplayConfigGetSourceName(ref sourceName);
            if (sourceResult != Win32DisplayNativeMethods.ErrorSuccess ||
                !string.Equals(
                    sourceName.ViewGdiDeviceName,
                    gdiDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetName = new DisplayConfigTargetDeviceName
            {
                Header = Header(
                    DisplayConfigDeviceInfoType.GetTargetName,
                    path.TargetInfo.AdapterId,
                    path.TargetInfo.Id,
                    Marshal.SizeOf<DisplayConfigTargetDeviceName>()),
                MonitorFriendlyDeviceName = string.Empty,
                MonitorDevicePath = string.Empty,
            };
            int targetResult = Win32DisplayNativeMethods.DisplayConfigGetTargetName(ref targetName);
            if (targetResult != Win32DisplayNativeMethods.ErrorSuccess)
                throw DisplayConfigException("DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME", targetResult);

            return new ActiveDisplayPath(
                path.SourceInfo,
                path.TargetInfo,
                sourceName.ViewGdiDeviceName,
                targetName.MonitorDevicePath,
                targetName.MonitorFriendlyDeviceName);
        }

        throw new InvalidOperationException(
            $"No active QueryDisplayConfig source matched {gdiDeviceName}.");
    }

    private static DisplayConfigPathInfo[] QueryActivePaths()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int sizesResult = Win32DisplayNativeMethods.GetDisplayConfigBufferSizes(
                Win32DisplayNativeMethods.QueryOnlyActivePaths,
                out uint pathCount,
                out uint modeCount);
            if (sizesResult != Win32DisplayNativeMethods.ErrorSuccess)
                throw DisplayConfigException("GetDisplayConfigBufferSizes", sizesResult);
            if (pathCount > int.MaxValue || modeCount > int.MaxValue)
                throw new InvalidOperationException("QueryDisplayConfig returned unreasonable buffer counts.");

            var paths = new DisplayConfigPathInfo[checked((int)pathCount)];
            var modes = new DisplayConfigModeInfo[checked((int)modeCount)];
            uint returnedPaths = pathCount;
            uint returnedModes = modeCount;
            int queryResult = Win32DisplayNativeMethods.QueryDisplayConfig(
                Win32DisplayNativeMethods.QueryOnlyActivePaths,
                ref returnedPaths,
                paths,
                ref returnedModes,
                modes,
                nint.Zero);
            if (queryResult == Win32DisplayNativeMethods.ErrorInsufficientBuffer) continue;
            if (queryResult != Win32DisplayNativeMethods.ErrorSuccess)
                throw DisplayConfigException("QueryDisplayConfig", queryResult);
            if (returnedPaths > checked((uint)paths.Length))
                throw new InvalidOperationException("QueryDisplayConfig returned more paths than allocated.");

            if (returnedPaths != paths.Length) Array.Resize(ref paths, checked((int)returnedPaths));
            return paths;
        }

        throw new InvalidOperationException("Display topology changed repeatedly during QueryDisplayConfig.");
    }

    private static AdvancedColorState ReadAdvancedColor(ActiveDisplayPath path)
    {
        var info2 = new DisplayConfigAdvancedColorInfo2
        {
            Header = Header(
                DisplayConfigDeviceInfoType.GetAdvancedColorInfo2,
                path.Target.AdapterId,
                path.Target.Id,
                Marshal.SizeOf<DisplayConfigAdvancedColorInfo2>()),
        };
        int info2Result = Win32DisplayNativeMethods.DisplayConfigGetAdvancedColorInfo2(ref info2);
        if (info2Result == Win32DisplayNativeMethods.ErrorSuccess)
        {
            bool modeKnown = Enum.IsDefined(info2.ActiveColorMode);
            bool supported = HasFlag(info2.Value, 0);
            bool active = HasFlag(info2.Value, 1);
            bool hdrSupported = HasFlag(info2.Value, 4);
            bool hdrUserEnabled = HasFlag(info2.Value, 5);
            bool wideSupported = HasFlag(info2.Value, 6);
            bool wideUserEnabled = HasFlag(info2.Value, 7);
            WindowsAdvancedColorMode mode = info2.ActiveColorMode switch
            {
                DisplayConfigAdvancedColorMode.Sdr => WindowsAdvancedColorMode.Sdr,
                DisplayConfigAdvancedColorMode.WideColorGamut => WindowsAdvancedColorMode.WideColorGamut,
                DisplayConfigAdvancedColorMode.HighDynamicRange => WindowsAdvancedColorMode.HighDynamicRange,
                _ => WindowsAdvancedColorMode.Unknown,
            };
            bool modeIsAdvanced = mode is
                WindowsAdvancedColorMode.WideColorGamut or WindowsAdvancedColorMode.HighDynamicRange;
            bool capabilityFlagsConsistent =
                (!hdrUserEnabled || hdrSupported) &&
                (!wideUserEnabled || wideSupported) &&
                (mode != WindowsAdvancedColorMode.HighDynamicRange ||
                    (hdrSupported && hdrUserEnabled)) &&
                (mode != WindowsAdvancedColorMode.WideColorGamut ||
                    (wideSupported && wideUserEnabled));
            bool consistent = modeKnown &&
                active == modeIsAdvanced &&
                (!active || supported) &&
                capabilityFlagsConsistent;
            return new AdvancedColorState(
                consistent,
                "QueryDisplayConfig / DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2",
                info2.Value,
                supported,
                active,
                hdrSupported,
                hdrUserEnabled,
                wideSupported,
                wideUserEnabled,
                mode,
                ColorEncodingName(info2.ColorEncoding),
                info2.BitsPerColorChannel,
                consistent
                    ? null
                    : modeKnown
                        ? "DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 returned contradictory " +
                          "supported/active/mode/capability values."
                        : $"Unknown DISPLAYCONFIG_ADVANCED_COLOR_MODE value {(uint)info2.ActiveColorMode}.");
        }

        var info = new DisplayConfigAdvancedColorInfo
        {
            Header = Header(
                DisplayConfigDeviceInfoType.GetAdvancedColorInfo,
                path.Target.AdapterId,
                path.Target.Id,
                Marshal.SizeOf<DisplayConfigAdvancedColorInfo>()),
        };
        int infoResult = Win32DisplayNativeMethods.DisplayConfigGetAdvancedColorInfo(ref info);
        if (infoResult == Win32DisplayNativeMethods.ErrorSuccess)
        {
            bool enabled = HasFlag(info.Value, 1);
            bool wideColorEnforced = HasFlag(info.Value, 2);
            bool forceDisabled = HasFlag(info.Value, 3);
            bool supported = HasFlag(info.Value, 0);
            bool consistent = !(enabled && forceDisabled) &&
                (!enabled || supported) &&
                (!wideColorEnforced || enabled);
            WindowsAdvancedColorMode mode = !enabled
                ? WindowsAdvancedColorMode.Sdr
                : wideColorEnforced
                    ? WindowsAdvancedColorMode.WideColorGamut
                    : WindowsAdvancedColorMode.HighDynamicRange;
            return new AdvancedColorState(
                consistent,
                "QueryDisplayConfig / DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (fallback)",
                info.Value,
                supported,
                enabled,
                HasFlag(info.Value, 0) && !wideColorEnforced,
                enabled && !wideColorEnforced,
                wideColorEnforced,
                enabled && wideColorEnforced,
                consistent ? mode : WindowsAdvancedColorMode.Unknown,
                ColorEncodingName(info.ColorEncoding),
                info.BitsPerColorChannel,
                consistent ? null : "Legacy Advanced Color flags are contradictory.");
        }

        string failure =
            $"Advanced Color state unavailable: Info2={ErrorCode(info2Result)}, Info={ErrorCode(infoResult)}.";
        return new AdvancedColorState(
            false,
            "QueryDisplayConfig / Advanced Color unavailable",
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            WindowsAdvancedColorMode.Unknown,
            null,
            null,
            failure);
    }

    private static (uint? Raw, float Nits, string? Failure) ReadSdrWhite(ActiveDisplayPath path)
    {
        var white = new DisplayConfigSdrWhiteLevel
        {
            Header = Header(
                DisplayConfigDeviceInfoType.GetSdrWhiteLevel,
                path.Target.AdapterId,
                path.Target.Id,
                Marshal.SizeOf<DisplayConfigSdrWhiteLevel>()),
        };
        int result = Win32DisplayNativeMethods.DisplayConfigGetSdrWhiteLevel(ref white);
        if (result != Win32DisplayNativeMethods.ErrorSuccess)
        {
            return (
                null,
                DisplayContract.CanonicalNominalWhiteNits,
                $"DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL failed with {ErrorCode(result)}; " +
                "using the 80-nit scRGB default.");
        }
        if (white.SdrWhiteLevel == 0)
        {
            return (
                null,
                DisplayContract.CanonicalNominalWhiteNits,
                "DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL returned zero; " +
                "using the 80-nit scRGB default.");
        }
        return (white.SdrWhiteLevel, white.SdrWhiteLevel / 1000f * DisplayContract.CanonicalNominalWhiteNits, null);
    }

    private (MonitorProfileData? Profile, string? Failure) ReadMonitorProfile(
        ActiveDisplayPath path)
    {
        int scopeResult;
        WcsProfileManagementScope scope;
        try
        {
            scopeResult = Win32DisplayNativeMethods.ColorProfileGetDisplayUserScope(
                path.Source.AdapterId, path.Source.Id, out scope);
        }
        catch (EntryPointNotFoundException ex)
        {
            return (null, $"Modern Windows display-profile APIs are unavailable: {ex.Message}");
        }
        if (scopeResult < 0)
            return (null, $"ColorProfileGetDisplayUserScope failed with {HResult(scopeResult)}.");

        int profileResult;
        nint profileNamePointer;
        try
        {
            profileResult = Win32DisplayNativeMethods.ColorProfileGetDisplayDefault(
                scope,
                path.Source.AdapterId,
                path.Source.Id,
                Win32DisplayNativeMethods.ColorProfileTypeIcc,
                Win32DisplayNativeMethods.ColorProfileSubtypeStandardDisplay,
                out profileNamePointer);
        }
        catch (EntryPointNotFoundException ex)
        {
            return (null, $"ColorProfileGetDisplayDefault is unavailable: {ex.Message}");
        }
        if (profileResult < 0 || profileNamePointer == nint.Zero)
            return (null, $"ColorProfileGetDisplayDefault failed with {HResult(profileResult)}.");

        string? profileName;
        try
        {
            profileName = Marshal.PtrToStringUni(profileNamePointer);
        }
        finally
        {
            Win32DisplayNativeMethods.LocalFree(profileNamePointer);
        }
        if (string.IsNullOrWhiteSpace(profileName))
            return (null, "ColorProfileGetDisplayDefault returned an empty profile name.");

        try
        {
            string profilePath = ResolveProfilePath(profileName);
            string scopeName = scope.ToString();

            // Every probe used to re-read the whole ICC and re-hash it. A probe happens on each
            // refresh, refreshes are driven by WM_MOVE/WM_WINDOWPOSCHANGED, and the drain that
            // runs them is on the UI thread — so dragging the window used to pay one file read,
            // one array copy and one SHA-256 of the entire profile per dispatcher turn. That is
            // nothing for the 3 KB stock sRGB profile and real work for a calibrated one, which
            // is routinely megabytes.
            //
            // The cache key is the identity Windows itself would change: the resolved path, the
            // scope, and the file's length and last-write time. The stamp is taken BEFORE the
            // read, so a profile rewritten while we read it lands under the old stamp and the
            // next probe re-reads rather than serving the torn copy forever.
            var info = new FileInfo(profilePath);
            long length = info.Length;
            DateTime writtenUtc = info.LastWriteTimeUtc;

            lock (_monitorProfileGate)
            {
                if (_cachedMonitorProfile is { } cached &&
                    _cachedMonitorProfileLength == length &&
                    _cachedMonitorProfileWrittenUtc == writtenUtc &&
                    string.Equals(_cachedMonitorProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_cachedMonitorProfileScope, scopeName, StringComparison.Ordinal))
                {
                    return (cached, null);
                }
            }

            byte[] bytes = File.ReadAllBytes(profilePath);
            if (bytes.Length == 0) return (null, $"Active monitor profile is empty: {profilePath}.");
            string fileName = Path.GetFileName(profilePath);
            var profile = new MonitorProfileData(
                bytes,
                Path.GetFileNameWithoutExtension(fileName),
                fileName,
                scopeName);

            lock (_monitorProfileGate)
            {
                _cachedMonitorProfile = profile;
                _cachedMonitorProfilePath = profilePath;
                _cachedMonitorProfileScope = scopeName;
                _cachedMonitorProfileLength = length;
                _cachedMonitorProfileWrittenUtc = writtenUtc;
            }

            return (profile, null);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, $"Could not read exact active monitor ICC bytes: {ex.Message}");
        }
    }

    private static string ResolveProfilePath(string profileName)
    {
        if (Path.IsPathFullyQualified(profileName)) return Path.GetFullPath(profileName);

        uint size = 0;
        Win32DisplayNativeMethods.GetColorDirectory(null, null, ref size);
        if (size == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetColorDirectoryW did not return a size.");
        if (size > 32_768) throw new InvalidOperationException("GetColorDirectoryW returned an unreasonable size.");

        var directory = new StringBuilder(checked((int)size));
        if (!Win32DisplayNativeMethods.GetColorDirectory(null, directory, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetColorDirectoryW failed.");
        return Path.GetFullPath(Path.Combine(directory.ToString(), profileName));
    }

    private static string BuildStableDisplayId(ActiveDisplayPath path)
    {
        string physical = string.IsNullOrWhiteSpace(path.MonitorDevicePath)
            ? path.GdiDeviceName
            : path.MonitorDevicePath;
        physical = physical.Trim().ToUpperInvariant();
        return $"win32:{physical}|adapter:{unchecked((uint)path.Target.AdapterId.HighPart):X8}:" +
               $"{path.Target.AdapterId.LowPart:X8}|target:{path.Target.Id}";
    }

    private static DisplayConfigDeviceInfoHeader Header(
        DisplayConfigDeviceInfoType type,
        Win32Luid adapterId,
        uint id,
        int size) => new()
        {
            Type = type,
            Size = checked((uint)size),
            AdapterId = adapterId,
            Id = id,
        };

    private static bool HasFlag(uint value, int bit) => (value & (1u << bit)) != 0;

    private static string ColorEncodingName(DisplayConfigColorEncoding encoding) => encoding switch
    {
        DisplayConfigColorEncoding.Rgb => "RGB",
        DisplayConfigColorEncoding.YCbCr444 => "YCbCr 4:4:4",
        DisplayConfigColorEncoding.YCbCr422 => "YCbCr 4:2:2",
        DisplayConfigColorEncoding.YCbCr420 => "YCbCr 4:2:0",
        DisplayConfigColorEncoding.Intensity => "Intensity",
        _ => $"Unknown ({(uint)encoding})",
    };

    private static Exception DisplayConfigException(string operation, int result) =>
        new Win32Exception(result, $"{operation} failed with {ErrorCode(result)}.");

    private static string ErrorCode(int value) => $"{value} (0x{unchecked((uint)value):X8})";
    private static string HResult(int value) => $"0x{unchecked((uint)value):X8}";
    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ActiveDisplayPath(
        DisplayConfigPathSourceInfo Source,
        DisplayConfigPathTargetInfo Target,
        string GdiDeviceName,
        string MonitorDevicePath,
        string MonitorFriendlyName);
}
