using OpenRevelare.Gui.Controls;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class WindowsPreviewInputBridgeTests
{
    [Fact]
    public void Classifier_has_an_explicit_mouse_whitelist_and_coordinate_policy()
    {
        uint[] clientMessages =
        {
            WindowsPreviewInputMessages.WmMouseMove,
            WindowsPreviewInputMessages.WmLeftButtonDown,
            WindowsPreviewInputMessages.WmLeftButtonUp,
            WindowsPreviewInputMessages.WmLeftButtonDoubleClick,
            WindowsPreviewInputMessages.WmRightButtonDown,
            WindowsPreviewInputMessages.WmRightButtonUp,
            WindowsPreviewInputMessages.WmRightButtonDoubleClick,
            WindowsPreviewInputMessages.WmMiddleButtonDown,
            WindowsPreviewInputMessages.WmMiddleButtonUp,
            WindowsPreviewInputMessages.WmMiddleButtonDoubleClick,
            WindowsPreviewInputMessages.WmXButtonDown,
            WindowsPreviewInputMessages.WmXButtonUp,
            WindowsPreviewInputMessages.WmXButtonDoubleClick,
        };
        foreach (uint message in clientMessages)
        {
            Assert.True(WindowsPreviewInputMessages.TryClassify(message, out var coordinates));
            Assert.Equal(WindowsPreviewInputCoordinateSpace.ContainerClient, coordinates);
        }

        uint[] screenMessages =
        {
            WindowsPreviewInputMessages.WmMouseWheel,
            WindowsPreviewInputMessages.WmMouseHorizontalWheel,
            WindowsPreviewInputMessages.WmContextMenu,
        };
        foreach (uint message in screenMessages)
        {
            Assert.True(WindowsPreviewInputMessages.TryClassify(message, out var coordinates));
            Assert.Equal(WindowsPreviewInputCoordinateSpace.Screen, coordinates);
        }

        Assert.False(WindowsPreviewInputMessages.TryClassify(0x0100, out _)); // WM_KEYDOWN
        Assert.False(WindowsPreviewInputMessages.TryClassify(0x0246, out _)); // WM_POINTERDOWN
        Assert.False(WindowsPreviewInputMessages.TryClassify(0x000F, out _)); // WM_PAINT
    }

    [Fact]
    public void Client_LPARAM_round_trips_signed_coordinates_and_rejects_unrepresentable_points()
    {
        var point = new WindowsPreviewInputPoint(-1234, 2345);

        Assert.True(WindowsPreviewInputMessages.TryEncodeClientPoint(point, out nint packed));
        Assert.Equal(point, WindowsPreviewInputMessages.DecodeClientPoint(packed));
        Assert.False(WindowsPreviewInputMessages.TryEncodeClientPoint(
            new WindowsPreviewInputPoint(short.MaxValue + 1, 0),
            out _));
        Assert.False(WindowsPreviewInputMessages.TryEncodeClientPoint(
            new WindowsPreviewInputPoint(0, short.MinValue - 1),
            out _));
    }

    [Fact]
    public void Bridge_maps_client_messages_preserves_screen_messages_and_restores_once()
    {
        var native = new FakeInputNative
        {
            Map = point => new WindowsPreviewInputPoint(point.X + 100, point.Y - 20),
        };
        WindowsPreviewInputBridge bridge = WindowsPreviewInputBridge.Install(
            native,
            containerHwnd: (nint)0x101,
            topLevelHwnd: (nint)0x202);

        Assert.True(bridge.IsInstalled);
        Assert.Equal(1, native.InstallCalls);
        nint moveCoordinates = Pack(new WindowsPreviewInputPoint(-5, 7));
        nint result = native.Invoke(
            WindowsPreviewInputMessages.WmMouseMove,
            wParam: (nuint)0x0005,
            lParam: moveCoordinates);

        Assert.Equal(native.ForwardedResult, result);
        Assert.Equal(1, native.MapCalls);
        Assert.Equal(new WindowsPreviewInputPoint(-5, 7), native.LastMapSource);
        Assert.Equal((nint)0x101, native.LastMapSourceHwnd);
        Assert.Equal((nint)0x202, native.LastMapDestinationHwnd);
        ForwardedMessage move = Assert.Single(native.Forwarded);
        Assert.Equal((nint)0x202, move.Hwnd);
        Assert.Equal(WindowsPreviewInputMessages.WmMouseMove, move.Message);
        Assert.Equal((nuint)0x0005, move.WParam);
        Assert.Equal(
            new WindowsPreviewInputPoint(95, -13),
            WindowsPreviewInputMessages.DecodeClientPoint(move.LParam));
        Assert.Empty(native.PreviousCalls);

        native.Forwarded.Clear();
        nint wheelScreenCoordinates = Pack(new WindowsPreviewInputPoint(-1000, 300));
        native.Invoke(
            WindowsPreviewInputMessages.WmMouseWheel,
            wParam: (nuint)0x00780008,
            lParam: wheelScreenCoordinates);
        ForwardedMessage wheel = Assert.Single(native.Forwarded);
        Assert.Equal(wheelScreenCoordinates, wheel.LParam);
        Assert.Equal(1, native.MapCalls);

        native.Forwarded.Clear();
        native.Invoke(
            WindowsPreviewInputMessages.WmContextMenu,
            wParam: 0,
            lParam: (nint)(-1));
        ForwardedMessage context = Assert.Single(native.Forwarded);
        Assert.Equal((nint)(-1), context.LParam);
        Assert.Equal(unchecked((nuint)(nint)0x202), context.WParam);

        native.Forwarded.Clear();
        native.Invoke(message: 0x0100, wParam: 0, lParam: nint.Zero); // WM_KEYDOWN
        Assert.Empty(native.Forwarded);

        bridge.Dispose();
        bridge.Dispose();
        Assert.False(bridge.IsInstalled);
        Assert.Equal(1, native.RestoreCalls);
        Assert.Equal((nint)0x101, native.RestoredHwnd);
        Assert.Equal(native.PreviousProcedure, native.RestoredPreviousProcedure);
        Assert.Same(native.LastInstalledProcedure, native.RestoredInstalledProcedure);

        // Model an already-entered/stale native callback after restoration. The strongly held
        // test delegate remains callable, but the bridge must fail closed to the prior WndProc.
        native.Forwarded.Clear();
        int previousCallCount = native.PreviousCalls.Count;
        nint staleResult = native.Invoke(
            WindowsPreviewInputMessages.WmMouseMove,
            wParam: 0,
            lParam: moveCoordinates);
        Assert.Equal(native.PreviousResult, staleResult);
        Assert.Empty(native.Forwarded);
        Assert.Equal(previousCallCount + 1, native.PreviousCalls.Count);
        Assert.Equal((nint)0x101, native.PreviousCalls[^1].Hwnd);
    }

    [Fact]
    public void Map_or_send_failure_falls_back_to_the_previous_WndProc_without_unwinding()
    {
        var native = new FakeInputNative { MapSucceeds = false };
        using WindowsPreviewInputBridge bridge = WindowsPreviewInputBridge.Install(
            native,
            containerHwnd: (nint)0x301,
            topLevelHwnd: (nint)0x302);

        nint input = Pack(new WindowsPreviewInputPoint(4, 8));
        nint mapFailureResult = native.Invoke(
            WindowsPreviewInputMessages.WmLeftButtonDown,
            wParam: 1,
            lParam: input);
        Assert.Equal(native.PreviousResult, mapFailureResult);
        Assert.Empty(native.Forwarded);
        Assert.Single(native.PreviousCalls);

        native.MapSucceeds = true;
        native.ThrowOnSend = true;
        nint sendFailureResult = native.Invoke(
            WindowsPreviewInputMessages.WmMouseMove,
            wParam: 0,
            lParam: input);
        Assert.Equal(native.PreviousResult, sendFailureResult);
        Assert.Equal(2, native.PreviousCalls.Count);
    }

    [Fact]
    public void Previous_WndProc_failure_uses_DefWindowProc_as_the_native_callback_safety_net()
    {
        var native = new FakeInputNative { ThrowOnCallPrevious = true };
        using WindowsPreviewInputBridge bridge = WindowsPreviewInputBridge.Install(
            native,
            containerHwnd: (nint)0x401,
            topLevelHwnd: (nint)0x402);

        nint result = native.Invoke(message: 0x000F, wParam: 0, lParam: nint.Zero);

        Assert.Equal(native.DefWindowResult, result);
        Assert.Equal(1, native.DefWindowCalls);
    }

    [Fact]
    public void Installation_failure_publishes_no_bridge()
    {
        var native = new FakeInputNative { ThrowOnInstall = true };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WindowsPreviewInputBridge.Install(
                native,
                containerHwnd: (nint)0x501,
                topLevelHwnd: (nint)0x502));

        Assert.Contains("injected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, native.InstallCalls);
        Assert.Equal(0, native.RestoreCalls);
    }

    private static nint Pack(WindowsPreviewInputPoint point)
    {
        Assert.True(WindowsPreviewInputMessages.TryEncodeClientPoint(point, out nint packed));
        return packed;
    }

    private readonly record struct ForwardedMessage(
        nint Hwnd,
        uint Message,
        nuint WParam,
        nint LParam);

    private sealed class FakeInputNative : IWindowsPreviewInputNative
    {
        internal nint PreviousProcedure { get; set; } = (nint)0xBEEF;
        internal nint PreviousResult { get; set; } = (nint)0xCAFE;
        internal nint ForwardedResult { get; set; } = (nint)0xF00D;
        internal nint DefWindowResult { get; set; } = (nint)0xD00D;
        internal bool ThrowOnInstall { get; set; }
        internal bool ThrowOnSend { get; set; }
        internal bool ThrowOnCallPrevious { get; set; }
        internal bool MapSucceeds { get; set; } = true;
        internal Func<WindowsPreviewInputPoint, WindowsPreviewInputPoint> Map { get; set; } =
            static point => point;
        internal int InstallCalls { get; private set; }
        internal int RestoreCalls { get; private set; }
        internal int MapCalls { get; private set; }
        internal int DefWindowCalls { get; private set; }
        internal nint LastMapSourceHwnd { get; private set; }
        internal nint LastMapDestinationHwnd { get; private set; }
        internal WindowsPreviewInputPoint LastMapSource { get; private set; }
        internal nint RestoredHwnd { get; private set; }
        internal nint InstalledHwnd { get; private set; }
        internal nint RestoredPreviousProcedure { get; private set; }
        internal WindowsPreviewWindowProcedure? LastInstalledProcedure { get; private set; }
        internal WindowsPreviewWindowProcedure? RestoredInstalledProcedure { get; private set; }
        internal List<ForwardedMessage> Forwarded { get; } = [];
        internal List<ForwardedMessage> PreviousCalls { get; } = [];

        public nint InstallWindowProcedure(nint hwnd, WindowsPreviewWindowProcedure procedure)
        {
            InstallCalls++;
            if (ThrowOnInstall) throw new InvalidOperationException("Injected subclass install failure.");
            InstalledHwnd = hwnd;
            LastInstalledProcedure = procedure;
            return PreviousProcedure;
        }

        public void RestoreWindowProcedure(
            nint hwnd,
            WindowsPreviewWindowProcedure installedProcedure,
            nint previousProcedure)
        {
            RestoreCalls++;
            RestoredHwnd = hwnd;
            RestoredInstalledProcedure = installedProcedure;
            RestoredPreviousProcedure = previousProcedure;
        }

        public bool TryMapClientPoint(
            nint sourceHwnd,
            nint destinationHwnd,
            WindowsPreviewInputPoint source,
            out WindowsPreviewInputPoint destination)
        {
            MapCalls++;
            LastMapSourceHwnd = sourceHwnd;
            LastMapDestinationHwnd = destinationHwnd;
            LastMapSource = source;
            destination = Map(source);
            return MapSucceeds;
        }

        public nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            if (ThrowOnSend) throw new InvalidOperationException("Injected send failure.");
            Forwarded.Add(new ForwardedMessage(hwnd, message, wParam, lParam));
            return ForwardedResult;
        }

        public nint CallPreviousWindowProcedure(
            nint previousProcedure,
            nint hwnd,
            uint message,
            nuint wParam,
            nint lParam)
        {
            if (ThrowOnCallPrevious) throw new InvalidOperationException("Injected previous WndProc failure.");
            PreviousCalls.Add(new ForwardedMessage(hwnd, message, wParam, lParam));
            return PreviousResult;
        }

        public nint DefWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            DefWindowCalls++;
            return DefWindowResult;
        }

        internal nint Invoke(uint message, nuint wParam, nint lParam)
        {
            WindowsPreviewWindowProcedure procedure = LastInstalledProcedure ??
                throw new InvalidOperationException("No test WndProc is installed.");
            return procedure(InstalledHwnd, message, wParam, lParam);
        }
    }
}
