namespace OpenRevelare.Gui.Controls;

[Flags]
internal enum WindowsPreviewDisplayRefreshKind
{
    None = 0,
    Refresh = 1,
    Metrics = 2,
}

/// <summary>
/// Pure classification for top-level Win32 messages that can invalidate a display contract.
/// The bridge observes the real Avalonia top-level HWND because broadcast setting/display
/// messages are not guaranteed to reach the NativeControlHost child.
/// </summary>
internal static class WindowsPreviewDisplayMessages
{
    internal const uint WmMove = 0x0003;
    internal const uint WmSettingChange = 0x001A;
    internal const uint WmWindowPosChanged = 0x0047;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmDpiChanged = 0x02E0;

    internal static bool TryClassify(
        uint message,
        out WindowsPreviewDisplayRefreshKind refreshKind)
    {
        switch (message)
        {
            case WmMove:
            case WmSettingChange:
            case WmDisplayChange:
                refreshKind = WindowsPreviewDisplayRefreshKind.Refresh;
                return true;

            case WmWindowPosChanged:
            case WmDpiChanged:
                refreshKind =
                    WindowsPreviewDisplayRefreshKind.Refresh |
                    WindowsPreviewDisplayRefreshKind.Metrics;
                return true;

            default:
                refreshKind = WindowsPreviewDisplayRefreshKind.None;
                return false;
        }
    }
}

internal interface IWindowsPreviewWindowSubclassNative
{
    nint InstallWindowProcedure(nint hwnd, WindowsPreviewWindowProcedure procedure);
    void RestoreWindowProcedure(
        nint hwnd,
        WindowsPreviewWindowProcedure installedProcedure,
        nint previousProcedure);
    nint CallPreviousWindowProcedure(
        nint previousProcedure,
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);
    nint DefWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
}

/// <summary>
/// Lifetime-paired subclass of the real top-level HWND. Avalonia's previous WndProc always runs
/// first; refresh work is only queued after it has applied a DPI/position message. Nothing can
/// unwind through the native callback.
/// </summary>
internal sealed class WindowsPreviewDisplayChangeBridge : IDisposable
{
    private static readonly object FailedRestoreGate = new();
    private static readonly List<WindowsPreviewWindowProcedure> FailedRestoreRoots = [];

    private readonly IWindowsPreviewWindowSubclassNative _native;
    private readonly nint _topLevelHwnd;
    private readonly Action<WindowsPreviewDisplayRefreshKind> _requestRefresh;
    private readonly WindowsPreviewWindowProcedure _windowProcedure;
    private readonly nint _previousProcedure;
    private bool _installed;

    private WindowsPreviewDisplayChangeBridge(
        IWindowsPreviewWindowSubclassNative native,
        nint topLevelHwnd,
        Action<WindowsPreviewDisplayRefreshKind> requestRefresh)
    {
        _native = native;
        _topLevelHwnd = topLevelHwnd;
        _requestRefresh = requestRefresh;
        _windowProcedure = WindowProcedure;
        _previousProcedure = native.InstallWindowProcedure(topLevelHwnd, _windowProcedure);
        if (_previousProcedure == nint.Zero)
        {
            throw new InvalidOperationException(
                "Avalonia top-level HWND returned a null previous window procedure.");
        }
        _installed = true;
    }

    internal bool IsInstalled => _installed;

    internal static WindowsPreviewDisplayChangeBridge Install(
        nint topLevelHwnd,
        Action<WindowsPreviewDisplayRefreshKind> requestRefresh)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The display-change bridge is Windows-only.");
        return Install(
            Win32WindowsPreviewInputNative.Instance,
            topLevelHwnd,
            requestRefresh);
    }

    internal static WindowsPreviewDisplayChangeBridge Install(
        IWindowsPreviewWindowSubclassNative native,
        nint topLevelHwnd,
        Action<WindowsPreviewDisplayRefreshKind> requestRefresh)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(requestRefresh);
        if (topLevelHwnd == nint.Zero)
            throw new ArgumentException("Top-level HWND is null.", nameof(topLevelHwnd));
        return new WindowsPreviewDisplayChangeBridge(native, topLevelHwnd, requestRefresh);
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        try
        {
            _native.RestoreWindowProcedure(
                _topLevelHwnd,
                _windowProcedure,
                _previousProcedure);
        }
        catch
        {
            // A later subclass may still call this delegate. Keep it rooted if exact-chain
            // restoration fails rather than permitting a native callback into collected code.
            lock (FailedRestoreGate) FailedRestoreRoots.Add(_windowProcedure);
            throw;
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        nint result = CallPreviousOrDefault(hwnd, message, wParam, lParam);

        try
        {
            if (_installed &&
                WindowsPreviewDisplayMessages.TryClassify(message, out var refreshKind))
            {
                _requestRefresh(refreshKind);
            }
        }
        catch
        {
            // The old WndProc result remains authoritative. A refresh callback failure must not
            // cross the unmanaged WndProc boundary or prevent Avalonia from handling the message.
        }

        return result;
    }

    private nint CallPreviousOrDefault(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return _native.CallPreviousWindowProcedure(
                _previousProcedure,
                hwnd,
                message,
                wParam,
                lParam);
        }
        catch
        {
            try
            {
                return _native.DefWindowProcedure(hwnd, message, wParam, lParam);
            }
            catch
            {
                return nint.Zero;
            }
        }
    }
}
