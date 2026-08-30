using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenRevelare.Gui.Controls;

internal enum WindowsPreviewInputCoordinateSpace
{
    ContainerClient,
    Screen,
}

internal readonly record struct WindowsPreviewInputPoint(int X, int Y);

/// <summary>Pure Win32 message classification and signed 16-bit LPARAM coordinate helpers.</summary>
internal static class WindowsPreviewInputMessages
{
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLeftButtonDown = 0x0201;
    internal const uint WmLeftButtonUp = 0x0202;
    internal const uint WmLeftButtonDoubleClick = 0x0203;
    internal const uint WmRightButtonDown = 0x0204;
    internal const uint WmRightButtonUp = 0x0205;
    internal const uint WmRightButtonDoubleClick = 0x0206;
    internal const uint WmMiddleButtonDown = 0x0207;
    internal const uint WmMiddleButtonUp = 0x0208;
    internal const uint WmMiddleButtonDoubleClick = 0x0209;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmXButtonDoubleClick = 0x020D;
    internal const uint WmMouseHorizontalWheel = 0x020E;

    internal static bool TryClassify(
        uint message,
        out WindowsPreviewInputCoordinateSpace coordinateSpace)
    {
        switch (message)
        {
            case WmMouseMove:
            case WmLeftButtonDown:
            case WmLeftButtonUp:
            case WmLeftButtonDoubleClick:
            case WmRightButtonDown:
            case WmRightButtonUp:
            case WmRightButtonDoubleClick:
            case WmMiddleButtonDown:
            case WmMiddleButtonUp:
            case WmMiddleButtonDoubleClick:
            case WmXButtonDown:
            case WmXButtonUp:
            case WmXButtonDoubleClick:
                coordinateSpace = WindowsPreviewInputCoordinateSpace.ContainerClient;
                return true;

            case WmMouseWheel:
            case WmMouseHorizontalWheel:
            case WmContextMenu:
                // Wheel LPARAM and WM_CONTEXTMENU LPARAM are already screen coordinates.
                // Keyboard-invoked context menus use -1 and must also pass through unchanged.
                coordinateSpace = WindowsPreviewInputCoordinateSpace.Screen;
                return true;

            default:
                // WM_POINTER is deliberately excluded: forwarding its pointer id to another HWND
                // without retargeting the underlying pointer state is not a correct bridge.
                coordinateSpace = default;
                return false;
        }
    }

    internal static WindowsPreviewInputPoint DecodeClientPoint(nint lParam)
    {
        long raw = lParam.ToInt64();
        return new WindowsPreviewInputPoint(
            unchecked((short)(raw & 0xFFFF)),
            unchecked((short)((raw >> 16) & 0xFFFF)));
    }

    internal static bool TryEncodeClientPoint(WindowsPreviewInputPoint point, out nint lParam)
    {
        if (point.X is < short.MinValue or > short.MaxValue ||
            point.Y is < short.MinValue or > short.MaxValue)
        {
            lParam = nint.Zero;
            return false;
        }

        uint packed = (uint)unchecked((ushort)(short)point.X) |
            ((uint)unchecked((ushort)(short)point.Y) << 16);
        lParam = unchecked((nint)(long)packed);
        return true;
    }
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint WindowsPreviewWindowProcedure(
    nint hwnd,
    uint message,
    nuint wParam,
    nint lParam);

internal interface IWindowsPreviewInputNative : IWindowsPreviewWindowSubclassNative
{
    bool TryMapClientPoint(
        nint sourceHwnd,
        nint destinationHwnd,
        WindowsPreviewInputPoint source,
        out WindowsPreviewInputPoint destination);
    nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}

/// <summary>
/// Pairs one NativeControlHost container subclass with its exact previous WndProc and forwards
/// pointer messages to Avalonia's real top-level HWND. The delegate is held strongly for the
/// complete installed lifetime.
/// </summary>
internal sealed class WindowsPreviewInputBridge : IDisposable
{
    private static readonly object FailedRestoreGate = new();
    private static readonly List<WindowsPreviewWindowProcedure> FailedRestoreRoots = [];

    private readonly IWindowsPreviewInputNative _native;
    private readonly nint _containerHwnd;
    private readonly nint _topLevelHwnd;
    private readonly WindowsPreviewWindowProcedure _windowProcedure;
    private readonly nint _previousProcedure;
    private volatile bool _installed;

    private WindowsPreviewInputBridge(
        IWindowsPreviewInputNative native,
        nint containerHwnd,
        nint topLevelHwnd)
    {
        _native = native;
        _containerHwnd = containerHwnd;
        _topLevelHwnd = topLevelHwnd;
        _windowProcedure = WindowProcedure;
        _previousProcedure = native.InstallWindowProcedure(containerHwnd, _windowProcedure);
        if (_previousProcedure == nint.Zero)
        {
            throw new InvalidOperationException(
                "NativeControlHost container returned a null previous window procedure.");
        }
        _installed = true;
    }

    internal bool IsInstalled => _installed;

    internal static WindowsPreviewInputBridge Install(nint containerHwnd, nint topLevelHwnd)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The preview input bridge is Windows-only.");
        return Install(Win32WindowsPreviewInputNative.Instance, containerHwnd, topLevelHwnd);
    }

    internal static WindowsPreviewInputBridge Install(
        IWindowsPreviewInputNative native,
        nint containerHwnd,
        nint topLevelHwnd)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (containerHwnd == nint.Zero)
            throw new ArgumentException("Container HWND is null.", nameof(containerHwnd));
        if (topLevelHwnd == nint.Zero)
            throw new ArgumentException("Top-level HWND is null.", nameof(topLevelHwnd));
        return new WindowsPreviewInputBridge(native, containerHwnd, topLevelHwnd);
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        try
        {
            _native.RestoreWindowProcedure(
                _containerHwnd,
                _windowProcedure,
                _previousProcedure);
        }
        catch
        {
            // A failed restore means native code may still call this delegate. Leaking one root in
            // this catastrophic path is safer than allowing a later callback into collected code.
            lock (FailedRestoreGate) FailedRestoreRoots.Add(_windowProcedure);
            throw;
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        // Nothing may unwind through a native WndProc. A failed map/send simply leaves the
        // container's original procedure as the fail-closed receiver for that message.
        try
        {
            if (_installed &&
                WindowsPreviewInputMessages.TryClassify(message, out var coordinates))
            {
                nint forwardedLParam = lParam;
                if (coordinates == WindowsPreviewInputCoordinateSpace.ContainerClient)
                {
                    WindowsPreviewInputPoint source =
                        WindowsPreviewInputMessages.DecodeClientPoint(lParam);
                    if (!_native.TryMapClientPoint(
                            _containerHwnd,
                            _topLevelHwnd,
                            source,
                            out WindowsPreviewInputPoint destination) ||
                        !WindowsPreviewInputMessages.TryEncodeClientPoint(
                            destination,
                            out forwardedLParam))
                    {
                        return CallPreviousOrDefault(hwnd, message, wParam, lParam);
                    }
                }

                nuint forwardedWParam = message == WindowsPreviewInputMessages.WmContextMenu
                    ? unchecked((nuint)_topLevelHwnd)
                    : wParam;
                // A successfully retargeted message is fully handled by the top-level WndProc.
                // Calling DefWindowProc for the child as well can synthesize a duplicate
                // WM_CONTEXTMENU from the same right-button release.
                return _native.SendMessage(
                    _topLevelHwnd,
                    message,
                    forwardedWParam,
                    forwardedLParam);
            }
        }
        catch
        {
            // Fall through to the original procedure. The host never claims successful routing
            // until installation itself has succeeded; individual native failures do not crash.
        }

        return CallPreviousOrDefault(hwnd, message, wParam, lParam);
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

internal sealed class Win32WindowsPreviewInputNative : IWindowsPreviewInputNative
{
    private const int GwlpWndProc = -4;

    internal static Win32WindowsPreviewInputNative Instance { get; } = new();

    private Win32WindowsPreviewInputNative()
    {
    }

    public nint InstallWindowProcedure(nint hwnd, WindowsPreviewWindowProcedure procedure)
    {
        nint installed = Marshal.GetFunctionPointerForDelegate(procedure);
        return SetWindowProcedure(hwnd, installed, "install");
    }

    public void RestoreWindowProcedure(
        nint hwnd,
        WindowsPreviewWindowProcedure installedProcedure,
        nint previousProcedure)
    {
        if (previousProcedure == nint.Zero)
            throw new InvalidOperationException("Cannot restore a null previous window procedure.");

        nint installed = Marshal.GetFunctionPointerForDelegate(installedProcedure);
        nint replaced = SetWindowProcedure(hwnd, previousProcedure, "restore");
        if (replaced == installed) return;

        // Another component changed the subclass chain after us. Put that current procedure back
        // rather than silently removing it, then keep our delegate rooted via the caller's failure
        // path because that later subclass may still call us as its previous procedure.
        try
        {
            _ = SetWindowProcedure(hwnd, replaced, "roll back changed subclass chain");
        }
        catch
        {
            // The primary mismatch below remains the useful failure; the bridge will quarantine
            // its delegate regardless.
        }
        throw new InvalidOperationException(
            "Target HWND WndProc changed after preview bridge installation; " +
            "the bridge refused to remove another subclass.");
    }

    public bool TryMapClientPoint(
        nint sourceHwnd,
        nint destinationHwnd,
        WindowsPreviewInputPoint source,
        out WindowsPreviewInputPoint destination)
    {
        var point = new NativePoint(source.X, source.Y);
        if (!ClientToScreen(sourceHwnd, ref point) ||
            !ScreenToClient(destinationHwnd, ref point))
        {
            destination = default;
            return false;
        }

        destination = new WindowsPreviewInputPoint(point.X, point.Y);
        return true;
    }

    public nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam) =>
        SendMessageW(hwnd, message, wParam, lParam);

    public nint CallPreviousWindowProcedure(
        nint previousProcedure,
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam)
    {
        if (previousProcedure == nint.Zero)
            throw new InvalidOperationException("Previous window procedure is null.");
        return CallWindowProcW(previousProcedure, hwnd, message, wParam, lParam);
    }

    public nint DefWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam) =>
        DefWindowProcW(hwnd, message, wParam, lParam);

    private static nint SetWindowProcedure(nint hwnd, nint procedure, string operation)
    {
        SetLastErrorNative(0);
        nint previous = IntPtr.Size == 8
            ? SetWindowLongPtrW(hwnd, GwlpWndProc, procedure)
            : (nint)SetWindowLongW(hwnd, GwlpWndProc, procedure.ToInt32());
        if (previous != nint.Zero) return previous;

        int error = Marshal.GetLastWin32Error();
        if (error != 0)
        {
            throw new Win32Exception(
                error,
                $"SetWindowLongPtrW could not {operation} the preview window subclass.");
        }
        throw new InvalidOperationException(
            $"SetWindowLongPtrW returned a null previous WndProc while attempting to {operation} " +
            "the preview window subclass.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;

        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError", ExactSpelling = true)]
    private static extern void SetLastErrorNative(uint errorCode);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLongW(nint hwnd, int index, int newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern nint CallWindowProcW(
        nint previousProcedure,
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
